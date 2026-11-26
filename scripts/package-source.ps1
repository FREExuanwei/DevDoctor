[CmdletBinding()]
param([ValidateSet('1.1.0','1.2.0')][string]$Version = '1.2.0')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Version -ne '1.2.0') { throw 'Current source owns only v1.2.0 outputs; archived v1.1 source/packages are preserved.' }
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactRoot = Join-Path $projectRoot 'artifacts'
$evidenceRoot = Join-Path $artifactRoot "release-evidence-v$Version"
function Assert-UnlinkedSourceAncestors([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    $prefix = $projectRoot.TrimEnd('\') + '\'
    if ($absolute -ne $projectRoot -and -not $absolute.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw "Source path escaped project root: $absolute" }
    for ($ancestor = $absolute; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
        $item = $null
        try { $item = Get-Item -LiteralPath $ancestor -Force -ErrorAction Stop }
        catch [Management.Automation.ItemNotFoundException] { }
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Refusing linked source/output ancestor: $ancestor" }
    }
}
function Get-ReviewedSourceFiles([string]$ChecksumFile) {
    Assert-UnlinkedSourceAncestors $ChecksumFile
    $listed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in Get-Content -LiteralPath $ChecksumFile) {
        if ($line -notmatch '^([0-9a-fA-F]{64})  ([^\\]+)$') { throw 'Invalid reviewed source checksum line.' }
        $expected = $Matches[1]
        $relative = $Matches[2]
        if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|/)\.{1,2}(/|$)' -or
            $relative -eq 'SHA256SUMS.txt' -or -not $listed.Add($relative)) {
            throw 'Invalid or duplicate reviewed source path.'
        }
        $path = Join-Path $projectRoot $relative
        Assert-UnlinkedSourceAncestors $path
        $file = Get-Item -LiteralPath $path -Force
        if ($file.PSIsContainer -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $expected) {
            throw "Reviewed source differs from its checksum: $relative"
        }
        $file
    }
    if ($listed.Count -eq 0) { throw 'The reviewed source checksum list is empty.' }
    Get-Item -LiteralPath $ChecksumFile -Force
}


