param([Parameter(Mandatory)][string]$Exe,[string]$Output=(Join-Path $PSScriptRoot '..\..\发布\任务栏切换验证.json'))
$ErrorActionPreference='Stop'
$Exe=(Resolve-Path -LiteralPath $Exe).Path
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type 'using System;using System.Runtime.InteropServices;public static class NookToggleCheck{[StructLayout(LayoutKind.Sequential)]public struct Point{public int X,Y;}[DllImport("user32.dll",CharSet=CharSet.Unicode)]public static extern IntPtr FindWindow(string c,string n);[DllImport("user32.dll")]public static extern bool GetCursorPos(out Point p);[DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);[DllImport("user32.dll")]public static extern bool IsIconic(IntPtr h);[DllImport("user32.dll")]public static extern IntPtr SendMessage(IntPtr h,uint m,IntPtr w,IntPtr l);[DllImport("user32.dll")]public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr e);}'
$app=Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($Exe)) -ErrorAction SilentlyContinue | Where-Object Path -eq $Exe | Select-Object -First 1
if(-not $app){throw ('No running application at '+$Exe)}
$handle=$app.MainWindowHandle
if($handle -eq [IntPtr]::Zero){throw 'Application window is not ready'}
$tray=[System.Windows.Automation.AutomationElement]::FromHandle([NookToggleCheck]::FindWindow('Shell_TrayWnd',$null))
$button=$tray.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'Appid: DesktopNook.DesktopNook'))
if(-not $button){throw 'DesktopNook taskbar entry was not found'}
$rect=$button.Current.BoundingRectangle
if($rect.IsEmpty -or $button.Current.IsOffscreen){throw 'Taskbar entry is not visible'}
$wasMinimized=[NookToggleCheck]::IsIconic($handle)
$cursor=[NookToggleCheck+Point]::new();[NookToggleCheck]::GetCursorPos([ref]$cursor)|Out-Null
$states=[System.Collections.Generic.List[bool]]::new()
$passed=$false
try {
    [NookToggleCheck]::SendMessage($handle,0x112,[IntPtr]0xF020,[IntPtr]::Zero)|Out-Null
    for($i=0;$i -lt 4;$i++) {
        [NookToggleCheck]::SetCursorPos([int]($rect.Left+$rect.Width/2),[int]($rect.Top+$rect.Height/2))|Out-Null
        [NookToggleCheck]::mouse_event(2,0,0,0,[UIntPtr]::Zero);[NookToggleCheck]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
        $expected=$i % 2 -eq 0
        $watch=[Diagnostics.Stopwatch]::StartNew()
        do {[Threading.Thread]::Sleep(100);$visible=-not [NookToggleCheck]::IsIconic($handle)} while($visible -ne $expected -and $watch.ElapsedMilliseconds -lt 3000)
        $states.Add($visible)
        # Allow the shell activation and placement callbacks to settle before
        # sending another click, then confirm that the state remains stable.
        [Threading.Thread]::Sleep(300)
        if((-not [NookToggleCheck]::IsIconic($handle)) -ne $expected){throw 'Panel state did not remain stable after the taskbar click'}
    }
    $passed=$states.Count -eq 4 -and $states[0] -and -not $states[1] -and $states[2] -and -not $states[3]
    if(-not $passed){throw 'Taskbar click sequence did not match expected states'}
} finally {
    [pscustomobject]@{VisibleAfterClicks=$states.ToArray();Expected=@($true,$false,$true,$false);Passed=$passed;Executable=$Exe} | ConvertTo-Json | Set-Content -LiteralPath $Output -Encoding utf8
    $command=if($wasMinimized){0xF020}else{0xF120}
    [NookToggleCheck]::SendMessage($handle,0x112,[IntPtr]$command,[IntPtr]::Zero)|Out-Null
    [NookToggleCheck]::SetCursorPos($cursor.X,$cursor.Y)|Out-Null
}
