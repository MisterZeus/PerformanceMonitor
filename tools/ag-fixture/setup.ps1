<#
.SYNOPSIS
    Stands up the two-node clusterless Availability Group test fixture in Docker.

.DESCRIPTION
    Brings up the ag1/ag2 containers with docker compose, waits for both to report
    healthy, then runs the numbered scripts in sql/ through each container's own
    sqlcmd to build a CLUSTER_TYPE = NONE availability group named ag_fixture with a
    seed database named AgFixtureDb.

    The whole thing is idempotent: every script guards its own object, so re-running
    setup against a live fixture is a no-op rather than a pile of errors.

    Requires Docker Desktop with the Linux engine running. Copy .env.example to .env
    first - compose refuses to start without MSSQL_SA_PASSWORD.

.PARAMETER DockerPath
    Path to docker.exe. Defaults to the Docker Desktop install location, which is not
    normally on PATH.

.PARAMETER SkipCompose
    Skip `docker compose up` and only run the SQL setup against already-running
    containers.

.PARAMETER ReadyTimeoutSeconds
    How long to wait for both containers to report healthy. Defaults to 300.

.EXAMPLE
    .\setup.ps1
    Bring the fixture up from nothing.

.EXAMPLE
    .\setup.ps1 -SkipCompose
    Re-run only the AG setup scripts against containers that are already up.
#>
[CmdletBinding()]
param
(
    [string]$DockerPath = "C:\Program Files\Docker\Docker\resources\bin\docker.exe",
    [switch]$SkipCompose,
    [int]$ReadyTimeoutSeconds = 300
)

$ErrorActionPreference = "Stop"

$fixtureRoot = $PSScriptRoot
$composeFile = Join-Path $fixtureRoot "docker-compose.yml"
$envFile = Join-Path $fixtureRoot ".env"
$sqlDir = Join-Path $fixtureRoot "sql"

# sqlcmd inside the 2022 image lives here, and needs -C because the instance presents
# a self-signed certificate.
$sqlcmd = "/opt/mssql-tools18/bin/sqlcmd"

if (-not (Test-Path $DockerPath))
{
    throw "docker.exe not found at '$DockerPath'. Pass -DockerPath."
}

# docker shells out to docker-credential-desktop.exe to read the registry credentials,
# and finds it on PATH only. In a default Docker Desktop install nothing in that bin
# directory is on PATH, so `compose up` dies on "error getting credentials" before it
# pulls anything.
$dockerBin = Split-Path -Parent $DockerPath

if (($env:PATH -split [System.IO.Path]::PathSeparator) -notcontains $dockerBin)
{
    $env:PATH = "$dockerBin$([System.IO.Path]::PathSeparator)$env:PATH"
}

if (-not (Test-Path $envFile))
{
    throw "No .env found at '$envFile'. Copy .env.example to .env and set the passwords."
}

# Compose reads .env itself; we need the same values here to pass to sqlcmd.
$envValues = @{}
foreach ($line in Get-Content $envFile)
{
    if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$')
    {
        $envValues[$Matches[1]] = $Matches[2]
    }
}

foreach ($required in @("MSSQL_SA_PASSWORD", "AG_CERT_PASSWORD"))
{
    if ([string]::IsNullOrWhiteSpace($envValues[$required]))
    {
        throw "$required is not set in '$envFile'."
    }

    if ($envValues[$required] -like "REPLACE_ME*")
    {
        Write-Warning "$required is still the .env.example placeholder. Fine for a local fixture, but change it if these containers are reachable from anywhere but this machine."
    }
}

$saPassword = $envValues["MSSQL_SA_PASSWORD"]
$certPassword = $envValues["AG_CERT_PASSWORD"]

function Invoke-Docker
{
    param
    (
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$IgnoreExitCode
    )

    $output = & $DockerPath @Arguments 2>&1
    $exit = $LASTEXITCODE

    if ($exit -ne 0 -and -not $IgnoreExitCode)
    {
        $joined = $output -join [Environment]::NewLine
        throw "docker $($Arguments -join ' ') failed with exit code $exit`n$joined"
    }

    return $output
}

