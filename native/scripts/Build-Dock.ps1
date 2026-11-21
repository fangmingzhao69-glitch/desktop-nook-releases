param([switch]$Tests, [string]$CMakePath)
$ErrorActionPreference = 'Stop'
$nativeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = Join-Path $nativeRoot 'FloatingTaskbar/native'
# Keep C++/WinRT paths short; only generated files go into TEMP.
$build = Join-Path $env:TEMP 'ZheyeDock-build-v1'
$output = Join-Path $nativeRoot 'artifacts/dock'
$archive = Join-Path $output 'ZheyeDock.zip'
$stamp = Join-Path $output 'source.sha256'
$inputs = @(Get-ChildItem (Join-Path $nativeRoot 'FloatingTaskbar') -Recurse -File | Sort-Object FullName)
$hashText = ($inputs | ForEach-Object { $_.FullName.Substring($nativeRoot.Length) + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }) -join "`n"
$hashText += [IO.File]::ReadAllText($PSCommandPath)
$fingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($hashText)))
if (!$Tests -and (Test-Path -LiteralPath $archive) -and (Test-Path -LiteralPath $stamp) -and ([IO.File]::ReadAllText($stamp) -eq $fingerprint)) { return }
$stagedSource = Join-Path $env:TEMP 'ZheyeDock-source'
robocopy.exe (Join-Path $nativeRoot 'FloatingTaskbar') $stagedSource /E /XJ /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'Could not stage floating taskbar source.' }
$source = Join-Path $stagedSource 'native'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Building the floating taskbar requires Visual Studio Desktop C++ tools.' }
& (Join-Path $vs 'Common7/Tools/Launch-VsDevShell.ps1') -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
if (!$CMakePath) {
    $available = Get-Command cmake.exe -ErrorAction SilentlyContinue
    $candidates = @($available.Source, (Join-Path $vs 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'), 'E:/cxgj/codex/devtools/ohos-sdk/26.0.0/native/build-tools/cmake/bin/cmake.exe')
    $CMakePath = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
}
if (!$CMakePath) { throw 'CMake 3.25 or newer is required. Pass -CMakePath or add cmake to PATH.' }
$env:PATH = (Split-Path $CMakePath) + ';' + $env:PATH
$testOption = if ($Tests) { 'ON' } else { 'OFF' }
[void](New-Item -ItemType Directory -Force -Path $build)
Push-Location $build
try {
& $CMakePath -S $source -B $build -G Ninja -DCMAKE_BUILD_TYPE=Release -DATRIUM_DEVELOPMENT=ON "-DATRIUM_BUILD_TESTS=$testOption"
if ($LASTEXITCODE) { throw 'Floating taskbar CMake configuration failed.' }
& $CMakePath --build $build --parallel 4
if ($LASTEXITCODE) { throw 'Floating taskbar build failed.' }
if ($Tests) {
    $oldDockData = $env:ZHEYE_DOCK_DATA
    $env:ZHEYE_DOCK_DATA = Join-Path $build 'test-data'
    [void](New-Item -ItemType Directory -Force -Path $env:ZHEYE_DOCK_DATA)
    try {
    & (Join-Path (Split-Path $CMakePath) 'ctest.exe') --test-dir $build --output-on-failure --timeout 30
    if ($LASTEXITCODE) { throw 'Floating taskbar native checks failed.' }
    } finally { $env:ZHEYE_DOCK_DATA = $oldDockData }
}
} finally { Pop-Location }
$stage = Join-Path $output 'package'
[void](New-Item -ItemType Directory -Path $stage -Force)
Copy-Item -LiteralPath (Join-Path $build 'ZheyeDock.exe') -Destination $stage -Force
Copy-Item -LiteralPath (Join-Path $nativeRoot 'FloatingTaskbar/LICENSE') -Destination $stage -Force
Copy-Item -LiteralPath (Join-Path $nativeRoot 'FloatingTaskbar/locales') -Destination $stage -Recurse -Force
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -Force -CompressionLevel Optimal
[IO.File]::WriteAllText($stamp, $fingerprint)
Write-Output "Floating taskbar package: $archive"
