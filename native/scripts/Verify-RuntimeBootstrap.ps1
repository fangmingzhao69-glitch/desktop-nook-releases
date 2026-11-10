param([Parameter(Mandatory=$true)][string]$NSIS)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type 'using System;using System.Runtime.InteropServices;public static class BootstrapTestInput{[DllImport("user32.dll")]public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);[DllImport("user32.dll")]public static extern IntPtr GetParent(IntPtr h);[DllImport("user32.dll")]public static extern int GetDlgCtrlID(IntPtr h);}'
function Find-TestButton($Element,[string]$Name){$Element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$Name))}
$root=Join-Path $env:TEMP ('Qingxia-BootstrapTests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root|Out-Null
$results=@()
. (Join-Path $PSScriptRoot 'RuntimeBootstrap.ps1') -FunctionsOnly
$manifest=Read-RuntimeManifest (Join-Path $PSScriptRoot 'DotNetRuntime.json')
foreach($kind in @('url','hash','version')){
    $bad=Get-Content (Join-Path $PSScriptRoot 'DotNetRuntime.json') -Raw|ConvertFrom-Json
    switch($kind){'url'{$bad.Url='https://example.com/runtime.exe'}'hash'{$bad.Sha512='invalid'}'version'{$bad.Version='11.0.0'}}
    $path=Join-Path $root ($kind+'.json');$bad|ConvertTo-Json|Set-Content $path -Encoding UTF8
    $rejected=$false;try{$null=Read-RuntimeManifest $path}catch{$rejected=$true};if(-not $rejected){throw ('Invalid manifest accepted: '+$kind)}
}
$corrupt=Join-Path $root 'corrupt.exe';'corrupt'|Set-Content $corrupt
$rejected=$false;try{Assert-RuntimePackage $corrupt $manifest}catch{$rejected=$true};if(-not $rejected){throw 'Corrupt package accepted'}
$manifest.Sha512=(Get-FileHash $corrupt -Algorithm SHA512).Hash
$rejected=$false;try{Assert-RuntimePackage $corrupt $manifest}catch{$rejected=$true};if(-not $rejected){throw 'Unsigned package accepted'}
if(-not(Test-RuntimeExit 0) -or -not(Test-RuntimeExit 3010) -or (Test-RuntimeExit 1602) -or (Test-RuntimeExit 24)){throw 'Incorrect component exit-code classification'}
foreach($case in @(
    @{Name='present';Code=99;Exit=0;Mark='installed';Missing=$false},
    @{Name='install';Code=0;Exit=0;Mark='installed';Missing=$true},
    @{Name='reboot';Code=3010;Exit=0;Mark='reboot';Missing=$true},
    @{Name='failure';Code=24;Exit=24;Mark='original';Missing=$true},
    @{Name='uac-cancel';Code=1602;Exit=1602;Mark='original';Missing=$true},
    @{Name='consent-cancel';Code=0;Exit=1602;Mark='original';Missing=$true;Cancel=$true},
    @{Name='silent-missing';Code=0;Exit=23;Mark='original';Missing=$true;Silent=$true}
)) {
    $dir=Join-Path $root $case.Name;New-Item -ItemType Directory -Path $dir|Out-Null
    $marker=Join-Path $dir 'marker.txt';[IO.File]::WriteAllText($marker,'original')
    $called=Join-Path $dir 'called.txt';$fake=Join-Path $dir 'fake.ps1';$exe=Join-Path $dir 'test.exe'
    [IO.File]::WriteAllText($fake,('param([string]$ManifestPath)'+"`r`n"+"[IO.File]::WriteAllText('"+$called+"','called')`r`nexit "+$case.Code),[Text.UTF8Encoding]::new($true))
    $defines=if($case.Missing){'!define VERIFY_MISSING_RUNTIME'+"`r`n"+'!define VERIFY_RUNTIME_AFTER_BOOTSTRAP'}else{''}
    $source=@'
Unicode true
Name "Qingxia runtime test"
OutFile "__EXE__"
RequestExecutionLevel user
AutoCloseWindow true
Page instfiles
!cd "__SCRIPTS__"
!define RUNTIME_BOOTSTRAP_SCRIPT "__FAKE__"
__DEFINES__
!include "RuntimeCheck.nsh"
Section
 FileOpen $0 "__MARKER__" w
 StrCmp $RuntimeExit 3010 reboot_mark
 FileWrite $0 "installed"
 Goto mark_done
 reboot_mark:
 FileWrite $0 "reboot"
 mark_done:
 FileClose $0
 SetErrorLevel 0
 Quit
SectionEnd
'@
    $source=$source.Replace('__EXE__',$exe).Replace('__SCRIPTS__',$PSScriptRoot).Replace('__FAKE__',$fake).Replace('__DEFINES__',$defines).Replace('__MARKER__',$marker)
    $nsi=Join-Path $dir 'test.nsi';[IO.File]::WriteAllText($nsi,$source,[Text.UTF8Encoding]::new($true))
    & $NSIS /V1 $nsi|Out-Null;if($LASTEXITCODE -ne 0){throw ('Compile failed: '+$case.Name)}
    $arguments=if($case.Silent){@('/S')}else{@()}
    $start=@{FilePath=$exe;PassThru=$true;WindowStyle='Hidden'};if($arguments.Count){$start.ArgumentList=$arguments}
    $p=Start-Process @start
    $deadline=[DateTime]::UtcNow.AddSeconds(35)
    while(-not $p.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        $windows=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$p.Id))
        for($i=0;$i -lt $windows.Count;$i++) {
            $buttonName=if($case.Cancel){'取消'}else{'确定'}
            $button=Find-TestButton $windows.Item($i) $buttonName
            if(-not $button){$button=Find-TestButton $windows.Item($i) $(if($case.Cancel){'Cancel'}else{'OK'})}
            if($button){try{if($button.Current.NativeWindowHandle){[BootstrapTestInput]::PostMessage([BootstrapTestInput]::GetParent([IntPtr]$button.Current.NativeWindowHandle),0x111,[IntPtr][BootstrapTestInput]::GetDlgCtrlID([IntPtr]$button.Current.NativeWindowHandle),[IntPtr]$button.Current.NativeWindowHandle)|Out-Null}else{$button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()}}catch [Windows.Automation.ElementNotAvailableException]{}}
        }
        Start-Sleep -Milliseconds 100
    }
    if(-not $p.HasExited){Stop-Process -Id $p.Id;throw ('Test timeout: '+$case.Name)}
    $actual=[IO.File]::ReadAllText($marker)
    if($p.ExitCode -ne $case.Exit -or $actual -ne $case.Mark){throw ('Unexpected result '+$case.Name+': exit='+$p.ExitCode+'; marker='+$actual)}
    if($case.Name -in @('present','silent-missing','consent-cancel') -and (Test-Path $called)){throw ('Unexpected component installation in '+$case.Name)}
    $results+=[pscustomobject]@{name=$case.Name;exit=$p.ExitCode;marker=$actual;passed=$true}
}
$report=Join-Path $PSScriptRoot '../test-results/runtime-bootstrap-verification.json'
New-Item -ItemType Directory -Path (Split-Path $report) -Force|Out-Null
[pscustomobject]@{passed=$results.Count;realRuntimeInstalled=$false;cases=$results}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $report -Encoding UTF8
Get-Content $report
