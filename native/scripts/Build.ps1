param([switch]$SkipTests,[switch]$Verification)
$ErrorActionPreference = 'Stop'
$nookRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $nookRoot
try {
    powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File (Join-Path $PSScriptRoot 'Make-Icon.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }
    if (-not $SkipTests) {
        dotnet run --project DesktopNook.Tests -c Release -- test-results\core.json
        if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
        dotnet run --project DesktopNook.Games.Tests -c Release -- test-results\game-core.json
        if ($LASTEXITCODE -ne 0) { throw 'Game core checks failed.' }
        dotnet run --project DesktopNook.Network.Tests -c Release -- test-results\network-core.json
        if ($LASTEXITCODE -ne 0) { throw 'Network core checks failed.' }
    }
    $output=if($Verification){'artifacts\win-x64-qa'}else{'artifacts\win-x64'}
    $includeChecks=$Verification.IsPresent.ToString().ToLowerInvariant()
    dotnet publish DesktopNook.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false "-p:IncludeUiVerification=$includeChecks" -o $output
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Write-Output (Join-Path $nookRoot (Join-Path $output 'DesktopNook.exe'))
} finally { Pop-Location }
