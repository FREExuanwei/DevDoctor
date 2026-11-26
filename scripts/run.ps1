[CmdletBinding()]
param([ValidateSet('1.2.0')][string]$Version = '1.2.0', [switch]$FromSource)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$portableSdk = Join-Path $projectRoot '.tools\dotnet'
if (Test-Path -LiteralPath (Join-Path $portableSdk 'dotnet.exe')) { $env:DOTNET_ROOT = $portableSdk }
$published = Join-Path $projectRoot "artifacts\DevDoctor-v$Version-win-x64-portable\DevDoctor.exe"
$appPath = if (-not $FromSource -and (Test-Path -LiteralPath $published)) { $published } else { Join-Path $projectRoot 'src\DevDoctor.App\bin\Debug\net10.0-windows\DevDoctor.exe' }
if (!(Test-Path -LiteralPath $appPath)) { throw '请先运行 scripts/build.ps1 或 dotnet build DevDoctor.slnx。' }
Start-Process -FilePath $appPath -WorkingDirectory $projectRoot -WindowStyle Hidden