function Get-SourceFiles {
    $review = Join-Path $projectRoot 'PUBLIC-RELEASE-AUDIT.md'
    $checksums = Join-Path $projectRoot 'SHA256SUMS.txt'
    if (Test-Path -LiteralPath $review) {
        return Get-ReviewedSourceFiles $checksums
    }
    $files = [Collections.Generic.List[IO.FileInfo]]::new()
    foreach ($name in @('.gitignore','DevDoctor.cmd','DevDoctor.slnx','Directory.Build.props','global.json','NuGet.Config','README.md','THIRD-PARTY-NOTICES.md')) {
        $path = Join-Path $projectRoot $name
        Assert-UnlinkedSourceAncestors $path
        $file = Get-Item -LiteralPath $path -Force
        if ($file.PSIsContainer) { throw "Expected a source file: $path" }
        $files.Add($file)
    }
    foreach ($name in @('.gitattributes','LICENSE','SECURITY.md','config/settings.example.json')) {
        $path = Join-Path $projectRoot $name
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            Assert-UnlinkedSourceAncestors $path
            $files.Add((Get-Item -LiteralPath $path -Force))
        }
    }
    $directories = [Collections.Generic.Stack[string]]::new()
    foreach ($name in @('src','tests','scripts','installer','docs','licenses')) {
        $path = Join-Path $projectRoot $name
        Assert-UnlinkedSourceAncestors $path
        if (-not (Get-Item -LiteralPath $path -Force).PSIsContainer) { throw "Expected a source directory: $path" }
        $directories.Push($path)
    }
    while ($directories.Count) {
        $directory = $directories.Pop()
        Assert-UnlinkedSourceAncestors $directory
        # Inspect every immediate child before considering exclusions. Never recurse
        # through a junction, even an empty directory or an excluded bin/cache name.
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            Assert-UnlinkedSourceAncestors $item.FullName
            $relative = [IO.Path]::GetRelativePath($projectRoot,$item.FullName).Replace('\','/')
            if ($relative -match '^docs/v12-work(/|$)') { continue }
            if ($relative -match '(^|/)(bin|obj|TestResults|artifacts|user-data|closed-seed|legacy-runtime|\.git|\.vs|\.tools|node_modules|__pycache__|cache)(/|$)') { continue }
            if ($item.PSIsContainer) { $directories.Push($item.FullName); continue }
            if ($item.Name -match '^(?:\.env(?:\..*)?|settings\.json|secrets\.json|credentials\.json|restore\.pending\.json|id_(?:rsa|dsa|ecdsa|ed25519))$|-(?:wal|shm|journal)$' -or
                $item.Extension -in @('.key','.pfx','.p12','.pem','.dmp','.mdmp','.trx','.binlog')) { continue }
            if ($item.Extension -in @('.dll','.exe','.pdb','.sqlite','.sqlite3','.db','.zip','.cache','.tmp','.log')) { continue }
            $files.Add($item)
        }
    }
    return $files
}
foreach ($folder in @($projectRoot,$artifactRoot,$evidenceRoot)) {
    Assert-UnlinkedSourceAncestors $folder
}
New-Item -ItemType Directory -Path $artifactRoot,$evidenceRoot -Force | Out-Null
$files = @(Get-SourceFiles)
$zipPath = Join-Path $artifactRoot "DevDoctor-v$Version-source.zip"
$stagingPath = Join-Path $artifactRoot ('.source-staging-' + [Guid]::NewGuid().ToString('N') + '.zip')
$rows = [Collections.Generic.List[object]]::new()
$zip = [IO.Compression.ZipFile]::Open($stagingPath,[IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files | Sort-Object FullName) {
        Assert-UnlinkedSourceAncestors $file.FullName
        $relative = [IO.Path]::GetRelativePath($projectRoot,$file.FullName).Replace('\','/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,$relative,[IO.Compression.CompressionLevel]::Optimal)
        $rows.Add([ordered]@{path=$relative;bytes=$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()})
    }
} finally { $zip.Dispose() }
$zip = [IO.Compression.ZipFile]::OpenRead($stagingPath)
try {
    if ($zip.Entries.Count -ne $rows.Count) { throw 'Source ZIP entry count differs from the source manifest.' }
    foreach ($row in $rows) {
        $entry = $zip.GetEntry($row.path)
        if (-not $entry -or $entry.Length -ne $row.bytes) { throw "Source ZIP is incomplete: $($row.path)" }
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        if ($hash -ne $row.sha256) { throw "Source ZIP contents differ: $($row.path)" }
    }
} finally { $zip.Dispose() }
# The ZIP must correspond to the entire eligible workspace at publication, not
# merely to hashes captured earlier while other entries were still being copied.
# Any drift fails before the previous ZIP or verified manifest can be replaced.
try {
    $currentFiles = @(Get-SourceFiles)
    if ($currentFiles.Count -ne $rows.Count) { throw 'eligible source file count differs' }
    $captured = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($row in $rows) { $captured.Add($row.path,$row) }
    foreach ($file in $currentFiles) {
        $relative = [IO.Path]::GetRelativePath($projectRoot,$file.FullName).Replace('\','/')
        if (-not $captured.ContainsKey($relative)) { throw "eligible source path differs: $relative" }
        $row = $captured[$relative]
        Assert-UnlinkedSourceAncestors $file.FullName
        $file.Refresh()
        if ($file.Length -ne $row.bytes -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $row.sha256) { throw "source length/SHA256 differs: $relative" }
    }
} catch { throw "Source changed during packaging; prior ZIP and manifest retained: $($_.Exception.Message)" }
if (Test-Path -LiteralPath $zipPath) {
    if ((Get-Item -LiteralPath $zipPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing a linked existing source archive.' }
    $archiveRoot = Join-Path $artifactRoot ('release-archive\source-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
    $allowed = [IO.Path]::GetFullPath($artifactRoot).TrimEnd('\') + '\'
    if (-not [IO.Path]::GetFullPath($archiveRoot).StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Archive directory escaped artifacts.' }
    New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null
    Move-Item -LiteralPath $zipPath -Destination (Join-Path $archiveRoot ([IO.Path]::GetFileName($zipPath)))
}
Move-Item -LiteralPath $stagingPath -Destination $zipPath
$hashLine = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($zipPath)
$hashPath = Join-Path $artifactRoot 'SHA256SUMS.txt'
$retained = if (Test-Path -LiteralPath $hashPath) { @(Get-Content -LiteralPath $hashPath | Where-Object { $_ -notmatch ('  DevDoctor-v' + [regex]::Escape($Version) + '-source\.zip$') }) } else { @() }
@($retained) + @($hashLine) | Set-Content -LiteralPath $hashPath -Encoding ascii
[ordered]@{version=$Version;utc=[DateTime]::UtcNow.ToString('o');verified=$true;files=$rows.Count;zip=$zipPath;sha256=$hashLine;entries=$rows} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'source-manifest.json') -Encoding utf8
Write-Output "Source: $zipPath ($($rows.Count) files; every extracted entry verified)"
