param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$nookRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $nookRoot
try {
    powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File (Join-Path $PSScriptRoot 'Make-Icon.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }
    if (-not $SkipTests) {
        dotnet run --project DesktopNook.Tests -c Release -- test-results\core.json
        if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
    }
    dotnet publish DesktopNook.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o artifacts\win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Write-Output (Join-Path $nookRoot 'artifacts\win-x64\DesktopNook.exe')
} finally { Pop-Location }
