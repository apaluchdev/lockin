<#
.SYNOPSIS
  Builds artifacts\LockIn.exe: a single self-contained executable (no .NET install needed).
.EXAMPLE
  ./tools/publish.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

dotnet test (Join-Path $root 'LockIn.slnx') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not publishing.' }

dotnet publish (Join-Path $root 'src\LockIn.App\LockIn.App.csproj') -p:PublishProfile=win-x64 -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

$exe = Join-Path $root 'artifacts\LockIn.exe'
Write-Host ("Published {0} ({1:N1} MB)" -f $exe, ((Get-Item $exe).Length / 1MB))