function Invoke-SqlFile
{
    <#
        Streams a .sql file into the container's sqlcmd on stdin rather than copying
        it in first. -b makes sqlcmd exit non-zero on a T-SQL error so a failed setup
        step actually stops the script.
    #>
    param
    (
        [Parameter(Mandatory = $true)][string]$Container,
        [Parameter(Mandatory = $true)][string]$Path,
        [hashtable]$Variables = @{}
    )

    # -W trims the fixed-width padding off every column; without it the verification
    # output is a few hundred characters of spaces per row.
    $arguments = @("exec", "-i", $Container, $sqlcmd, "-S", "localhost", "-U", "sa", "-P", $saPassword, "-C", "-b", "-l", "60", "-W", "-w", "1024")

    foreach ($key in $Variables.Keys)
    {
        $arguments += @("-v", "$key=$($Variables[$key])")
    }

    Write-Host "  [$Container] $(Split-Path -Leaf $Path)" -ForegroundColor DarkGray

    $output = Get-Content -Raw -Path $Path | & $DockerPath @arguments 2>&1
    $exit = $LASTEXITCODE

    if ($exit -ne 0)
    {
        $joined = $output -join [Environment]::NewLine
        throw "$(Split-Path -Leaf $Path) failed on $Container (exit $exit)`n$joined"
    }

    return $output
}

function Invoke-SqlQuery
{
    param
    (
        [Parameter(Mandatory = $true)][string]$Container,
        [Parameter(Mandatory = $true)][string]$Query,
        [switch]$IgnoreExitCode
    )

    $arguments = @("exec", "-i", $Container, $sqlcmd, "-S", "localhost", "-U", "sa", "-P", $saPassword, "-C", "-b", "-l", "60", "-Q", $Query)

    $output = & $DockerPath @arguments 2>&1

    if ($LASTEXITCODE -ne 0 -and -not $IgnoreExitCode)
    {
        throw "Query failed on ${Container}: $($output -join [Environment]::NewLine)"
    }

    return $output
}

if (-not $SkipCompose)
{
    Write-Host "Starting containers..." -ForegroundColor Cyan
    Invoke-Docker -Arguments @("compose", "-f", $composeFile, "--env-file", $envFile, "up", "-d") | Out-Null
}

Write-Host "Waiting for ag1 and ag2 to report healthy (timeout ${ReadyTimeoutSeconds}s)..." -ForegroundColor Cyan

$deadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds)

foreach ($container in @("ag1", "ag2"))
{
    while ($true)
    {
        $status = (Invoke-Docker -Arguments @("inspect", "-f", "{{.State.Health.Status}}", $container) -IgnoreExitCode) -join ""

        if ($status -match "healthy")
        {
            Write-Host "  $container healthy" -ForegroundColor Green
            break
        }

        if ((Get-Date) -gt $deadline)
        {
            throw "$container did not become healthy in ${ReadyTimeoutSeconds}s (last status: '$status'). Check: $DockerPath logs $container"
        }

        Start-Sleep -Seconds 5
    }
}

# The healthcheck fires on the errorlog's ready line, which SQL Server writes slightly
# before it will actually accept logins. Poll a trivial query rather than sleeping a
# fixed amount.
Write-Host "Waiting for both instances to accept logins..." -ForegroundColor Cyan

foreach ($container in @("ag1", "ag2"))
{
    while ($true)
    {
        $probe = Invoke-SqlQuery -Container $container -Query "SET NOCOUNT ON; SELECT 'up';" -IgnoreExitCode

        if (($probe -join "") -match "up")
        {
            Write-Host "  $container accepting logins" -ForegroundColor Green
            break
        }

        if ((Get-Date) -gt $deadline)
        {
            throw "$container never accepted logins. Check: $DockerPath logs $container"
        }

        Start-Sleep -Seconds 5
    }
}

Write-Host "Creating certificate on ag1..." -ForegroundColor Cyan

# BACKUP CERTIFICATE will not overwrite, so clear any export left by a previous run.
Invoke-Docker -Arguments @("exec", "-u", "root", "ag1", "rm", "-f", "/var/opt/mssql/data/ag_fixture_certificate.cer", "/var/opt/mssql/data/ag_fixture_certificate.pvk") -IgnoreExitCode | Out-Null

Invoke-SqlFile -Container "ag1" -Path (Join-Path $sqlDir "01_master_key_and_certificate.sql") -Variables @{ cert_password = $certPassword } | Out-Null

