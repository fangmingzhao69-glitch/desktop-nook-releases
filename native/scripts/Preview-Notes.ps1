param([string]$Sandbox = (Join-Path $env:TEMP 'Qingxia-Notes-Preview'))
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$previewExe = Join-Path $projectRoot 'native\DesktopNook.App\bin\Release\net10.0-windows\DesktopNook.exe'
& dotnet build (Join-Path $projectRoot 'native\DesktopNook.App') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw '随手编译失败，未启动试用。' }
$previewSandbox = [IO.Path]::GetFullPath($Sandbox)
Write-Output ('隔离试用目录：' + $previewSandbox)
Start-Process -FilePath $previewExe -ArgumentList @('--sandbox', ('"' + $previewSandbox + '"')) -WindowStyle Hidden
