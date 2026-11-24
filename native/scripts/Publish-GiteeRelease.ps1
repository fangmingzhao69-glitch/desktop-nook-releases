param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$')][string]$Repository,
    [Parameter(Mandatory)][ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+$')][string]$Tag,
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [Parameter(Mandatory)][string]$TaskbarComponent
)
$ErrorActionPreference = 'Stop'
$github = 'fangmingzhao69-glitch/desktop-nook-releases'
$published = gh api "repos/$github/releases/tags/$Tag" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or !$published -or $published.draft -or $published.prerelease) {
    throw '未找到对应的 GitHub 正式发行。'
}
$commit = [string]$published.target_commitish
if ($commit -cnotmatch '^[a-f0-9]{40}$') { throw 'GitHub 发行未固定到源码提交。' }

$files = @(
    foreach ($name in @('DesktopNook-Setup.exe','DesktopNook-win-x64.exe',"DesktopNook-$Tag-source.zip",'SHA256SUMS.txt')) {
        $asset = $published.assets | Where-Object name -CEQ $name
        $file = Get-Item -LiteralPath (Join-Path $ArtifactDirectory $name)
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($asset.digest -cne "sha256:$hash" -or $asset.size -ne $file.Length) {
            throw "本地发行资产与 GitHub 不一致：$name"
        }
        $file
    }
)
$component = Get-Item -LiteralPath $TaskbarComponent
$componentHash = (Get-FileHash -LiteralPath $component.FullName -Algorithm SHA256).Hash
if ($component.Name -cne 'Qingxia-TaskbarCore-win-x64.zip' -or $component.Length -ne 837343 -or
    $componentHash -cne '083F2CB805D88CB2A8644A17D29CD5F993FC90D71A14889CDEE6B2C990123A51') {
    throw '任务栏外观附件与客户端固定的发行资产不一致。'
}
$files += $component
$gitRemote = "https://gitee.com/$Repository.git"
$pushedTags = @(git ls-remote $gitRemote "refs/tags/$Tag" "refs/tags/$Tag^{}")
if ($LASTEXITCODE -ne 0 -or $pushedTags.Count -eq 0 -or ($pushedTags[-1] -split '\s+')[0] -cne $commit) {
    throw "请先把源码提交 $commit 以 $Tag 标签推送到这个 Gitee 仓库。"
}

$api = "https://gitee.com/api/v5/repos/$Repository/releases"
$token = $env:GITEE_RELEASE_TOKEN
if ([string]::IsNullOrWhiteSpace($token)) {
    $masked = Read-Host 'Gitee 发布令牌（输入已隐藏）' -AsSecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($masked)
    try { $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        $masked.Dispose()
    }
}
if ([string]::IsNullOrWhiteSpace($token)) { throw '没有提供 Gitee 发布令牌。' }

function Get-Release {
    $request = Invoke-WebRequest -Uri "$api/tags/$Tag" -Headers @{Authorization="token $token"} -SkipHttpErrorCheck -MaximumRedirection 0 -TimeoutSec 25
    if ($request.StatusCode -eq 404) { return }
    if ($request.StatusCode -ne 200) { throw "无法读取 Gitee 发行版（HTTP $($request.StatusCode)）。" }
    return $request.Content | ConvertFrom-Json
}

function Send-Gitee([string]$Method,[string]$RequestUri,[hashtable]$Fields,[switch]$FileUpload) {
    $parameters = @{
        Uri=$RequestUri; Method=$Method; Headers=@{Authorization="token $token"}
        SkipHttpErrorCheck=$true; MaximumRedirection=0; TimeoutSec=90
    }
    if ($FileUpload) { $parameters.Form = $Fields }
    else {
        $parameters.ContentType = 'application/json'
        $parameters.Body = [Text.Encoding]::UTF8.GetBytes(($Fields | ConvertTo-Json -Compress))
    }
    $response = Invoke-WebRequest @parameters
    if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
        throw "Gitee 发布请求失败（HTTP $($response.StatusCode)）。"
    }
    if ($response.Content) { return $response.Content | ConvertFrom-Json }
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ('Zheye-Gitee-Verify-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $release = Get-Release
    $title = "折页 $($Tag.Substring(1))"
    if (!$release) {
        $release = Send-Gitee Post $api @{
            tag_name=$Tag; target_commitish=$commit; name=$title; body=$published.body; prerelease=$true
        }
    }
    if ($release.tag_name -cne $Tag -or ([string]$release.id) -notmatch '^[0-9]+$') {
        throw 'Gitee 发行信息与预期标签不符。'
    }
    foreach ($file in $files) {
        if (!($release.assets | Where-Object name -CEQ $file.Name)) {
            if (!$release.prerelease) { throw '已公开的发行版缺少附件，请用新版本重新发布。' }
            Send-Gitee Post "$api/$($release.id)/attach_files" @{ file=$file } -FileUpload | Out-Null
        }
        $download = Join-Path $temp $file.Name
        Invoke-WebRequest -Uri "https://gitee.com/$Repository/releases/download/$Tag/$($file.Name)" -OutFile $download -TimeoutSec 60
        if ((Get-FileHash -LiteralPath $download).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
            throw "Gitee 匿名下载验证未通过：$($file.Name)"
        }
    }
    if ($release.prerelease) {
        Send-Gitee Patch "$api/$($release.id)" @{
            tag_name=$Tag; target_commitish=$commit; name=$title; body=$published.body; prerelease=$false
        } | Out-Null
    }
    $visible = Get-Release
    if (!$visible -or $visible.prerelease) { throw 'Gitee 发行版尚未转为正式版。' }
    "https://gitee.com/$Repository/releases/tag/$Tag"
}
finally {
    $token = $null
    Get-ChildItem -LiteralPath $temp -File | Remove-Item -Force
    Remove-Item -LiteralPath $temp
}
