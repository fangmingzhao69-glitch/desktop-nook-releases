param([switch]$Execute)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')).TrimEnd('\')
$latest=Join-Path $project '发布\轻匣-v0.12.2'
if(-not(Test-Path -LiteralPath (Join-Path $project '.git')) -or -not(Test-Path -LiteralPath (Join-Path $project 'native\DesktopNook.Core\NookStore.cs'))){throw 'Not the expected project root.'}
[xml]$csproj=Get-Content -LiteralPath (Join-Path $project 'native\DesktopNook.App\DesktopNook.App.csproj') -Raw
if($csproj.Project.PropertyGroup.Version -ne '0.12.2'){throw 'This one-time plan is only for 0.12.2. Re-inventory newer source before cleanup.'}
$hashes=@{'DesktopNook-win-x64.exe'='2609ee4f9fa65129f1639e44587ca8360cd91c19f8c707b774e7f00ea492ed95';'DesktopNook-Setup.exe'='a5314a6aecc50cd82e0134964cc320932e2bdd9243c18f2cd24bf68d0b53cb71'}
function Check-Release {
    foreach($name in $hashes.Keys){if((Get-FileHash -LiteralPath (Join-Path $latest $name)).Hash.ToLowerInvariant() -ne $hashes[$name]){throw 'Retained release checksum mismatch.'}}
}
Check-Release
$plan=@(Get-Content -LiteralPath (Join-Path $project 'docs\验证结果\清理计划.json') -Raw|ConvertFrom-Json)
$dirs=@('原型','test-results','native/artifacts','native/test-results','native/DesktopNook.App/bin','native/DesktopNook.App/obj','native/DesktopNook.Core/bin','native/DesktopNook.Core/obj','native/DesktopNook.Tests/bin','native/DesktopNook.Tests/obj','docs/品牌方案')
$images=@('overview.png','collect.png','more-menu.png','context-menu.png','category-menu.png','update-dialog.png','rename.png','submenu.png','taskbar.png')
$oldDocs=@('家族设计与交互规范-v0.1.md','轻匣品牌与界面落地-v0.12.0.md','轻匣任务栏图标与菜单修复-v0.12.1.md','验证修复记录-2026-11-06.md')
$resolved=[Collections.Generic.List[object]]::new()
foreach($entry in $plan){
    $rel=$entry.Relative.Replace('\','/');$allowed=$rel -in $dirs
    if($rel -match '^发布/([^/]+)$' -and $matches[1] -ne '轻匣-v0.12.2'){$allowed=$true}
    if($rel -match '^([^/]+\.md)$' -and $matches[1] -notin @('README.md','AGENTS.md')){$allowed=$true}
    if($rel -match '^docs/images/([^/]+)$' -and $matches[1] -notin $images){$allowed=$true}
    if($rel -match '^docs/([^/]+)$' -and $matches[1] -in $oldDocs){$allowed=$true}
    if($rel -match '^native/releases/([^/]+\.md)$' -and $matches[1] -ne 'v0.12.2.md'){$allowed=$true}
    $path=[IO.Path]::GetFullPath((Join-Path $project $rel))
    if(-not $allowed -or -not $path.StartsWith($project+'\',[StringComparison]::OrdinalIgnoreCase) -or $path.StartsWith($project+'\.git',[StringComparison]::OrdinalIgnoreCase) -or $path.StartsWith($latest,[StringComparison]::OrdinalIgnoreCase)){throw ('Unsafe target: '+$rel)}
    if(-not(Test-Path -LiteralPath $path)){continue}
    $item=Get-Item -LiteralPath $path -Force
    if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint)-ne 0){throw ('Reparse point: '+$rel)}
    if($item.PSIsContainer -and @(Get-ChildItem -LiteralPath $path -Recurse -Force -Attributes ReparsePoint).Count){throw ('Nested reparse point: '+$rel)}
    $files=if($item.PSIsContainer){@(Get-ChildItem -LiteralPath $path -Recurse -Force -File)}else{@($item)}
    $size=($files|Measure-Object Length -Sum).Sum
    if($files.Count -ne $entry.Files -or $size -ne $entry.Bytes){throw ('Contents changed after inventory; re-review before deleting: '+$rel)}
    $resolved.Add([pscustomobject]@{Path=$path;Relative=$rel;Directory=$item.PSIsContainer;Files=$files.Count;Bytes=$size})
}
function Is-Target([string]$path){
    if(-not $path){return $false}
    $full=[IO.Path]::GetFullPath($path)
    foreach($target in $resolved){if($full.Equals($target.Path,[StringComparison]::OrdinalIgnoreCase)-or $full.StartsWith($target.Path+'\',[StringComparison]::OrdinalIgnoreCase)){return $true}}
    return $false
}
foreach($p in Get-CimInstance Win32_Process){if($p.ExecutablePath -and (Is-Target $p.ExecutablePath)){throw ('A cleanup target is running; close the debugging process first: '+$p.ExecutablePath)}}
$data=Join-Path $env:LOCALAPPDATA 'DesktopNook';$statePath=Join-Path $data 'state.json';$prefs=Join-Path $data 'preferences.json'
$stateHash=if(Test-Path -LiteralPath $statePath){(Get-FileHash -LiteralPath $statePath).Hash}else{$null}
$prefHash=if(Test-Path -LiteralPath $prefs){(Get-FileHash -LiteralPath $prefs).Hash}else{$null}
if(Is-Target $data){throw 'Live data is inside a target; refusing cleanup.'}
if($stateHash){
    $state=Get-Content -LiteralPath $statePath -Raw|ConvertFrom-Json
    foreach($item in $state.Items){if($item.PayloadPath -and (Is-Target $item.PayloadPath)){throw 'Live payload references a cleanup target.'}}
    $shell=New-Object -ComObject WScript.Shell
    try{foreach($item in $state.Items|Where-Object Kind -eq 'shortcut'){
        if($item.Id -notmatch '^[a-fA-F0-9]{32}$' -or [IO.Path]::GetFileName($item.FileName) -ne $item.FileName){throw 'Unexpected shortcut metadata; inspect live data first.'}
        $file=Join-Path (Join-Path (Join-Path $data 'vault') $item.Id) $item.FileName
        if(-not(Test-Path -LiteralPath $file)){continue}
        if([IO.Path]::GetExtension($file) -eq '.lnk'){$link=$shell.CreateShortcut($file);try{if(Is-Target $link.TargetPath){throw 'A collected shortcut points to a cleanup target.'}}finally{[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link)}}
        elseif([IO.Path]::GetExtension($file) -eq '.url'){foreach($line in Get-Content -LiteralPath $file){if($line -match '^URL=(file:.+)$'){$uri=[uri]$matches[1];if(Is-Target $uri.LocalPath){throw 'A collected URL points to a cleanup target.'}}}}
    }}finally{[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)}
}
$summary=[ordered]@{InventoryDate='2026-11-10';RunAt=[DateTimeOffset]::Now.ToString('o');Mode=if($Execute){'execute'}else{'preview'};Targets=$resolved.Count;Files=($resolved|Measure-Object Files -Sum).Sum;GiB=[Math]::Round(($resolved|Measure-Object Bytes -Sum).Sum/1GB,3);RetainedVersion='0.12.2';ReleaseHashesVerified=$true;GitHistoryPreserved=$true}
if(-not $Execute){$summary|ConvertTo-Json;Write-Output 'Preview only. Review docs/验证结果/清理计划.json, then run with -Execute locally.';return}
# All checks are completed before the first deletion. One PowerShell session only.
foreach($target in $resolved){if($target.Directory){Remove-Item -LiteralPath $target.Path -Recurse -Force}else{Remove-Item -LiteralPath $target.Path -Force}}
Check-Release
$summary.StateHashUnchanged=if($stateHash){(Get-FileHash -LiteralPath $statePath).Hash -eq $stateHash}else{$null}
$summary.PreferencesHashUnchanged=if($prefHash){(Get-FileHash -LiteralPath $prefs).Hash -eq $prefHash}else{$null}
$summary.Completed=$true
$summary|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $project 'docs\验证结果\清理结果.json') -Encoding utf8
$summary|ConvertTo-Json
