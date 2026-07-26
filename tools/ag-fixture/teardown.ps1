<#
.SYNOPSIS
    Destroys the Availability Group test fixture.

.DESCRIPTION
    Runs `docker compose down -v`, which removes the ag1/ag2 containers, the
    ag-fixture-net network, and both data volumes. Nothing survives - the next
    setup.ps1 builds the AG from scratch.

    Only touches the ag-fixture compose project. Other containers on the machine are
    left alone.

.PARAMETER DockerPath
    Path to docker.exe. Defaults to the Docker Desktop install location.

.EXAMPLE
    .\teardown.ps1
#>
[CmdletBinding()]
param
(
    [string]$DockerPath = "C:\Program Files\Docker\Docker\resources\bin\docker.exe"
)

$ErrorActionPreference = "Stop"

$fixtureRoot = $PSScriptRoot
$composeFile = Join-Path $fixtureRoot "docker-compose.yml"
$envFile = Join-Path $fixtureRoot ".env"

if (-not (Test-Path $DockerPath))
{
    throw "docker.exe not found at '$DockerPath'. Pass -DockerPath."
}

# Same reason as setup.ps1: compose subcommands shell out to the credential helper,
# which is findable on PATH only, and a default Docker Desktop install puts none of
# its bin directory there.
$dockerBin = Split-Path -Parent $DockerPath

if (($env:PATH -split [System.IO.Path]::PathSeparator) -notcontains $dockerBin)
{
    $env:PATH = "$dockerBin$([System.IO.Path]::PathSeparator)$env:PATH"
}

# compose still interpolates MSSQL_SA_PASSWORD while parsing the file on `down`, so
# the .env has to be there even though nothing is being started.
$arguments = @("compose", "-f", $composeFile)

if (Test-Path $envFile)
{
    $arguments += @("--env-file", $envFile)
}
else
{
    $env:MSSQL_SA_PASSWORD = "teardown-placeholder"
}

$arguments += @("down", "-v")

Write-Host "Removing the ag-fixture containers, network, and volumes..." -ForegroundColor Cyan

& $DockerPath @arguments

if ($LASTEXITCODE -ne 0)
{
    throw "docker compose down failed (exit $LASTEXITCODE)."
}

Write-Host "Fixture removed." -ForegroundColor Green
