param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$DependencyArchiveDirectory,
    [string]$OutputDirectory=(Join-Path $PSScriptRoot '..\artifacts\taskbar-release')
)
$ErrorActionPreference='Stop'
if($Version -notmatch '^\d+\.\d+\.\d+$'){throw 'Expected major.minor.patch'}
$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
[void](New-Item -ItemType Directory -Force -Path $OutputDirectory)
$destination=(Resolve-Path $OutputDirectory).Path
$prefix="DesktopNook-v$Version"
$work=Join-Path $destination ("source-"+[guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $work)
$snapshot=Join-Path $work 'git-source.zip'
git -C $root archive --format=zip "--prefix=$prefix/" "--output=$snapshot" HEAD
if($LASTEXITCODE -ne 0){throw 'Could not export committed release files'}
Expand-Archive -LiteralPath $snapshot -DestinationPath $work
$tree=Join-Path $work $prefix
$dependencies=Join-Path $tree 'dependencies'
[void](New-Item -ItemType Directory -Path $dependencies)
foreach($name in @('detours.tar.gz','member-thunk.tar.gz','rapidjson.tar.gz','spdlog.tar.gz','wil.zip')){
    Copy-Item -LiteralPath (Join-Path $DependencyArchiveDirectory $name) -Destination $dependencies
}
$target=Join-Path $destination ("$prefix-source.zip")
tar.exe -a -cf $target -C $work $prefix
if($LASTEXITCODE -ne 0){throw 'Could not package corresponding source'}
Get-Item -LiteralPath $target | Select-Object FullName,Length
Get-FileHash -LiteralPath $target
