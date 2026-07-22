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
store / MCP LAN endpoints (guided, delegated validation, comment-preserving darling.json edit + backup).
Off by default; the endpoints stay loopback-only unless you pass this or edit darling.json by hand.
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
}

# -- 6. Viewer shortcuts --------------------------------------------------------------------------
if (-not $NoShortcuts) {
    if (Test-Path $viewerExe) {
        # GetFolderPath returns '' for Desktop/StartMenu in a non-interactive / SYSTEM context (e.g. an
        # SSM or remote-exec session with no loaded user profile), and Join-Path then throws. Guard it:
        # keep only the shortcut targets whose folder actually resolves, and skip cleanly otherwise.
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
