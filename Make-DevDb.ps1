<#
.SYNOPSIS
  Drop-and-recreate the local TradingDb and apply all DbUp migrations.

.DESCRIPTION
  Idempotent: every invocation produces the same end state — an empty TradingDb
  with the full S2 schema and seed data. Targets LocalDB by default; pass
  -Server '.\SQLEXPRESS' or a full connection string to override.

  All DB work (drop, recreate, migrate) is delegated to TradingBot.MigrationsRunner
  so the script has no PowerShell-version-specific SqlClient dependency.

.PARAMETER Server
  SQL Server instance to target. Default: (localdb)\MSSQLLocalDB

.PARAMETER Database
  Database name. Default: TradingDb

.PARAMETER ConnectionString
  Optional full connection string (overrides -Server / -Database).

.PARAMETER NoBuild
  Skip `dotnet build` and use existing artefacts. Faster on repeat runs.

.EXAMPLE
  pwsh ./Make-DevDb.ps1

.EXAMPLE
  pwsh ./Make-DevDb.ps1 -Server '.\SQLEXPRESS'

.EXAMPLE
  pwsh ./Make-DevDb.ps1 -ConnectionString 'Server=localhost,1433;Database=TradingDb;User Id=sa;Password=Strong!Pwd;TrustServerCertificate=True'
#>
[CmdletBinding()]
param(
    [string]$Server   = '(localdb)\MSSQLLocalDB',
    [string]$Database = 'TradingDb',
    [string]$ConnectionString,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $ConnectionString) {
    $ConnectionString = "Server=$Server;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True;"
}

$root       = $PSScriptRoot
$runnerProj = Join-Path $root 'src/TradingBot.MigrationsRunner/TradingBot.MigrationsRunner.csproj'

if (-not (Test-Path $runnerProj)) {
    throw "Migrations runner project not found at $runnerProj"
}

Write-Host "Resetting and migrating: $ConnectionString" -ForegroundColor Cyan

$dotnetArgs = @('run', '--project', $runnerProj, '-c', 'Release')
if ($NoBuild) { $dotnetArgs += '--no-build' }
$dotnetArgs += @('--', '--reset', '--connection', $ConnectionString)

& dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) {
    throw "Migrations runner exited with code $LASTEXITCODE"
}

Write-Host ""
Write-Host "Done. $Database is ready." -ForegroundColor Green
