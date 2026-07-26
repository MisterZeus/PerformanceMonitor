<#
.SYNOPSIS
    Runs a short insert workload against AgFixtureDb on ag1.

.DESCRIPTION
    Puts enough log traffic on the AG for the send/redo queue and rate columns in
    sys.dm_hadr_database_replica_states to move off zero, so collector output can be
    checked against something other than a completely idle group.

    Runs server-side in a loop for -Seconds, then prints what it inserted.

.PARAMETER Seconds
    How long to keep inserting. Defaults to 30.

.PARAMETER DockerPath
    Path to docker.exe. Defaults to the Docker Desktop install location.

.EXAMPLE
    .\write-load.ps1 -Seconds 60
#>
[CmdletBinding()]
param
(
    [int]$Seconds = 30,
    [string]$DockerPath = "C:\Program Files\Docker\Docker\resources\bin\docker.exe"
)

$ErrorActionPreference = "Stop"

$fixtureRoot = $PSScriptRoot
$envFile = Join-Path $fixtureRoot ".env"
$loadScript = Join-Path $fixtureRoot "sql\90_write_load.sql"

if (-not (Test-Path $envFile))
{
    throw "No .env found at '$envFile'. Copy .env.example to .env and set the passwords."
}

$saPassword = $null

foreach ($line in Get-Content $envFile)
{
    if ($line -match '^\s*MSSQL_SA_PASSWORD\s*=\s*(.*?)\s*$')
    {
        $saPassword = $Matches[1]
    }
}

if ([string]::IsNullOrWhiteSpace($saPassword))
{
    throw "MSSQL_SA_PASSWORD is not set in '$envFile'."
}

Write-Host "Running write load against ag1 for ${Seconds}s..." -ForegroundColor Cyan

# -t has to clear the loop duration or sqlcmd times the batch out mid-run.
$arguments = @(
    "exec", "-i", "ag1",
    "/opt/mssql-tools18/bin/sqlcmd",
    "-S", "localhost",
    "-U", "sa",
    "-P", $saPassword,
    "-C",
    "-b",
    "-t", ($Seconds + 120),
    "-v", "duration_seconds=$Seconds"
)

Get-Content -Raw -Path $loadScript | & $DockerPath @arguments

if ($LASTEXITCODE -ne 0)
{
    throw "Write load failed (exit $LASTEXITCODE)."
}
