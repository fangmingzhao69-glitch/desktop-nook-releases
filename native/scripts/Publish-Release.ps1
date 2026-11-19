param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$Installer,
    [Parameter(Mandatory=$true)][string]$NotesFile,
    [string]$Repository='fangmingzhao69-glitch/desktop-nook-releases',
    [string[]]$AdditionalAsset=@(),
    [string]$SourceBranch='main',
    [switch]$DraftOnly,
    [switch]$VerifyOnly
)
$ErrorActionPreference='Stop'
if($DraftOnly -and $VerifyOnly){throw '请选择创建草稿或校验模式。'}
if($SourceBranch -notmatch '^(main|release/[0-9]+\.[0-9]+\.[0-9]+)$'){throw '发布源码分支名称无效。'}
if($SourceBranch -ne 'main' -and -not ($DraftOnly -or $VerifyOnly)){throw '公开正式版前必须由 GitHub main 提供构建提交。'}
$Executable=(Resolve-Path -LiteralPath $Executable).Path
$Installer=(Resolve-Path -LiteralPath $Installer).Path
$NotesFile=(Resolve-Path -LiteralPath $NotesFile).Path
$exeInfo=[Diagnostics.FileVersionInfo]::GetVersionInfo($Executable)
$setupInfo=[Diagnostics.FileVersionInfo]::GetVersionInfo($Installer)
$version=([version]$exeInfo.FileVersion).ToString(3)
if($exeInfo.ProductName -ne 'DesktopNook' -or $setupInfo.ProductName -ne '桌面小筑' -or ([version]$setupInfo.FileVersion).ToString(3) -ne $version){throw '程序与安装包版本或产品标识不一致。'}
$tag='v'+$version
$sourceRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$sourceCommit=(& git -C $sourceRoot rev-parse HEAD).Trim()
if($LASTEXITCODE -ne 0){throw '无法读取源码提交。'}
if(-not $VerifyOnly) {
    $pending=& git -C $sourceRoot status --porcelain
    if($LASTEXITCODE -ne 0 -or $pending){throw '请先提交并推送所有源码修改，再构建发布。'}
    if(-not $exeInfo.ProductVersion.EndsWith('+'+$sourceCommit,[StringComparison]::OrdinalIgnoreCase)){throw '程序不是从当前提交构建，请从已提交源码重新构建。'}
}
$stage=Join-Path $PSScriptRoot ('..\artifacts\github-release\'+$tag)
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$binary=Join-Path $stage 'DesktopNook-win-x64.exe'
$setup=Join-Path $stage 'DesktopNook-Setup.exe'
Copy-Item -LiteralPath $Executable -Destination $binary -Force
Copy-Item -LiteralPath $Installer -Destination $setup -Force
$assetNames=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($name in @('DesktopNook-win-x64.exe','DesktopNook-Setup.exe','SHA256SUMS.txt')){[void]$assetNames.Add($name)}
$extra=@(foreach($item in $AdditionalAsset){
    $source=(Resolve-Path -LiteralPath $item).Path
    $name=[IO.Path]::GetFileName($source)
    if($name -notmatch '^[A-Za-z0-9._-]+\.zip$' -or !$assetNames.Add($name)){throw "无法使用发行附件名称：$name"}
    $destination=Join-Path $stage $name
    Copy-Item -LiteralPath $source -Destination $destination -Force
    $destination
})
$payloadFiles=@($binary,$setup)+$extra
$hashes=$payloadFiles|ForEach-Object{(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($_)}
$checksum=Join-Path $stage 'SHA256SUMS.txt'
[IO.File]::WriteAllLines($checksum,$hashes,[Text.UTF8Encoding]::new($false))
$releaseFiles=$payloadFiles+@($checksum)
$priorProxy=$env:HTTPS_PROXY
try {
    if([string]::IsNullOrWhiteSpace($env:HTTPS_PROXY)) {
        $site=[uri]'https://github.com'
        $proxy=[Net.WebRequest]::GetSystemWebProxy().GetProxy($site)
        if($proxy.AbsoluteUri -ne $site.AbsoluteUri){$env:HTTPS_PROXY=$proxy.AbsoluteUri}
    }
    $visibleRepository=& gh api "repos/$Repository" --jq .full_name
    if($LASTEXITCODE -ne 0 -or $visibleRepository -ne $Repository){throw "无法确认既有发布仓库 $Repository。请检查权限或网络后重试。"}
    $releases=& gh api "repos/$Repository/releases?per_page=100" | ConvertFrom-Json
    if($LASTEXITCODE -ne 0){throw '无法读取发行版列表。'}
    if(-not $VerifyOnly) {
        $branchRef=[Uri]::EscapeDataString($SourceBranch)
        $remoteCommit=& gh api "repos/$Repository/commits/$branchRef" --jq .sha
        if($LASTEXITCODE -ne 0 -or $remoteCommit.Trim() -ne $sourceCommit){throw "当前提交尚未推送到 GitHub $SourceBranch，或远程已有新提交。"}
    }
    $existing=$releases|Where-Object tag_name -eq $tag
    if($VerifyOnly -and -not $existing){throw '待核验的发行版不存在。'}
    if($existing -and -not $existing.draft -and -not $VerifyOnly){throw '该版本已公开，请使用新版本号，不覆盖已发布文件。'}
    # Publish only the explicit files, after all uploads and hashes succeed.
    if(-not $existing) {
        & gh release create $tag @releaseFiles --repo $Repository --target $sourceCommit --draft --title ('一处 '+$version) --notes-file $NotesFile
        if($LASTEXITCODE -ne 0){throw '发行包上传未完成，草稿保留，可再次运行本脚本续传。'}
    } elseif($existing.draft -and -not $VerifyOnly) {
        if($existing.target_commitish -ne $sourceCommit){throw '已有草稿绑定其他源码提交，请检查后使用新版本号。'}
        foreach($file in $releaseFiles) {
            if(-not ($existing.assets|Where-Object name -eq ([IO.Path]::GetFileName($file)))) {
                & gh release upload $tag $file --repo $Repository
                if($LASTEXITCODE -ne 0){throw '发行包续传失败，草稿保留。'}
            }
        }
    }
    $release=@(& gh api "repos/$Repository/releases" | ConvertFrom-Json)|Where-Object tag_name -eq $tag
    if($LASTEXITCODE -ne 0){throw '无法核对上传文件。'}
    if(!$release -or $release.assets.Count -ne $releaseFiles.Count){throw '发行资产数量与清单不一致。'}
    foreach($file in $releaseFiles) {
        $asset=$release.assets|Where-Object name -eq ([IO.Path]::GetFileName($file))
        $expected='sha256:'+(Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant()
        if($asset.digest -ne $expected -or $asset.size -ne (Get-Item -LiteralPath $file).Length){throw '上传后文件大小或 SHA-256 不一致，发行版保留为草稿。'}
    }
    if($VerifyOnly){Write-Output "发布文件校验通过：https://github.com/$Repository/releases/tag/$tag";return}
    if($DraftOnly){Write-Output $release.html_url;return}
    & gh release edit $tag --repo $Repository --draft=false --latest
    if($LASTEXITCODE -ne 0){throw '文件已上传，但发行版尚未公开。'}
    Write-Output "https://github.com/$Repository/releases/tag/$tag"
} finally {$env:HTTPS_PROXY=$priorProxy}