Write-Host "Copying certificate to ag2..." -ForegroundColor Cyan

$staging = Join-Path ([System.IO.Path]::GetTempPath()) "ag-fixture-cert-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $staging | Out-Null

try
{
    foreach ($file in @("ag_fixture_certificate.cer", "ag_fixture_certificate.pvk"))
    {
        Invoke-Docker -Arguments @("cp", "ag1:/var/opt/mssql/data/$file", (Join-Path $staging $file)) | Out-Null
        Invoke-Docker -Arguments @("cp", (Join-Path $staging $file), "ag2:/var/opt/mssql/data/$file") | Out-Null
    }

    # docker cp lands files owned by root. SQL Server runs as mssql and cannot read
    # them, and CREATE CERTIFICATE fails with a misleading "cannot find the file".
    Invoke-Docker -Arguments @("exec", "-u", "root", "ag2", "chown", "mssql:root", "/var/opt/mssql/data/ag_fixture_certificate.cer", "/var/opt/mssql/data/ag_fixture_certificate.pvk") | Out-Null
    Invoke-Docker -Arguments @("exec", "-u", "root", "ag2", "chmod", "600", "/var/opt/mssql/data/ag_fixture_certificate.cer", "/var/opt/mssql/data/ag_fixture_certificate.pvk") | Out-Null
}
finally
{
    Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue
}

Invoke-SqlFile -Container "ag2" -Path (Join-Path $sqlDir "02_restore_certificate.sql") -Variables @{ cert_password = $certPassword } | Out-Null

Write-Host "Creating mirroring endpoints..." -ForegroundColor Cyan
Invoke-SqlFile -Container "ag1" -Path (Join-Path $sqlDir "03_endpoint.sql") | Out-Null
Invoke-SqlFile -Container "ag2" -Path (Join-Path $sqlDir "03_endpoint.sql") | Out-Null

Write-Host "Creating availability group ag_fixture on ag1..." -ForegroundColor Cyan
Invoke-SqlFile -Container "ag1" -Path (Join-Path $sqlDir "04_create_availability_group.sql") | Out-Null

Write-Host "Joining ag2..." -ForegroundColor Cyan
Invoke-SqlFile -Container "ag2" -Path (Join-Path $sqlDir "05_join_availability_group.sql") | Out-Null

Write-Host "Creating and seeding AgFixtureDb..." -ForegroundColor Cyan
Invoke-SqlFile -Container "ag1" -Path (Join-Path $sqlDir "06_seed_database.sql") | Out-Null

# Automatic seeding is asynchronous - the ADD DATABASE returns well before the
# secondary copy exists.
Write-Host "Waiting for AgFixtureDb to seed to ag2..." -ForegroundColor Cyan

$seedDeadline = (Get-Date).AddSeconds(180)

while ($true)
{
    $state = (Invoke-SqlQuery -Container "ag1" -IgnoreExitCode -Query "SET NOCOUNT ON; SELECT TOP (1) drs.synchronization_state_desc FROM sys.dm_hadr_database_replica_states AS drs WHERE drs.is_local = 0 AND drs.database_id = DB_ID('AgFixtureDb');") -join " "

    if ($state -match "SYNCHRONIZED")
    {
        Write-Host "  AgFixtureDb SYNCHRONIZED on ag2" -ForegroundColor Green
        break
    }

    if ((Get-Date) -gt $seedDeadline)
    {
        throw "AgFixtureDb never reached SYNCHRONIZED on ag2 (last state: '$($state.Trim())')."
    }

    Start-Sleep -Seconds 5
}

Write-Host ""
Write-Host "Verification:" -ForegroundColor Cyan
Invoke-SqlFile -Container "ag1" -Path (Join-Path $sqlDir "07_verify.sql")

Write-Host ""
Write-Host "Fixture is up." -ForegroundColor Green
Write-Host "  ag1 (primary):   localhost,14331"
Write-Host "  ag2 (secondary): localhost,14332"
Write-Host "  login: sa / the MSSQL_SA_PASSWORD in .env   (TrustServerCertificate=True)"
Write-Host ""
Write-Host "Put load on it with:  .\write-load.ps1 -Seconds 30"
Write-Host "Tear it down with:    .\teardown.ps1"
