[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$toolRoot = Join-Path $projectRoot '.tools\inno-6.7.3'
$compiler = Join-Path $toolRoot 'ISCC.exe'
$installer = Join-Path $projectRoot '.tools\installer-cache\innosetup-6.7.3.exe'
$expectedHash = '9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732'
if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Pinned official Inno Setup compiler installer hash mismatch.' }
$signature = Get-AuthenticodeSignature -LiteralPath $installer
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike 'CN=Pyrsys B.V.,*') { throw 'Official Inno Setup Authenticode validation failed.' }
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Extract the verified compiler in portable mode as documented in docs/BUILD.md.' }
$compilerSignature = Get-AuthenticodeSignature -LiteralPath $compiler
if ($compilerSignature.Status -ne 'Valid' -or $compilerSignature.SignerCertificate.Subject -notlike 'CN=Pyrsys B.V.,*') { throw 'Portable Inno compiler Authenticode validation failed.' }
$engineSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $toolRoot 'ISCmplr.dll')
if ($engineSignature.Status -ne 'Valid' -or $engineSignature.SignerCertificate.Subject -notlike 'CN=Pyrsys B.V.,*') { throw 'Portable Inno compiler engine Authenticode validation failed.' }
Write-Output $compiler
