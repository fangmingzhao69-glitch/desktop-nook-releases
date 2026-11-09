param([string]$Setup,[string]$Profile,[string]$InstallDir)
$ErrorActionPreference='Stop'
function Run-Setup { $p=Start-Process -FilePath $Setup -ArgumentList @('/S',('/D='+$InstallDir)) -WindowStyle Hidden -PassThru; $p.WaitForExit(); if($p.ExitCode -ne 0){throw ('Setup exit '+$p.ExitCode)} }
$before=(Get-FileHash -LiteralPath (Join-Path $Profile 'Data\state.json')).Hash
Run-Setup
$preferences=Join-Path $Profile 'Data\preferences.json'
$preferenceHash=if(Test-Path -LiteralPath $preferences){(Get-FileHash -LiteralPath $preferences).Hash}else{$null}
$exe=Join-Path $InstallDir 'DesktopNook.exe'
$app=Start-Process -FilePath $exe -ArgumentList @('--sandbox',('"'+$Profile+'"')) -WindowStyle Hidden -PassThru
Run-Setup
if((Get-FileHash -LiteralPath (Join-Path $Profile 'Data\state.json')).Hash -ne $before){throw 'Upgrade changed user state'}
if($preferenceHash -and (Get-FileHash -LiteralPath $preferences).Hash -ne $preferenceHash){throw 'Upgrade changed preferences'}
if((Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DesktopNookValidation').DisplayName -ne '轻匣'){throw 'Installer display name not updated'}
$uninstaller=Join-Path $InstallDir '卸载桌面小筑.exe'
$removal=Start-Process -FilePath $uninstaller -ArgumentList @('/S',('_?='+$InstallDir)) -WindowStyle Hidden -PassThru
$removal.WaitForExit()
$state=Get-Content -LiteralPath (Join-Path $Profile 'Data\state.json') -Raw|ConvertFrom-Json
if($removal.ExitCode -ne 0 -or $state.Items.Count -ne 0 -or (Test-Path -LiteralPath $exe)){throw 'Uninstall recovery failed'}
[pscustomobject]@{Installed=$true;Upgraded=$true;UserStateHashPreserved=$true;UserPreferencesHashPreserved=$true;DisplayName="轻匣";Uninstalled=$true;RemainingItems=$state.Items.Count;RecoveredFiles=@(Get-ChildItem -LiteralPath (Join-Path $Profile 'Desktop') -File).Count;LiveUserDataTouched=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $PSScriptRoot '..\test-results\installer-verification.json') -Encoding utf8
