# Read-only snapshots. Include hidden files, directories and missing roots; never restore or delete user files.
function Assert-UnlinkedPathAncestors([string[]]$Paths) {
    foreach($path in $Paths) {
        for($ancestor=[IO.Path]::GetFullPath($path);$ancestor;$ancestor=[IO.Path]::GetDirectoryName($ancestor)) {
            if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "Refusing a linked test path: $ancestor"}
        }
    }
}
function Get-ExactPathSnapshot([string[]]$Paths) {
    $result=[ordered]@{}
    foreach($path in $Paths | Sort-Object -Unique) {
        $absolute=[IO.Path]::GetFullPath($path).TrimEnd('\')
        for($ancestor=$absolute;$ancestor;$ancestor=[IO.Path]::GetDirectoryName($ancestor)) {
            if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {throw "Protected path contains a link: $ancestor"}
        }
        if(-not (Test-Path -LiteralPath $absolute)) {$result[$absolute]=@{exists=$false};continue}
        $root=Get-Item -LiteralPath $absolute -Force
        $entries=@($root)
        if($root.PSIsContainer){$entries+=@(Get-ChildItem -LiteralPath $absolute -Force -Recurse -ErrorAction Stop)}
        $rows=@(foreach($entry in $entries | Sort-Object FullName) {
            if($entry.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Protected tree contains a link: $($entry.FullName)"}
            [ordered]@{path=[IO.Path]::GetRelativePath($absolute,$entry.FullName);directory=[bool]$entry.PSIsContainer;
                attributes=[int]$entry.Attributes;bytes=$(if($entry.PSIsContainer){$null}else{$entry.Length});
                sha256=$(if($entry.PSIsContainer){$null}else{(Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash})}
        })
        $result[$absolute]=[ordered]@{exists=$true;entries=$rows}
    }
    return $result
}
function Assert-ExactSnapshot([object]$Before,[object]$After,[string]$Name) {
    if(($Before | ConvertTo-Json -Depth 10 -Compress) -cne ($After | ConvertTo-Json -Depth 10 -Compress)) {throw "$Name changed; preserve evidence and review before continuing."}
}
function Get-ShellPinSnapshot([string]$RealLocalAppData) {
    $startRoot=Join-Path $RealLocalAppData 'Packages\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\LocalState'
    $startFiles=if(Test-Path -LiteralPath $startRoot){@(Get-ChildItem -LiteralPath $startRoot -Force -File -Filter 'start*.bin' | Sort-Object Name)}else{@()}
    $pinRoot=Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Microsoft\Internet Explorer\Quick Launch\User Pinned'
    $fileSnapshot=Get-ExactPathSnapshot (@($pinRoot)+@($startFiles | ForEach-Object FullName))
    $values=[ordered]@{}
    $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,[Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key=$base.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband')
        $exists=$null -ne $key
        if($key){try{foreach($name in $key.GetValueNames() | Sort-Object) {
            $value=$key.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            $hash=if($value -is [byte[]]){[Security.Cryptography.SHA256]::HashData($value)}else{[Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes([string]($value | ConvertTo-Json -Compress)))}
            $values[$name]=[ordered]@{kind=$key.GetValueKind($name).ToString();sha256=[Convert]::ToHexString([byte[]]$hash)}
        }}finally{$key.Dispose()}}
    }finally{$base.Dispose()}
    return [ordered]@{startRootExists=(Test-Path -LiteralPath $startRoot);startFiles=@($startFiles | ForEach-Object Name);files=$fileSnapshot;taskbandExists=$exists;taskbandValues=$values}
}
function Get-TaskbarAndLegacyPinState([object]$Snapshot,[string]$RealLocalAppData) {
    $startPrefix=[IO.Path]::GetFullPath((Join-Path $RealLocalAppData 'Packages\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\LocalState')).TrimEnd('\')+'\'
    $pinFiles=[ordered]@{}
    foreach($path in $Snapshot.files.Keys | Sort-Object) {
        # start*.bin is an opaque Windows state file, observed to change on app launch
        # before any uninstall. Preserve its hashes as evidence, not a pin-layout assertion.
        if(-not $path.StartsWith($startPrefix,[StringComparison]::OrdinalIgnoreCase)){$pinFiles[$path]=$Snapshot.files[$path]}
    }
    return [ordered]@{files=$pinFiles;taskbandExists=$Snapshot.taskbandExists;taskbandValues=$Snapshot.taskbandValues}
}
