<#
.SYNOPSIS
  Runs the tests, publishes LockIn.exe, and builds the per-user installer in artifacts\installer.
.EXAMPLE
  ./tools/build-installer.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

& (Join-Path $PSScriptRoot 'publish.ps1')

dotnet build (Join-Path $root 'installer\LockIn.Installer.wixproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }

$msi = Get-ChildItem (Join-Path $root 'artifacts\installer\*.msi') | Sort-Object LastWriteTime | Select-Object -Last 1
Write-Host ("Built {0} ({1:N1} MB)" -f $msi.FullName, ($msi.Length / 1MB))
Write-Host ("SHA-256: {0}" -f (Get-FileHash $msi.FullName).Hash)
