$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $projectRoot 'DevDoctor.slnx'
& "$PSScriptRoot\dotnet.ps1" restore $solution
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& "$PSScriptRoot\dotnet.ps1" build $solution --no-restore --configuration Debug
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& "$PSScriptRoot\dotnet.ps1" test $solution --no-restore --configuration Debug
exit $LASTEXITCODE
