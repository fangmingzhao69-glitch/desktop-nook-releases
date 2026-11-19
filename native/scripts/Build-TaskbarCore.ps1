param(
    [string]$OutputDirectory=(Join-Path $PSScriptRoot '..\artifacts\taskbar-release'),
    [string]$DependencyArchiveDirectory,
    [string]$NuGetPackagesDirectory,
    [switch]$WithoutSpectreLibraries
)
$ErrorActionPreference='Stop'
$vendor=(Resolve-Path (Join-Path $PSScriptRoot '..\TaskbarCore\upstream')).Path
# C++/WinRT's SDK metadata paths must fit the older Windows path limit.
$source=Join-Path $env:TEMP ('QxCore-'+[guid]::NewGuid().ToString('N').Substring(0,8))
robocopy.exe $vendor $source /E /XJ /XD packages taskbar-build-deps x64 'Generated Files' /NFL /NDL /NJH /NJS /NP
if($LASTEXITCODE -ge 8){throw 'Could not stage the native source.'}
if($NuGetPackagesDirectory){
    $packagesRoot=(Resolve-Path -LiteralPath $NuGetPackagesDirectory).Path
    [void](New-Item -ItemType Junction -Path (Join-Path $source 'packages') -Target $packagesRoot)
}
$deps=Join-Path $source 'taskbar-build-deps'
$archives=Join-Path $deps 'archives'
[void](New-Item -ItemType Directory -Force -Path $archives)
function Get-Archive([string]$file,[string]$uri,[string]$sha) {
    $target=Join-Path $archives $file
    if(!(Test-Path -LiteralPath $target)){
        $seed=if($DependencyArchiveDirectory){Join-Path $DependencyArchiveDirectory $file}
        if($seed -and (Test-Path -LiteralPath $seed)){
            Copy-Item -LiteralPath $seed -Destination $target
        }else{
            Invoke-WebRequest -Uri $uri -OutFile $target
        }
    }
    if($sha -and (Get-FileHash -LiteralPath $target).Hash -ne $sha){throw "Dependency archive checksum mismatch: $target"}
    $target
}
$recipe=@(
    @('detours.tar.gz','https://codeload.github.com/microsoft/Detours/tar.gz/4b8c659f549b0ab21cf649377c7a84eb708f5e68','3242840029EA5EBFE9E269A4E731B608A9FD7226AAAAB519F34217DE089D05FD','Detours-4b8c659f549b0ab21cf649377c7a84eb708f5e68'),
    @('member-thunk.tar.gz','https://codeload.github.com/sylveon/member_thunk/tar.gz/3acf1402af87de495f073233c9b842c230a7fb12','4F506801EFF016194AA0A8C9B6F26CDB916B7A8EECB3C7116E46AC5555734A4F','member_thunk-3acf1402af87de495f073233c9b842c230a7fb12'),
    @('rapidjson.tar.gz','https://codeload.github.com/Tencent/rapidjson/tar.gz/24b5e7a8b27f42fa16b96fc70aade9106cf7102f','2D2601A82D2D3B7E143A3C8D43EF616671391034BC46891A9816B79CF2D3E7A8','rapidjson-24b5e7a8b27f42fa16b96fc70aade9106cf7102f'),
    @('spdlog.tar.gz','https://codeload.github.com/gabime/spdlog/tar.gz/refs/tags/v1.16.0','8741753E488A78DD0D0024C980E1FB5B5C85888447E309D9CB9D949BDB52AA3E','spdlog-1.16.0')
)
foreach($entry in $recipe){
    $archive=Get-Archive $entry[0] $entry[1] $entry[2]
    if(!(Test-Path -LiteralPath (Join-Path $deps $entry[3]))){
        tar.exe -xzf $archive -C $deps
        if($LASTEXITCODE -ne 0){throw "Could not extract $archive"}
    }
}
$flat='https://api.nuget.org/v3-flatcontainer'
$wilName='microsoft.windows.implementationlibrary'
$wilVersion='1.0.260126.7'
$wil=Get-Archive 'wil.zip' "$flat/$wilName/$wilVersion/$wilName.$wilVersion.nupkg" '7D67BC71DD0EDC342DB44EA0334AC9B6A1776514F81694B0B5B3C7AC9D608E42'
$wilDirectory=Join-Path $deps 'wil-package'
if(!(Test-Path -LiteralPath $wilDirectory)){Expand-Archive -LiteralPath $wil -DestinationPath $wilDirectory}
$projectDirectories=@('ExplorerHooks','ExplorerTAP','ProgramLog','TranslucentTB','WindowsUdk')
$packages=[ordered]@{}
foreach($directory in $projectDirectories){
    $config=[xml][IO.File]::ReadAllText((Join-Path $source "$directory/packages.config"))
    foreach($package in $config.packages.package){$packages["$($package.id).$($package.version)"]=$package}
}
foreach($entry in $packages.GetEnumerator()){
    $destination=Join-Path $source "packages/$($entry.Key)"
    if(Test-Path -LiteralPath $destination){continue}
    $item=$entry.Value
    $id=$item.id.ToLowerInvariant()
    $version=$item.version.ToLowerInvariant()
    $archive=Get-Archive "$($entry.Key).zip" "$flat/$id/$version/$id.$version.nupkg" ''
    Expand-Archive -LiteralPath $archive -DestinationPath $destination
}
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if($LASTEXITCODE -ne 0 -or !$vs){throw 'Visual Studio with the x64 Desktop C++ workload is required.'}
& (Join-Path $vs 'Common7/Tools/Launch-VsDevShell.ps1') -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
$detours=Join-Path $deps $recipe[0][3]
$include=Join-Path $deps 'include/detours'
[void](New-Item -ItemType Directory -Path $include -Force)
Copy-Item -LiteralPath @((Join-Path $detours 'src/detours.h'),(Join-Path $detours 'src/detver.h')) -Destination $include -Force
Push-Location (Join-Path $detours 'src')
try {
    nmake.exe /nologo DETOURS_CONFIG=Release 'CFLAGS=/nologo /W4 /WX /we4777 /we4800 /Zi /MT /Gy /Gm- /Zl /Od /DDETOUR_DEBUG=0 /DWIN32_LEAN_AND_MEAN /D_WIN32_WINNT=0x501 /guard:cf /guard:ehcont /ZH:SHA_256 /QIntel-jcc-erratum'
    if($LASTEXITCODE -ne 0){throw 'Detours build failed.'}
} finally {Pop-Location}
$msbuild=Join-Path $vs 'MSBuild/Current/Bin/MSBuild.exe'
$options=@('/p:Platform=x64','/p:Configuration=Release','/p:BuildType=Dev','/p:VcpkgEnabled=false','/m','/nologo','/v:minimal')
if($WithoutSpectreLibraries){$options+='/p:SpectreMitigation=false'}
foreach($project in @('ExplorerHooks/ExplorerHooks.vcxproj','ExplorerTAP/ExplorerTAP.vcxproj','TranslucentTB/QingxiaTaskbarCore.vcxproj')){
    & $msbuild (Join-Path $source $project) @options
    if($LASTEXITCODE -ne 0){throw "Native build failed: $project"}
}
$files=@(
    'ExplorerHooks/x64/Release/ExplorerHooks.dll',
    'ExplorerTAP/x64/Release/ExplorerTAP.dll',
    'ProgramLog/x64/Release/ProgramLog.dll',
    'TranslucentTB/x64/Release/QingxiaTaskbarCore.dll'
) | ForEach-Object {Join-Path $source $_}
[void](New-Item -ItemType Directory -Force -Path $OutputDirectory)
$payload=Join-Path $OutputDirectory 'Qingxia-TaskbarCore-win-x64.zip'
$license=Join-Path $OutputDirectory 'LICENSE.txt'
$notices=Join-Path $OutputDirectory 'THIRD-PARTY-NOTICES.txt'
Copy-Item -LiteralPath (Join-Path $source 'LICENSE.md') -Destination $license -Force
$noticeRoot=(Resolve-Path (Join-Path $PSScriptRoot '../ThirdPartyNotices')).Path
$noticeText=@(foreach($name in @('Detours-MIT.txt','WIL-MIT.txt','RapidJSON-License.txt','spdlog-MIT.txt','member_thunk-MIT.txt')){
    $name
    Get-Content -LiteralPath (Join-Path $noticeRoot $name) -Raw
}) -join ([Environment]::NewLine * 2)
[IO.File]::WriteAllText([IO.Path]::GetFullPath($notices),$noticeText)
Compress-Archive -LiteralPath ($files+@($license,$notices)) -DestinationPath $payload -CompressionLevel Optimal -Force
Get-Item -LiteralPath $payload | Select-Object FullName,Length
Get-FileHash -LiteralPath $payload
$files | ForEach-Object {Get-FileHash -LiteralPath $_}
