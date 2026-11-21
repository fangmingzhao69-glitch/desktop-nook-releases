param([string]$Setup,[string]$Profile,[string]$InstallDir)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
function Get-IconSignature([string]$File) {
    $icon=[Drawing.Icon]::ExtractAssociatedIcon((Resolve-Path -LiteralPath $File).Path)
    try {
        $bitmap=$icon.ToBitmap()
        try {
            $buffer=[IO.MemoryStream]::new()
            try {
                $bitmap.Save($buffer,[Drawing.Imaging.ImageFormat]::Png)
                $sha=[Security.Cryptography.SHA256]::Create()
                try { return [BitConverter]::ToString($sha.ComputeHash($buffer.ToArray())) }
                finally { $sha.Dispose() }
            } finally { $buffer.Dispose() }
        } finally { $bitmap.Dispose() }
    } finally { $icon.Dispose() }
}
function Run-Setup { $p=Start-Process -FilePath $Setup -ArgumentList @('/S',('/D='+$InstallDir)) -WindowStyle Hidden -PassThru; $p.WaitForExit(); if($p.ExitCode -ne 0){throw ('Setup exit '+$p.ExitCode)} }
$before=(Get-FileHash -LiteralPath (Join-Path $Profile 'Data\state.json')).Hash
Run-Setup
$preferences=Join-Path $Profile 'Data\preferences.json'
$preferenceHash=if(Test-Path -LiteralPath $preferences){(Get-FileHash -LiteralPath $preferences).Hash}else{$null}
$exe=Join-Path $InstallDir 'DesktopNook.exe'
$app=Start-Process -FilePath $exe -ArgumentList @('--sandbox',('"'+$Profile+'"')) -WindowStyle Hidden -PassThru
if(-not $app.WaitForInputIdle(15000)){throw 'Sandbox application did not become ready before upgrade'}
Run-Setup
if((Get-FileHash -LiteralPath (Join-Path $Profile 'Data\state.json')).Hash -ne $before){throw 'Upgrade changed user state'}
if($preferenceHash -and (Get-FileHash -LiteralPath $preferences).Hash -ne $preferenceHash){throw 'Upgrade changed preferences'}
if((Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DesktopNookValidation').DisplayName -ne '折页'){throw 'Installer display name not updated'}
$uninstaller=Join-Path $InstallDir '卸载桌面小筑.exe'
$programIcon=Get-IconSignature $exe
if((Get-IconSignature $Setup) -ne $programIcon -or (Get-IconSignature $uninstaller) -ne $programIcon){
    throw 'The installer or uninstaller has a different Windows icon from the program'
}
$removal=Start-Process -FilePath $uninstaller -ArgumentList @('/S',('_?='+$InstallDir)) -WindowStyle Hidden -PassThru
$removal.WaitForExit()
$state=Get-Content -LiteralPath (Join-Path $Profile 'Data\state.json') -Raw|ConvertFrom-Json
if($removal.ExitCode -ne 0 -or $state.Items.Count -ne 0 -or (Test-Path -LiteralPath $exe)){throw 'Uninstall recovery failed'}
[pscustomobject]@{Installed=$true;Upgraded=$true;UserStateHashPreserved=$true;UserPreferencesHashPreserved=$true;DisplayName="折页";InstallerAndUninstallerIconsMatch=$true;Uninstalled=$true;RemainingItems=$state.Items.Count;RecoveredFiles=@(Get-ChildItem -LiteralPath (Join-Path $Profile 'Desktop') -File).Count;LiveUserDataTouched=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $PSScriptRoot '..\test-results\installer-verification.json') -Encoding utf8
