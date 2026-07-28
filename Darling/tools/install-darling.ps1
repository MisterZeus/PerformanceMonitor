<#
.SYNOPSIS
Installs (or upgrades) the PerformanceMonitor Darling service from the folder this script sits in,
and creates Desktop + Start Menu shortcuts for the bundled viewer.

.DESCRIPTION
Run from an ELEVATED PowerShell, from the folder you extracted the Darling zip into (the script
installs the service pointing at THAT folder — extract to the final location first, e.g.
C:\PerformanceMonitorDarling). What it does, in order:

  1. Verifies elevation, the service exe, and darling.json (offers to copy darling.sample.json
     and stops so you can edit it — the service is not installed with an unedited sample).
  2. Optional pre-flight: runs `--test-connection` and shows the per-server PASS/FAIL lines
     (continue-or-abort prompt on failure; -SkipPreflight to skip).
  3. Registers the Windows Event Log source 'PerformanceMonitor Darling' (requires elevation —
     the service's own virtual account cannot; without it Event Log diagnostics are silently
     dropped. The file log under %ProgramData%\PerformanceMonitorDarling\logs works regardless).
  4. Creates the service under the NT SERVICE virtual account (NEVER LocalSystem — the bundled
     PostgreSQL refuses to run with administrative privileges), start=auto. If the service
     already exists this is an UPGRADE: it is stopped and its binPath updated in place; your
     darling.json, store data, and credentials are untouched.
  4b. Restricts darling.json to SYSTEM / Administrators / the service account, plus read for
     INTERACTIVE (the Viewer). It holds encrypted SQL passwords and the MCP/web access tokens,
     and an install folder under C:\ otherwise inherits read access for BUILTIN\Users.
  4c. Creates (or removes) the scoped Windows Firewall rules to match darling.json, via the exe's
     --configure-firewall verb. This requires elevation, which is why it lives here: the service's
     own unprivileged account cannot create them, and only verifies them at runtime.
  5. Starts the service and confirms it reaches Running.
  6. Creates 'Darling Viewer' shortcuts on the Desktop and in the Start Menu pointing at
     viewer\PerformanceMonitor.Darling.Viewer.exe. (Taskbar pinning is deliberately not
     attempted — Windows blocks programmatic pinning by design; pin from the Start Menu entry.)

Uninstall with uninstall-darling.ps1 (same folder).

.PARAMETER SkipPreflight
Skip the --test-connection pre-flight gate.

.PARAMETER NoShortcuts
Do not create the viewer shortcuts.

.PARAMETER Network
After the service reaches Running, launch the interactive --configure-network wizard to opt into the
store / MCP / web-dashboard LAN endpoints (guided, delegated validation, comment-preserving darling.json
edit + backup). Off by default; the endpoints stay loopback-only unless you pass this or edit darling.json
by hand.
#>
[CmdletBinding()]
param(
    [switch]$SkipPreflight,
    [switch]$NoShortcuts,
    [switch]$Network
)

$ErrorActionPreference = 'Stop'
$serviceName = 'PerformanceMonitor Darling'
$root = $PSScriptRoot
$serviceExe = Join-Path $root 'PerformanceMonitor.Darling.Service.exe'
$viewerExe = Join-Path $root 'viewer\PerformanceMonitor.Darling.Viewer.exe'
$configPath = Join-Path $root 'darling.json'
$samplePath = Join-Path $root 'darling.sample.json'

function Fail([string]$message) { Write-Host "ERROR: $message" -ForegroundColor Red; exit 1 }

# -- 1. Environment checks ------------------------------------------------------------------------
$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail 'Run this script from an ELEVATED PowerShell (service creation and Event Log registration require it).'
}

if (-not (Test-Path $serviceExe)) {
    Fail "PerformanceMonitor.Darling.Service.exe not found beside this script. Extract the full Darling zip and run install-darling.ps1 from the extracted folder."
}

