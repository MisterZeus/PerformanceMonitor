<#
.SYNOPSIS
Upgrades an installed PerformanceMonitor Darling in place from a newer build, keeping a BOUNDED number of
rollback backups — or prunes the ones an earlier deploy left behind (-PruneOnly).

.DESCRIPTION
This is the supported version of a procedure that had been living in people's heads and in ad-hoc SSM
scripts: stop the service, copy the install tree's root files aside as _rollback_manual_<stamp>, lay the new
build over the top, start the service, verify. Every step of it was already documented somewhere. What was
missing was the step nobody remembered to do by hand, which is deleting the backups from the LAST twenty
deploys — a dogfood box was found carrying 46 of them, 5.48 GB, the oldest three weeks old, and the service
warning about every single one on every single start (#2525).

Retention lives HERE, at deploy time, and not in the service. The service does not delete things it did not
create; this script created every one of these directories, so this script is the only thing entitled to
remove them. It keeps the newest -KeepRollbacks (3 by default) and removes the rest, after the new backup is
made — so a failed upgrade always has something to roll back to, including the copy it just took.

What it does, in order:

  1. Verifies elevation, resolves the install root from the REGISTERED service (or -InstallRoot), and
     refuses if the service is not installed — this upgrades an install, it does not create one. Use
     install-darling.ps1 for that.
  2. Refuses to copy a source that IS the install root. The upgrade would be overwriting this very script
     while PowerShell is reading it, and Expand-Archive dying half-way through leaves the service stopped
     with mixed binaries. Run the copy that came out of the NEW zip instead.
  3. Verifies the source zip's SHA256 (against -Sha256, or a SHA256SUMS.txt sitting beside it). Refuses an
     unverified zip unless you say -SkipHashCheck out loud.
  4. Names any process running out of the install tree and STOPS — it never kills one. The bundled
     PostgreSQL lives under pg-runtime and a blanket kill takes the store down with it; the last time this
     guard fired, what it caught was an operator's own psql.exe sitting in the install directory from a
     diagnostic query.
  5. Stops the service and waits for it to actually be Stopped.
  6. Copies the install root's FILES (not its subdirectories) to _rollback_manual_<stamp>. A re-run inside
     -BackupWindowMinutes reuses the existing backup instead of taking a second one, because a second one
     would be a copy of the half-extracted tree written over your only good copy.
  7. Prunes the rollback backups past -KeepRollbacks, each in its own handler.
  8. Lays the new build over the install root, retrying once on the transient file lock that has bitten this
     step before.
  9. Confirms darling.json is byte-identical to what it was, starts the service, waits for Running, and
     prints the post-install health check — which is a STAGE of the install, not a favour.

Every step is safe to re-run. That is not a nicety: steps 5 through 9 leave the service DOWN if anything
between them fails, so "run it again" has to be the correct advice, and the script says so at the point of
failure rather than leaving you to guess.

.PARAMETER Source
The new build: either the .zip as downloaded, or a folder you already extracted it into. Defaults to the
folder this script is in, which is what you get by extracting the new zip to a staging directory and running
ITS copy of this script.

.PARAMETER InstallRoot
The install directory to upgrade. Defaults to the directory of the registered service's executable, which is
the one place that cannot be wrong about where the service is actually installed.

.PARAMETER Sha256
Expected SHA256 of the source zip. Without it the script looks for SHA256SUMS.txt beside the zip.

.PARAMETER KeepRollbacks
How many rollback backups to keep, newest first, INCLUDING the one this run takes. Three is enough to roll
back a bad deploy; the fourth can only roll back to a version nobody wants.

.PARAMETER BackupWindowMinutes
A backup newer than this is reused rather than replaced, so a re-run after an interrupted upgrade does not
overwrite the good pre-upgrade copy with a copy of the half-upgraded tree.

.PARAMETER PruneOnly
Prune the rollback backups and exit. Nothing is stopped, nothing is copied, and the service keeps running.
This is what an existing box with a backlog needs, and it is the command the service's own layout report
tells operators to run.

.PARAMETER ListRollbacks
Show which backups would be kept and which pruned, and exit. Changes nothing.

.PARAMETER SkipHashCheck
Proceed with a source zip whose SHA256 could not be verified.

.PARAMETER SkipStopGuard
Proceed even though processes are running out of the install tree. Almost always the wrong answer — the
copy will fail on a locked file and leave mixed binaries — but there is no way to be sure from here that
your case is not the exception.
#>
[CmdletBinding()]
param(
    [string]$Source = $PSScriptRoot,
    [string]$InstallRoot,
    [string]$Sha256,
    [ValidateRange(1, 100)]
    [int]$KeepRollbacks = 3,
    [ValidateRange(0, 10080)]
    [int]$BackupWindowMinutes = 60,
    [switch]$PruneOnly,
    [switch]$ListRollbacks,
    [switch]$SkipHashCheck,
    [switch]$SkipStopGuard
)

$ErrorActionPreference = 'Stop'
$serviceName = 'PerformanceMonitor Darling'
$serviceExeName = 'PerformanceMonitor.Darling.Service.exe'
$configName = 'darling.json'

function Fail([string]$message) { Write-Host "ERROR: $message" -ForegroundColor Red; exit 1 }
function Note([string]$message) { Write-Host $message }
function Good([string]$message) { Write-Host $message -ForegroundColor Green }
function Warn([string]$message) { Write-Host "WARNING: $message" -ForegroundColor Yellow }

# ============================ the rollback-backup convention ============================
#
# The C# twin is DarlingRollbackBackups and the two must stay identical. That is not a style preference:
# the service RECOGNISES these directories so it can report the whole set on one line instead of one
# warning each, and this script CREATES and PRUNES them. If the two spellings ever drift apart the service
# goes back to naming 46 directories individually and nobody notices, because each of those lines is
# perfectly true. DarlingDeployRollbackRetentionTests runs the function below against the C# predicate over
# a shared case table to make the drift impossible to ship.

# What a rollback backup is called. Prefix plus at least one more character, matched case-insensitively
# because Windows paths are - a matcher stricter than the filesystem is a matcher that misses a directory
# the operator can see with their own eyes.
#
# No stamp parsing. The backlog this has to recognise was made over months by a procedure that has spelled
# its stamp more than one way, and a rule that only accepted today's spelling would leave yesterday's
# backups unrecognised - which is the entire complaint in #2525. The prefix is a namespace: everything
# inside it belongs to this procedure.
function Test-DarlingRollbackBackupName([string]$name) {
    $prefix = '_rollback_manual_'
    if ([string]::IsNullOrEmpty($name)) { return $false }
    if ($name.Length -le $prefix.Length) { return $false }
    return $name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

# The name for a backup taken now. Seconds are in it because two deploys in one minute is a rehearsal, not
# a hypothetical, and a stamp that collides silently merges two builds into one directory.
function New-DarlingRollbackBackupName([datetime]$whenUtc) {
    return '_rollback_manual_' + $whenUtc.ToString('yyyyMMdd-HHmmss')
}

# Every rollback backup in the install root, NEWEST FIRST. That ordering is a contract - the prune below
# keeps a prefix of this list - so it is produced in exactly one place.
#
# Ordered by LastWriteTimeUtc rather than by the stamps in the names, because the filesystem knows when a
# directory was written and the name only carries whatever spelling the procedure used that month. The name
# is the tiebreak so the ordering is still total when two backups share a timestamp.
#
# Note there is deliberately NO -Filter '_rollback_manual_*' here. Get-ChildItem's filter is handed to the
# filesystem, which matches a directory's Windows 8.3 SHORT name as well as its real one - so a wildcard
# can hand a DELETE a directory whose real name looks nothing like the pattern. DarlingStoreUpgrade guards
# that by re-checking the real name after the wildcard; enumerating everything and testing the real name is
# the same guard with the trap removed, and on a directory holding tens of entries it costs nothing.
function Get-DarlingRollbackBackups([string]$installRoot) {
    if ([string]::IsNullOrWhiteSpace($installRoot)) { return @() }
    if (-not (Test-Path -LiteralPath $installRoot -PathType Container)) { return @() }

    $all = @(Get-ChildItem -LiteralPath $installRoot -Directory -Force -ErrorAction SilentlyContinue)
    $mine = @($all | Where-Object { Test-DarlingRollbackBackupName $_.Name })
    return @($mine | Sort-Object -Property LastWriteTimeUtc, Name -Descending)
}

# The backups past retention, given the newest-first list Get-DarlingRollbackBackups produces.
#
# $keep counts the backup this run just took, so -KeepRollbacks 3 means "this one and the two before it".
# The floor of 1 is not defensive clutter: this function's output is fed straight to a recursive delete in
# an install directory, and the one input that must never be possible is the one that selects everything.
function Select-DarlingRollbackBackupsToPrune($backups, [int]$keep) {
    $ordered = @($backups)
    if ($keep -lt 1) { $keep = 1 }
    if ($ordered.Count -le $keep) { return @() }
    return @($ordered[$keep..($ordered.Count - 1)])
}

# True when the newest backup is recent enough that this run is a RE-RUN of an interrupted upgrade rather
# than a new deploy.
#
# The failure this prevents is specific and expensive. Step 8 dies on a locked DLL, leaving the tree half
# extracted; the operator does the right thing and runs the script again; without this, step 6 backs up the
# HALF-EXTRACTED tree, and now the newest rollback copy - the one anybody would reach for - is a mixture of
# two builds. Reusing the existing backup keeps the copy that was taken while the tree was still coherent.
#
# $nowUtc is a parameter rather than a call to Get-Date so the rule can be tested at a known instant.
#
# The elapsed time has to be non-negative as well as small, and that is not pedantry - it was a live bug
# caught by running this function against a planted tree. A backup whose timestamp is in the FUTURE (a
# clock that stepped backwards, a directory restored from elsewhere with its metadata) produces a negative
# elapsed time, which is less than any window, which reads as "recent" - and the upgrade would then skip
# taking a backup at all. The two ways to be wrong here are not symmetric: an extra backup costs 120 MB
# that the prune reclaims on the next deploy, and a missing one costs the rollback this whole procedure
# exists to provide. So anything the clock cannot vouch for falls through to taking a new backup.
function Test-DarlingRollbackBackupIsRecent($backups, [int]$withinMinutes, [datetime]$nowUtc) {
    $ordered = @($backups)
    if ($ordered.Count -eq 0) { return $false }
    if ($withinMinutes -le 0) { return $false }

    $elapsed = ($nowUtc - $ordered[0].LastWriteTimeUtc).TotalMinutes
    return ($elapsed -ge 0) -and ($elapsed -lt $withinMinutes)
}

# ============================ the install tree ============================

# Where the service is ACTUALLY installed, read from the registered ImagePath rather than guessed from
# where this script happens to be sitting. Returns $null when the service is not installed.
#
# The ImagePath is quoted when it contains spaces and 'C:\PerformanceMonitorDarling' usually does not, so
# both spellings are handled; a path that cannot be parsed returns $null and the caller asks for
# -InstallRoot rather than upgrading a directory it guessed at.
function Get-DarlingInstallRootFromService([string]$name) {
    try {
        $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$name'" -ErrorAction Stop
    }
    catch {
        return $null
    }

    if (-not $service) { return $null }

    $imagePath = $service.PathName
    if ([string]::IsNullOrWhiteSpace($imagePath)) { return $null }

    $imagePath = $imagePath.Trim()
    if ($imagePath.StartsWith('"')) {
        $close = $imagePath.IndexOf('"', 1)
        if ($close -gt 1) { $imagePath = $imagePath.Substring(1, $close - 1) }
    }
    else {
        # An unquoted path with arguments after it: everything up to the .exe is the executable.
        $exe = $imagePath.IndexOf('.exe', [StringComparison]::OrdinalIgnoreCase)
        if ($exe -ge 0) { $imagePath = $imagePath.Substring(0, $exe + 4) }
    }

    try { return [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($imagePath)) }
    catch { return $null }
}

# Every process whose executable lives under $root, so the copy can refuse instead of failing half-way.
#
# It NAMES them. It never kills one, and no version of this script ever should: the bundled PostgreSQL runs
# out of pg-runtime under this very directory, and a sweep that force-kills "everything under the install
# dir" takes the monitoring store down as its first act. Stopping the service stops the store properly;
# anything still holding the tree after that is a person's session, and a person can close it.
function Get-DarlingProcessesUnderPath([string]$root) {
    $hits = @()
    if ([string]::IsNullOrWhiteSpace($root)) { return @($hits) }

    try { $prefix = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\' }
    catch { return @($hits) }

    foreach ($process in @(Get-Process -ErrorAction SilentlyContinue)) {
        $path = $null
        # A process owned by another account throws on .Path rather than returning empty, and an
        # inaccessible process is not evidence of anything - skip it and keep looking.
        try { $path = $process.Path } catch { continue }
        if ([string]::IsNullOrEmpty($path)) { continue }
        if ($path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { $hits += $process }
    }

    return @($hits)
}

# True for a process that stopping the service will take with it: the service's own executable, and
# anything under pg-runtime (the bundled PostgreSQL, which the service starts and stops).
#
# This exists because the stop guard has to run TWICE, and the two runs are asking different questions.
# Before the service is stopped, every install is holding its own tree - the service exe is right there and
# the store's postmaster is under pg-runtime - so an unfiltered check refuses every real upgrade there has
# ever been. Filtering those two out leaves exactly the processes a service stop will NOT clear: an
# operator's own psql.exe, a shell whose working directory is the install folder, a Darling Viewer someone
# left open holding viewer\*.dll. Catching those BEFORE the stop costs nothing but a re-run; catching them
# after costs an outage.
#
# The Viewer is deliberately NOT on this list even though we ship it. It is a separate process that the
# service does not own and stopping the service does not close, so it holds viewer\ exactly as hard as any
# other application would.
# The separator comes from the runtime rather than being typed as '\'. This script only ever RUNS on
# Windows, but this particular predicate decides which processes are EXCUSED from a guard, and a rule that
# excuses things is the one worth being able to test on the machine it was written on. A hardcoded
# backslash makes it verifiable only on the box it already shipped to.
function Test-DarlingProcessStopsWithTheService([string]$processPath, [string]$installRoot) {
    if ([string]::IsNullOrEmpty($processPath)) { return $false }

    $sep = [IO.Path]::DirectorySeparatorChar
    try { $root = [IO.Path]::GetFullPath($installRoot).TrimEnd($sep, [IO.Path]::AltDirectorySeparatorChar) }
    catch { return $false }

    if ($processPath.Equals(($root + $sep + 'PerformanceMonitor.Darling.Service.exe'), [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    # The trailing separator is what keeps pg-runtime-prev - the rescued previous runtime, a real directory
    # in this layout - from matching a pg-runtime prefix test.
    return $processPath.StartsWith(($root + $sep + 'pg-runtime' + $sep), [StringComparison]::OrdinalIgnoreCase)
}

function Get-DarlingDirectoryBytes([string]$path) {
    try {
        $files = @(Get-ChildItem -LiteralPath $path -File -Recurse -Force -ErrorAction SilentlyContinue)
        if ($files.Count -eq 0) { return [long]0 }
        return [long](($files | Measure-Object -Property Length -Sum).Sum)
    }
    catch {
        return [long]0
    }
}

function Format-DarlingBytes([long]$bytes) {
    if ($bytes -ge 1GB) { return ('{0:N2} GB' -f ($bytes / 1GB)) }
    if ($bytes -ge 1MB) { return ('{0:N1} MB' -f ($bytes / 1MB)) }
    if ($bytes -ge 1KB) { return ('{0:N0} KB' -f ($bytes / 1KB)) }
    return "$bytes bytes"
}

# Deletes the selected backups, EACH IN ITS OWN HANDLER.
#
# That is #1775's lesson paid for once already: the store's retained-copy sweep had its failure handling
# outside the loop, so one directory an antivirus scan still held abandoned the sweep for every other
# directory too - and kept abandoning it for as long as the condition lasted, which is how nothing aged out
# at all. One directory that cannot be deleted must cost exactly that directory.
#
# The try wraps ONLY the measure and the delete, and the reporting happens after it on a success flag. That
# is not tidiness. With the write-up inside the try, anything that went wrong while composing a LINE OF TEXT
# landed in the catch and was recorded as a delete failure - so the log said "could not remove X" about a
# directory that was already gone, and the returned failure count disagreed with the disk. A run of this
# function against a planted tree produced exactly that: two directories removed and two failures reported,
# for the same two directories. What the caller does with the answer (exit codes, "re-running is safe") is
# built on those counts, so they have to mean what they say.
function Remove-DarlingRollbackBackups($prunable) {
    $removed = 0
    $reclaimed = [long]0
    $failures = @()

    foreach ($backup in @($prunable)) {
        $bytes = [long]0
        $gone = $false
        $reason = ''

        try {
            $bytes = Get-DarlingDirectoryBytes $backup.FullName
            Remove-Item -LiteralPath $backup.FullName -Recurse -Force -ErrorAction Stop
            $gone = $true
        }
        catch {
            $reason = $_.Exception.Message
        }

        if ($gone) {
            $removed++
            $reclaimed += $bytes
            Note ("  removed {0} ({1})" -f $backup.Name, (Format-DarlingBytes $bytes))
        }
        else {
            $failures += $backup.Name
            Warn ("could not remove {0}: {1}. The other backups were still swept; delete this one by hand when whatever is holding it lets go." -f $backup.Name, $reason)
        }
    }

    return [pscustomobject]@{
        Removed   = $removed
        Reclaimed = $reclaimed
        Failures  = @($failures)
    }
}

# ============================ preamble ============================

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail "Run this from an ELEVATED PowerShell. Stopping the service, writing to the install directory, and starting it again all need it."
}

if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = Get-DarlingInstallRootFromService $serviceName
    if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
        Fail "The '$serviceName' service is not installed (or its ImagePath could not be read), so there is nothing here to upgrade. Install with install-darling.ps1, or pass -InstallRoot to point at the tree you mean."
    }
}

try { $InstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\') }
catch { Fail "'$InstallRoot' is not a path this script can resolve." }

if (-not (Test-Path -LiteralPath $InstallRoot -PathType Container)) {
    Fail "The install directory '$InstallRoot' does not exist."
}

if (-not (Test-Path -LiteralPath (Join-Path $InstallRoot $serviceExeName))) {
    Fail "'$InstallRoot' does not hold $serviceExeName, so it is not a Darling install. Refusing to copy a build over it."
}

Note "Install directory: $InstallRoot"

# ============================ -ListRollbacks / -PruneOnly ============================
#
# Both run against a LIVE service on purpose. Neither touches a binary, and the backups are inert copies
# that nothing has open - so making an operator stop their monitoring host to reclaim 5 GB would be asking
# them to take an outage to clean up after us. -PruneOnly is what the service's own layout report tells
# people to run, and it has to be something they can run at 3pm on a Tuesday.

$backups = Get-DarlingRollbackBackups $InstallRoot

if ($ListRollbacks -or $PruneOnly) {
    if ($backups.Count -eq 0) {
        Good "No rollback backups in $InstallRoot."
        exit 0
    }

    $prunable = Select-DarlingRollbackBackupsToPrune $backups $KeepRollbacks
    $total = [long]0
    foreach ($backup in $backups) { $total += (Get-DarlingDirectoryBytes $backup.FullName) }

    Note ("{0} rollback backup(s), {1} total. Keeping the newest {2}:" -f $backups.Count, (Format-DarlingBytes $total), $KeepRollbacks)

    $prunableNames = @($prunable | ForEach-Object { $_.Name })
    foreach ($backup in $backups) {
        $verdict = if ($prunableNames -contains $backup.Name) { 'prune' } else { 'KEEP ' }
        Note ("  [{0}] {1}  ({2}, last written {3:yyyy-MM-dd HH:mm} UTC)" -f $verdict, $backup.Name, (Format-DarlingBytes (Get-DarlingDirectoryBytes $backup.FullName)), $backup.LastWriteTimeUtc)
    }

    if ($ListRollbacks) {
        Note "Nothing was changed (-ListRollbacks). Re-run with -PruneOnly to remove the ones marked prune."
        exit 0
    }

    if ($prunable.Count -eq 0) {
        Good "Nothing to prune."
        exit 0
    }

    $result = Remove-DarlingRollbackBackups $prunable
    Good ("Removed {0} rollback backup(s), reclaiming {1}." -f $result.Removed, (Format-DarlingBytes $result.Reclaimed))
    if ($result.Failures.Count -gt 0) {
        Warn ("{0} could not be removed: {1}. Re-running is safe and will retry them." -f $result.Failures.Count, ($result.Failures -join ', '))
        exit 1
    }

    exit 0
}

# ============================ the source build ============================

if ([string]::IsNullOrWhiteSpace($Source)) {
    Fail "No -Source given and this script is not running from a file, so there is nothing to install from."
}

try { $Source = [IO.Path]::GetFullPath($Source).TrimEnd('\') }
catch { Fail "'$Source' is not a path this script can resolve." }

if (-not (Test-Path -LiteralPath $Source)) {
    Fail "The source '$Source' does not exist."
}

$sourceIsZip = (Test-Path -LiteralPath $Source -PathType Leaf) -and $Source.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)
$sourceRoot = if ($sourceIsZip) { [IO.Path]::GetDirectoryName($Source) } else { $Source }

# THE SELF-OVERWRITE REFUSAL.
#
# This script ships inside the zip and therefore also lives in the install root, so an upgrade run from the
# INSTALLED copy would write the new build over the .ps1 that PowerShell is reading line by line. Best case
# the copy fails on the lock and the tree is half written with the service stopped; worst case the script's
# own remaining lines change underneath it. Neither is worth a clever workaround like relaunching from a
# temp copy: a refusal that names the fix is understood in one read and cannot go subtly wrong.
#
# The condition is WHERE THIS SCRIPT IS, not where the source is. Keying it off the source was the first
# spelling and it had a hole big enough to drive the whole failure through: a zip sitting inside the install
# directory - which is exactly where someone downloads it - has a source root equal to the install root and
# would have been waved past by any rule about folders. The hazard is "the file being executed is about to
# be overwritten", so that is the thing to ask about.
#
# Scoped to the copy path. -PruneOnly and -ListRollbacks exit above this and write no binaries, so the
# installed copy is exactly the right thing to run for those - and it is the one the service's report names.
$runningFrom = if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) { $null } else { [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') }
if ($runningFrom -and $runningFrom.Equals($InstallRoot, [StringComparison]::OrdinalIgnoreCase)) {
    Fail "This script is running from the install directory, so the upgrade would write the new build over the copy of itself that PowerShell is currently reading. Extract the new zip to a staging folder (e.g. C:\staging\<version>) and run ITS upgrade-darling.ps1 instead. To prune rollback backups from the installed copy, use -PruneOnly, which copies nothing."
}

# And the degenerate case the rule above does not cover: a source folder that IS the install directory,
# handed in from a script running somewhere else. Copying a tree over itself is not an upgrade.
if (-not $sourceIsZip -and $sourceRoot.Equals($InstallRoot, [StringComparison]::OrdinalIgnoreCase)) {
    Fail "The source folder is the install directory itself, so there is nothing to upgrade from. Point -Source at the new build."
}

if ($sourceIsZip) {
    $expected = $Sha256

    if ([string]::IsNullOrWhiteSpace($expected)) {
        $sums = Join-Path ([IO.Path]::GetDirectoryName($Source)) 'SHA256SUMS.txt'
        if (Test-Path -LiteralPath $sums) {
            $leaf = [IO.Path]::GetFileName($Source)
            foreach ($line in (Get-Content -LiteralPath $sums)) {
                # '<hash>  <name>' and '<hash> *<name>' are both in the wild; splitting on whitespace and
                # comparing the leaf handles either without a regex nobody can read.
                $parts = @($line -split '\s+' | Where-Object { $_ })
                if ($parts.Count -ge 2 -and $parts[-1].TrimStart('*').Equals($leaf, [StringComparison]::OrdinalIgnoreCase)) {
                    $expected = $parts[0]
                    break
                }
            }
        }
    }

    if ([string]::IsNullOrWhiteSpace($expected)) {
        if (-not $SkipHashCheck) {
            Fail "No SHA256 for '$Source' — pass -Sha256 <hash>, put SHA256SUMS.txt beside the zip, or say -SkipHashCheck. This overwrites the binaries of a running monitoring host; an unverified zip is not something to find out about afterwards."
        }
        Warn "Proceeding with an UNVERIFIED source zip (-SkipHashCheck)."
    }
    else {
        $actual = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
        if (-not $actual.Equals($expected.Trim(), [StringComparison]::OrdinalIgnoreCase)) {
            Fail "SHA256 mismatch on '$Source'. Expected $expected, got $actual. Nothing has been stopped or copied."
        }
        Good "Source zip SHA256 verified."
    }
}
else {
    if (-not (Test-Path -LiteralPath (Join-Path $Source $serviceExeName))) {
        Fail "'$Source' does not hold $serviceExeName, so it is not an extracted Darling build."
    }
    Warn "The source is a folder, so this script cannot verify it — verify the zip's SHA256 before you extract it."
}

# ============================ the service has to exist ============================
#
# The auto-resolve path cannot reach here without a registered service - it reads the install root out of
# the ImagePath - but an explicit -InstallRoot skips that check entirely. A tree holding the binaries of a
# service that was renamed, removed, or never registered then sails through the stop guard, the backup, the
# prune and the copy, and falls over at Start-Service with a raw terminating error instead of one of this
# script's own messages. Failing HERE costs nothing and says what to do; failing there costs a completed
# copy, a stopped-that-was-never-running service, and an error nobody can interpret.
#
# Deliberately not applied to -PruneOnly, which exits above: reclaiming disk from a tree whose service is
# gone is a perfectly reasonable thing to want, and it copies nothing.
if (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
    Fail "The '$serviceName' service is not registered on this machine, so there is nothing for this copy to stop and start around it. NOTHING has been stopped or copied. If '$InstallRoot' is a staging tree rather than an install, you want install-darling.ps1; if you only meant to reclaim disk, re-run with -PruneOnly, which needs no service."
}

# ============================ the stop guard ============================

# PHASE ONE, before anything is stopped: the processes a service stop will NOT clear.
#
# The service's own exe and the bundled PostgreSQL under pg-runtime are filtered out here because they are
# about to be stopped on purpose. Without that filter this guard refuses EVERY upgrade of a running install
# - the service is always holding its own tree - which is a guard that fails closed on the happy path and
# trains people to pass -SkipStopGuard, i.e. a guard that has stopped guarding by being unusable.
$holders = @(Get-DarlingProcessesUnderPath $InstallRoot |
    Where-Object { -not (Test-DarlingProcessStopsWithTheService $_.Path $InstallRoot) })

if ($holders.Count -gt 0 -and -not $SkipStopGuard) {
    # Parenthesised before -join on purpose: `$x | ForEach-Object { ... } -join ', '` binds -join to
    # ForEach-Object as a parameter and throws, which is a fine way to lose a deploy to a formatting bug.
    $names = @($holders | ForEach-Object { "$($_.ProcessName) (pid $($_.Id))" })
    Note "Processes are running out of the install tree that stopping the service will not close:"
    foreach ($name in $names) { Note "  $name" }
    Fail "Close them and re-run. NOTHING has been stopped or copied — the service is still running and the install is untouched. Do NOT kill them blindly. If these are your own psql.exe or a shell sitting in the install directory, or a Darling Viewer you left open, just exit them. Use -SkipStopGuard only if you are certain the copy will not hit a locked file."
}

# ============================ stop, back up, prune, copy, start ============================
#
# From here on a failure leaves the service DOWN. Every step below is idempotent and the failure messages
# say so, because "run it again" is the correct advice and an operator staring at a stopped monitoring
# service should not have to work that out.

$configPath = Join-Path $InstallRoot $configName
$configHashBefore = if (Test-Path -LiteralPath $configPath) { (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash } else { $null }

# Status is re-read here rather than carried down from the existence check above: it is a snapshot, and
# between the two the service can legitimately have been stopped by someone else or crashed on its own.
if ((Get-Service -Name $serviceName).Status -ne 'Stopped') {
    Note "Stopping '$serviceName'..."
    Stop-Service -Name $serviceName -Force
    try { (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromMinutes(2)) }
    catch { Fail "'$serviceName' did not reach Stopped within two minutes. Nothing has been copied. Check what it is waiting on and re-run." }
}
Good "Service is stopped."

# PHASE TWO, now that it is down: anything STILL holding the tree, with no exclusions at all.
#
# Everything phase one filtered out should be gone by now, so a hit here is the interesting case rather
# than the normal one - most often a postmaster under pg-runtime that outlived the service stop, which is
# precisely the process nothing may kill. Phase one cannot see this and phase two cannot see phase one's
# cases without an outage, which is why there are two.
$stillHolding = Get-DarlingProcessesUnderPath $InstallRoot
if ($stillHolding.Count -gt 0 -and -not $SkipStopGuard) {
    $names = @($stillHolding | ForEach-Object { "$($_.ProcessName) (pid $($_.Id))" })
    Note "The service is stopped, but processes are STILL running out of the install tree:"
    foreach ($name in $names) { Note "  $name" }
    Fail "Nothing has been copied, so the install is intact — but the service is now STOPPED. Either close these and re-run (safe, and it will reuse the backup it is about to take), or abandon the upgrade with: Start-Service '$serviceName'. Do NOT kill anything under $InstallRoot\pg-runtime — that is the bundled PostgreSQL and killing it takes the store down; give a postmaster that outlived the stop a few seconds and re-run."
}

$backups = Get-DarlingRollbackBackups $InstallRoot
$nowUtc = [datetime]::UtcNow

if (Test-DarlingRollbackBackupIsRecent $backups $BackupWindowMinutes $nowUtc) {
    Note ("Reusing the rollback backup {0}, taken {1:N0} minute(s) ago — this looks like a re-run of an interrupted upgrade, and a second backup now would copy a half-upgraded tree over the good one." -f $backups[0].Name, ($nowUtc - $backups[0].LastWriteTimeUtc).TotalMinutes)
}
else {
    $backupPath = Join-Path $InstallRoot (New-DarlingRollbackBackupName $nowUtc)
    Note "Backing up the install root's files to $backupPath ..."
    try {
        New-Item -ItemType Directory -Path $backupPath -Force | Out-Null
        # FILES only, not subdirectories - the shape the documented procedure has always used, and the
        # reason a backup is ~120 MB rather than ~1 GB. pg-runtime is the bundled PostgreSQL (extracted on
        # first run, and hundreds of megabytes of it), and viewer\ / wwwroot\ / runtimes\ all come back
        # from the zip.
        #
        # SO A BACKUP IS NOT A WHOLE-TREE SNAPSHOT, and the difference matters in exactly one scenario:
        # a copy that dies PARTWAY can leave viewer\ / wwwroot\ / runtimes\ mixed old-and-new, and
        # restoring these files over the top does not unmix them. The complete revert is these files PLUS
        # the previous version's zip re-extracted. Backing those directories up instead was considered and
        # rejected: it multiplies what #2525 is about - retained disk - to cover a case whose real fix is
        # re-extracting a zip you still have, and it still would not cover pg-runtime. The failure paths
        # below say this rather than leaving an operator to discover it while recovering.
        Get-ChildItem -LiteralPath $InstallRoot -File -Force | Copy-Item -Destination $backupPath -Force
    }
    catch {
        Fail "Could not take the rollback backup ($($_.Exception.Message)). The service is STOPPED and NOTHING has been overwritten, so the install is intact: start it with 'Start-Service ''$serviceName''', or free up disk and re-run."
    }

    $backups = Get-DarlingRollbackBackups $InstallRoot
    Good ("Rollback backup taken ({0})." -f (Format-DarlingBytes (Get-DarlingDirectoryBytes $backupPath)))
}

# Pruning comes AFTER the backup, so the copy this run just took is counted among the ones kept and the
# tree never passes through a moment with fewer rollback points than retention promises.
$prunable = Select-DarlingRollbackBackupsToPrune $backups $KeepRollbacks
if ($prunable.Count -gt 0) {
    Note ("Pruning {0} rollback backup(s) past the newest {1}:" -f $prunable.Count, $KeepRollbacks)
    $result = Remove-DarlingRollbackBackups $prunable
    Good ("Reclaimed {0}." -f (Format-DarlingBytes $result.Reclaimed))
}

# A KNOWN GAP, named here rather than left for someone to discover: this is an OVERLAY, not a replacement.
# Expand-Archive -Force (and the Copy-Item -Recurse -Force folder path) overwrite what the new build ships
# and delete nothing else, so a file the old version had and the new one dropped - a removed dependency, a
# renamed assembly, a satellite-resource folder for a culture we no longer localize into - stays in the
# tree forever. DarlingInstallDirectoryReport will not catch it either: it walks top-level DIRECTORIES, so
# a stale DLL sitting in the root or in viewer\ is invisible to it.
#
# Not fixed here on purpose. The obvious repair - diff the new build's manifest against the install root
# and warn about the remainder - has to know about every file that legitimately lives here and was never
# in a zip: darling.json, the DPAPI credential blobs, the rollback backups themselves, pg-runtime, and
# whatever an operator put there. Get that list wrong and it warns about darling.json on every upgrade,
# which is #2525 all over again with a new subject. Filed as #2529 with the options and the measurement
# that should decide between them, rather than bolted on at the end of this one.
Note "Laying the new build over $InstallRoot ..."
$copied = $false
foreach ($attempt in 1, 2) {
    try {
        if ($sourceIsZip) {
            Expand-Archive -LiteralPath $Source -DestinationPath $InstallRoot -Force
        }
        else {
            Copy-Item -Path (Join-Path $Source '*') -Destination $InstallRoot -Recurse -Force
        }
        $copied = $true
        break
    }
    catch {
        # The transient one is a DLL an antivirus scan or a not-yet-exited process still holds, and a retry
        # a moment later has worked more than once. Two attempts, then stop: a third would just be a longer
        # way to arrive at the same half-written tree.
        if ($attempt -eq 1) {
            Warn "The copy failed ($($_.Exception.Message)). Retrying in 10 seconds — this step has lost to a transiently locked DLL before."
            Start-Sleep -Seconds 10
        }
        else {
            Fail "The copy failed twice ($($_.Exception.Message)). The service is STOPPED and the install tree may be HALF WRITTEN — do not start it. Re-run this script with the same arguments: it will reuse the rollback backup it already took rather than replacing it, and finish the copy, which is the FIRST thing to try. To go back to the old version instead, note that a half-written tree needs BOTH halves: re-extract the PREVIOUS version's zip over $InstallRoot (that restores viewer\, wwwroot\ and runtimes\, which the backup does not hold), then copy the files from the newest _rollback_manual_* directory over the top. Restoring only the backup leaves old root binaries paired with partly-new subdirectories."
        }
    }
}

if (-not $copied) { Fail "The copy did not complete." }
Good "New build in place."

if ($configHashBefore) {
    $configHashAfter = if (Test-Path -LiteralPath $configPath) { (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash } else { $null }
    if ($configHashAfter -ne $configHashBefore) {
        # The zip ships darling.sample.json and never darling.json, so this should be impossible - which is
        # exactly why it is checked rather than trusted. A config replaced by a deploy is a monitoring host
        # that comes back up watching nothing, and the newest rollback backup still holds the original.
        Warn "darling.json CHANGED during the copy. The zip ships only darling.sample.json, so this should not happen. The original is in the newest _rollback_manual_* directory — compare them before starting the service."
    }
    else {
        Good "darling.json is unchanged."
    }
}

Note "Starting '$serviceName'..."
Start-Service -Name $serviceName
try { (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromMinutes(2)) }
catch { Fail "'$serviceName' did not reach Running within two minutes. The new build IS in place — read %ProgramData%\PerformanceMonitorDarling\logs before rolling back." }

Good "Service is Running."
Note ""
Note "The install is NOT verified yet. 'Running' means the process started, not that it collects."
Note "In 10-15 minutes, against the store, confirm all of:"
Note "  1. MAX(version) FROM darling_schema_version equals this build's expected schema rung."
Note "  2. COUNT(DISTINCT server_id) FROM collect.collection_log over the last 15 minutes equals the fleet size."
Note "  3. COUNT(DISTINCT collector_name) over the same window is in the mid-30s, not single digits."
Note "  4. Any non-SUCCESS rows since the restart are READ, not just counted — YIELDED is the lock-timeout guard working; anything else is a finding."
Note "  5. If this build added a collector or a migration, one targeted probe that ITS table moved."
