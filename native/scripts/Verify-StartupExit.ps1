param([Parameter(Mandatory)][string]$Exe,[Parameter(Mandatory)][string]$Root)
$ErrorActionPreference='Stop'
$Exe=(Resolve-Path -LiteralPath $Exe).Path
$Root=[IO.Path]::GetFullPath($Root)
New-Item -ItemType Directory -Path $Root -Force | Out-Null
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type 'using System;using System.Runtime.InteropServices;public static class NookStartupProbe{[DllImport("user32.dll")]public static extern bool IsIconic(IntPtr h);}'

function Wait-Until([scriptblock]$Condition,[string]$Description,[int]$TimeoutMs=15000) {
    $watch=[Diagnostics.Stopwatch]::StartNew()
    do {
        if(& $Condition){return}
        [Threading.Thread]::Sleep(100)
    } while($watch.ElapsedMilliseconds -lt $TimeoutMs)
    throw ('Timed out waiting for '+$Description)
}
function Start-App([switch]$Autostart) {
    $arguments=@('--sandbox',('"'+$Root+'"'),'--keep-open-verification')
    if($Autostart){$arguments+='--autostart'}
    Start-Process -FilePath $Exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
}
function Wait-Window($Process) {
    Wait-Until {
        $Process.Refresh()
        if($Process.HasExited){throw ('Application exited before creating its window: '+$Process.ExitCode)}
        $Process.MainWindowHandle -ne [IntPtr]::Zero -and (Test-Path -LiteralPath (Join-Path $Root 'instance.pid'))
    } 'application window and startup completion'
    if(-not $Process.WaitForInputIdle(5000)){throw 'Application input queue did not become idle'}
}
function Find-UI($Process,[string]$Name,$Type) {
    $condition=[System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$Name),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,$Type))
    $watch=[Diagnostics.Stopwatch]::StartNew()
    do {
        $Process.Refresh()
        if($Process.HasExited){throw ('Application exited while waiting for '+$Name)}
        try {
            $ui=[System.Windows.Automation.AutomationElement]::FromHandle($Process.MainWindowHandle)
            $found=$ui.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
            if($found){return $found}
        } catch [System.Windows.Automation.ElementNotAvailableException] { }
        [Threading.Thread]::Sleep(100)
    } while($watch.ElapsedMilliseconds -lt 15000)
    throw ('UI did not become ready: '+$Name)
}
function Invoke-UI($Element) {
    ([System.Windows.Automation.InvokePattern]$Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
function Show-Panel($Process) {
    if([NookStartupProbe]::IsIconic($Process.MainWindowHandle)) {
        $activate=Start-App
        if(-not $activate.WaitForExit(5000)){throw 'Activation process did not exit'}
        if($activate.ExitCode -ne 0){throw 'Activation process failed'}
    }
    Wait-Until {-not [NookStartupProbe]::IsIconic($Process.MainWindowHandle)} 'panel activation'
    $null=Find-UI $Process '更多操作' ([System.Windows.Automation.ControlType]::Button)
}
function Set-CloseBehavior($Process,[bool]$EndsProcess) {
    Invoke-UI (Find-UI $Process '更多操作' ([System.Windows.Automation.ControlType]::Button))
    $option=Find-UI $Process '关闭时结束进程' ([System.Windows.Automation.ControlType]::MenuItem)
    $toggle=[System.Windows.Automation.TogglePattern]$option.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $desired=if($EndsProcess){[System.Windows.Automation.ToggleState]::On}else{[System.Windows.Automation.ToggleState]::Off}
    if($toggle.Current.ToggleState -ne $desired){$toggle.Toggle()}
    Wait-Until {$toggle.Current.ToggleState -eq $desired} 'close preference state'
}

$app=$null
$report=[ordered]@{Success=$false;Failure='';AutostartStartsMinimized=$false;DuplicateAutostartKeepsMinimized=$false;CloseToHideWorks=$false;CloseToExitTerminatesProcess=$false;ExitMenuTerminatesProcess=$false;CollectionStateUnchanged=$false}
try {
    $app=Start-App -Autostart
    Wait-Window $app
    Wait-Until {[NookStartupProbe]::IsIconic($app.MainWindowHandle)} 'minimized autostart'
    $report.AutostartStartsMinimized=$true
    $before=(Get-FileHash -LiteralPath (Join-Path $Root 'Data\state.json')).Hash
    $duplicate=Start-App -Autostart
    if(-not $duplicate.WaitForExit(5000) -or $duplicate.ExitCode -ne 0 -or -not [NookStartupProbe]::IsIconic($app.MainWindowHandle)){throw 'Duplicate autostart changed the panel or did not exit'}
    $report.DuplicateAutostartKeepsMinimized=$true

    Show-Panel $app
    Set-CloseBehavior $app $false
    Invoke-UI (Find-UI $app '关闭轻匣' ([System.Windows.Automation.ControlType]::Button))
    Wait-Until {$app.Refresh();if($app.HasExited){throw 'Close-to-hide terminated the process'};[NookStartupProbe]::IsIconic($app.MainWindowHandle)} 'close-to-hide'
    $report.CloseToHideWorks=$true

    Show-Panel $app
    Set-CloseBehavior $app $true
    Invoke-UI (Find-UI $app '关闭轻匣' ([System.Windows.Automation.ControlType]::Button))
    if(-not $app.WaitForExit(5000)){throw 'Close button did not terminate process'}
    if($app.ExitCode -ne 0){throw 'Close button returned an error'}
    $report.CloseToExitTerminatesProcess=$true

    $app=Start-App
    Wait-Window $app
    Show-Panel $app
    Invoke-UI (Find-UI $app '更多操作' ([System.Windows.Automation.ControlType]::Button))
    Invoke-UI (Find-UI $app '退出并结束进程' ([System.Windows.Automation.ControlType]::MenuItem))
    if(-not $app.WaitForExit(5000)){throw 'Exit menu did not terminate process'}
    if($app.ExitCode -ne 0){throw 'Exit menu returned an error'}
    $report.ExitMenuTerminatesProcess=$true
    if((Get-FileHash -LiteralPath (Join-Path $Root 'Data\state.json')).Hash -ne $before){throw 'Closing modified collection data'}
    $report.CollectionStateUnchanged=$true
    $report.Success=$true
} catch {
    $report.Failure=$_.Exception.Message
    throw
} finally {
    if($app -and -not $app.HasExited) {
        $stop=Start-Process -FilePath $Exe -ArgumentList @('--maintenance=stop','--sandbox',('"'+$Root+'"')) -WindowStyle Hidden -PassThru
        if(-not $stop.WaitForExit(12000) -or $stop.ExitCode -ne 0){Write-Warning ('Isolated process needs attention: '+$app.Id)}
    }
    $report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Root 'startup-exit-verification.json') -Encoding utf8
}
