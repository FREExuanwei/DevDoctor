[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PayloadDirectory)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$payload=(Resolve-Path -LiteralPath $PayloadDirectory).Path
$lines=[Collections.Generic.List[string]]::new()
$lines.Add('// Generated from this exact Release payload. No paths or user data are embedded.')
$lines.Add('function IsShippedPayloadPrefix(const FileBytes: Int64; const PrefixHash: String): Boolean;')
$lines.Add('begin')
$first=$true
foreach($file in Get-ChildItem -LiteralPath $payload -File | Where-Object {$_.Extension -in '.dll','.exe' -and $_.Length -ge 65536 -and $_.Length -le 67108864} | Sort-Object Name) {
    if($file.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Payload fingerprinting refuses reparse points.'}
    $input=[IO.File]::OpenRead($file.FullName)
    try {
        $prefix=[byte[]]::new(65536)
        $input.ReadExactly($prefix,0,$prefix.Length)
        $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($prefix)).ToLowerInvariant()
        $prefixText=if($first){'  Result :='}else{'    or'}
        $lines.Add("$prefixText ((FileBytes = $($file.Length)) and (PrefixHash = '$hash')) // $($file.Name)")
        $first=$false
    }finally{$input.Dispose()}
}
if($first){throw 'No payload binaries were available to fingerprint.'}
$lines.Add('  ;')
$lines.Add('end;')
$destination=Join-Path $projectRoot 'installer\PayloadFingerprints.iss'
[IO.File]::WriteAllLines($destination,$lines,[Text.UTF8Encoding]::new($false))