if (-not (Test-Path $configPath)) {
    if (Test-Path $samplePath) {
        Copy-Item $samplePath $configPath
        Write-Host ''
        Write-Host 'darling.json did not exist, so darling.sample.json was copied to darling.json.' -ForegroundColor Yellow
        Write-Host 'EDIT IT NOW (servers to monitor, auth) and re-run this script. The sample is heavily commented.' -ForegroundColor Yellow
        Write-Host "  notepad `"$configPath`"" -ForegroundColor Yellow
        exit 2
    }

    Fail 'Neither darling.json nor darling.sample.json found beside this script - the zip looks incomplete.'
}

# -- 2. Pre-flight --------------------------------------------------------------------------------
if (-not $SkipPreflight) {
    Write-Host 'Pre-flight: validating darling.json and probing every configured server (--test-connection)...'
    & $serviceExe --test-connection
    if ($LASTEXITCODE -ne 0) {
        Write-Host ''
        Write-Host "Pre-flight FAILED (exit $LASTEXITCODE) - the config is invalid or a server is unreachable." -ForegroundColor Yellow
        $answer = Read-Host 'Install anyway? A monitoring service may legitimately be installed while a target is down. [y/N]'
        if ($answer -notmatch '^[Yy]') { exit 3 }
    }
    else {
        Write-Host 'Pre-flight passed.' -ForegroundColor Green
    }
}

# -- 3. Event Log source --------------------------------------------------------------------------
try {
    New-EventLog -LogName Application -Source $serviceName -ErrorAction Stop
    Write-Host "Registered Event Log source '$serviceName'."
}
catch [System.InvalidOperationException] {
    Write-Host "Event Log source '$serviceName' already registered."
}

# -- 4. Create or upgrade the service -------------------------------------------------------------
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service already exists - upgrading its binPath in place (config, store data, and credentials untouched)."
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }

    & sc.exe config $serviceName binPath= "`"$serviceExe`"" | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "sc config failed ($LASTEXITCODE)." }
}
else {
    # The space after binPath=/start=/obj= is sc.exe syntax. The virtual service account is
    # password-less, per-service, unprivileged - and mandatory-in-practice for the bundled
    # PostgreSQL, which refuses to run with administrative privileges (LocalSystem bricks it).
    & sc.exe create $serviceName binPath= "`"$serviceExe`"" start= auto obj= "NT SERVICE\$serviceName" | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "sc create failed ($LASTEXITCODE)." }
    Write-Host "Created service '$serviceName' (NT SERVICE virtual account, automatic start)."
}

# -- 4b. Lock down darling.json (#1647) -----------------------------------------------------------
# The config holds every monitored server's encryptedPassword plus the MCP bearer and web access tokens.
# They are DPAPI LocalMachine scope with an entropy constant that ships in the open-source repo, so READ
# access to this file IS the secret - the ACL is the boundary, the same posture the PG credential files get.
# Extracting the zip to a folder created directly under C:\ (the documented location) inherits
# BUILTIN\Users: Read & Execute from the root DACL, which hands every local user the lot.
#
# Runs AFTER service creation on purpose: the NT SERVICE virtual account has no SID to grant until sc.exe
# create has made it. Mirrors DarlingFileSecurity.HardenFile(path, allowInteractiveRead: true) exactly -
# SYSTEM / Administrators / the service account get full control, INTERACTIVE gets read (the Viewer and the
# CLI verbs run as the interactive operator and must still read the file). Best-effort: a failure warns
# rather than aborting an otherwise good install, and the service re-asserts this ACL at every startup.
#
# Covers darling.json AND its .bak-* siblings (#1721). The CLI's config-editing verbs back the file up
# before rewriting it, and File.Copy does NOT carry the source DACL - a backup takes the DIRECTORY's
# inheritable ACEs instead, so on an install under C:\ every past edit left a world-readable copy of the
# same secrets. The service hardens new backups itself now; these are the ones already on disk.
#
# The service account is also made the OWNER, best-effort and separately from the DACL. Ownership carries
# WRITE_DAC implicitly, which is what lets the service re-assert this ACL at every start. Granting it
# FullControl below achieves the same thing today; owning the file means it still holds if someone later
# edits the DACL and drops that grant. Done AFTER the DACL and in its own try, so a SeRestorePrivilege
# failure cannot take the DACL down with it - the failure mode we are fixing came from a permissions call
# that silently did nothing.
$hardened = @()
$failed = @()
foreach ($secretFile in @($configPath) + @(Get-ChildItem -Path $root -Filter 'darling.json.bak-*' -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })) {
    try {
        $wk = [System.Security.Principal.WellKnownSidType]
        $systemSid      = New-Object System.Security.Principal.SecurityIdentifier($wk::LocalSystemSid, $null)
        $adminsSid      = New-Object System.Security.Principal.SecurityIdentifier($wk::BuiltinAdministratorsSid, $null)
        $interactiveSid = New-Object System.Security.Principal.SecurityIdentifier($wk::InteractiveSid, $null)
        $serviceSid     = (New-Object System.Security.Principal.NTAccount("NT SERVICE\$serviceName")).Translate([System.Security.Principal.SecurityIdentifier])

        $acl = New-Object System.Security.AccessControl.FileSecurity
        # Protect the DACL and drop every inherited ACE: access is now EXACTLY the four rules below.
        $acl.SetAccessRuleProtection($true, $false)
        $full = [System.Security.AccessControl.FileSystemRights]::FullControl
        $read = [System.Security.AccessControl.FileSystemRights]::Read -bor [System.Security.AccessControl.FileSystemRights]::Synchronize
        $allow = [System.Security.AccessControl.AccessControlType]::Allow
        foreach ($sid in @($systemSid, $adminsSid, $serviceSid)) {
            $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sid, $full, $allow)))
        }
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($interactiveSid, $read, $allow)))
        Set-Acl -Path $secretFile -AclObject $acl

        try {
            $owner = New-Object System.Security.AccessControl.FileSecurity
            $owner.SetOwner($serviceSid)
            Set-Acl -Path $secretFile -AclObject $owner
        }
        catch {
            # Not fatal: the explicit FullControl grant above already carries WRITE_DAC.
        }

        # VERIFY rather than assume. A permissions call that appears to succeed and leaves the file
        # readable is exactly what went unnoticed for months on a field box.
        $after = Get-Acl -Path $secretFile
        $exposed = $after.Access | Where-Object {
            $_.AccessControlType -eq 'Allow' -and
            $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).IsWellKnown($wk::BuiltinUsersSid)
        }
        if ($exposed -or -not $after.AreAccessRulesProtected) {
            $failed += $secretFile
        }
        else {
            $hardened += $secretFile
        }
    }
    catch {
        $failed += "$secretFile ($($_.Exception.Message))"
    }
}

if ($hardened.Count -gt 0) {
    Write-Host "Restricted $($hardened.Count) credential file(s) to SYSTEM, Administrators, the service account, and INTERACTIVE (they hold encrypted passwords and access tokens)."
}
if ($failed.Count -gt 0) {
    Write-Host ''
    Write-Host 'SECURITY WARNING: these files still are not restricted:' -ForegroundColor Red
    foreach ($f in $failed) { Write-Host "  $f" -ForegroundColor Red }
    Write-Host '  They hold every monitored server''s encrypted SQL password plus the MCP and web access tokens.' -ForegroundColor Red
    Write-Host '  Those are DPAPI LocalMachine blobs, so READ ACCESS IS THE SECRET - any local user who can open' -ForegroundColor Red
    Write-Host '  the file can recover all of it. Fix each one from an elevated prompt:' -ForegroundColor Red
    Write-Host "    icacls `"<file>`" /inheritance:d" -ForegroundColor Yellow
    Write-Host "    icacls `"<file>`" /remove:g `"BUILTIN\Users`"" -ForegroundColor Yellow
    Write-Host "    icacls `"<file>`" /grant `"NT SERVICE\$serviceName`:(F)`"" -ForegroundColor Yellow
    Write-Host '  ...or move the install out of a world-readable folder such as one created directly under C:\.' -ForegroundColor Red
    Write-Host ''
}

# -- 4c. Firewall rules (#1771) --------------------------------------------------------------------
# The service runs as an unprivileged virtual account that CANNOT create firewall rules. It used to try on
# every start and fail "Access is denied" each time - harmless where the rules already existed from a manual
# setup, but on a FRESH networked install nothing ever created them, so remote MCP/web/store clients were
# simply blocked. Networked is the normal deployment mode, so rule management belongs here, in the elevated
# install, and the running service only verifies.
#
# The exe does the work (--configure-firewall) rather than this script building rules itself: the rule names,
# the ports, and above all WHETHER a surface is really exposed come from the same resolvers the service
# fail-closes on, so a config the service degrades to loopback never gets an open port, and the service's own
# start-up check always reaches the same verdict the installer just acted on. Idempotent, so an upgrade
# re-running it is a no-op, and it needs only darling.json - no store, no credentials - so it is safe here,
# BEFORE the first start.
#
# Best-effort: a firewall failure warns rather than aborting an otherwise good install. Re-run it by hand.
function Invoke-FirewallReconcile {
    & $serviceExe --configure-firewall
    if ($LASTEXITCODE -ne 0) {
        Write-Host ''
        Write-Host "Firewall rules were not fully configured (exit $LASTEXITCODE). The service will still run, and" -ForegroundColor Yellow
        Write-Host 'will log which rule is missing on each start. Re-run from an elevated prompt:' -ForegroundColor Yellow
        Write-Host "  & `"$serviceExe`" --configure-firewall" -ForegroundColor Yellow
        Write-Host ''
    }
}

Invoke-FirewallReconcile

# -- 5. Start + confirm ---------------------------------------------------------------------------
Start-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
Write-Host "Service is Running. First start does real work (unpack pg-runtime, initdb, store migration, first collection cycle) - give it ~2 minutes." -ForegroundColor Green
Write-Host "Primary log: %ProgramData%\PerformanceMonitorDarling\logs\darling-service_yyyyMMdd.log"

# -- 5b. Optional guided network setup ------------------------------------------------------------
# Runs elevated (this whole script is), so the wizard's restart-to-apply works, and its restart is what
# generates the store TLS cert on the first exposed start. Loopback-only stays the default without -Network.
if ($Network) {
    Write-Host ''
    Write-Host 'Launching the guided network-exposure wizard (--configure-network)...' -ForegroundColor Cyan
    & $serviceExe --configure-network

    # The wizard just rewrote the exposure, so the rules created above no longer match darling.json - a newly
    # exposed endpoint has no rule yet, and one turned back off has a stale one. Re-reconcile (idempotent).
    Write-Host ''
    Write-Host 'Re-applying the firewall rules to match the new exposure...' -ForegroundColor Cyan
    Invoke-FirewallReconcile
}

# -- 6. Viewer shortcuts --------------------------------------------------------------------------
if (-not $NoShortcuts) {
    if (Test-Path $viewerExe) {
        # GetFolderPath returns '' for Desktop/StartMenu whenever there is no loaded user profile - a
        # Windows service, a scheduled task, a CI runner, a remote/SSM session - and Join-Path then
        # throws. Guard it: keep only the targets whose folder resolves, and skip cleanly otherwise.
        $desktop   = [Environment]::GetFolderPath('Desktop')
        $startMenu = [Environment]::GetFolderPath('StartMenu')
        $targets = @()
        if ($desktop)   { $targets += (Join-Path $desktop 'Darling Viewer.lnk') }
        if ($startMenu) { $targets += (Join-Path $startMenu 'Programs\Darling Viewer.lnk') }
        if ($targets.Count -gt 0) {
            $shell = New-Object -ComObject WScript.Shell
            foreach ($lnkPath in $targets) {
                $lnk = $shell.CreateShortcut($lnkPath)
                $lnk.TargetPath = $viewerExe
                $lnk.WorkingDirectory = Split-Path $viewerExe
                $lnk.Description = 'PerformanceMonitor Darling Viewer'
                $lnk.Save()
            }
            Write-Host "Created 'Darling Viewer' shortcuts ($($targets.Count)). Pin to taskbar from the Start Menu entry if wanted."
        }
        else {
            Write-Host 'No interactive Desktop/Start Menu (non-interactive or SYSTEM context) - skipping viewer shortcuts.' -ForegroundColor Yellow
        }
    }
    else {
        Write-Host 'viewer\PerformanceMonitor.Darling.Viewer.exe not found - skipping shortcuts.' -ForegroundColor Yellow
    }
}

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
