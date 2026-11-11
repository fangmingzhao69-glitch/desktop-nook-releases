param([string]$Executable=(Join-Path $PSScriptRoot '..\artifacts\win-x64\DesktopNook.exe'),[ValidateRange(1,100)][int]$Cycles=10,[string[]]$Variants=@('on-1','off-1','off-2','on-2'),[switch]$ProductionPlacement,[switch]$Software)
$ErrorActionPreference='Stop'
Add-Type @"
using System;using System.Runtime.InteropServices;
public static class AuditWindow {
[StructLayout(LayoutKind.Sequential)]public struct Memory {public uint Size,Faults;public UIntPtr PeakWorking,Working,PeakPaged,Paged,PeakNonPaged,NonPaged,Pagefile,PeakPagefile,Private,PrivateWorking;public ulong SharedCommit;}
[DllImport("psapi.dll",SetLastError=true)]public static extern bool GetProcessMemoryInfo(IntPtr process,ref Memory memory,uint size);
[DllImport("user32.dll")]public static extern bool ShowWindowAsync(IntPtr h,int state);
[DllImport("user32.dll")]public static extern bool IsIconic(IntPtr h);
[DllImport("user32.dll")]public static extern IntPtr SendMessage(IntPtr h,int message,IntPtr wParam,IntPtr lParam);
}
"@
$base=Join-Path $env:TEMP ('Qingxia-memory-audit-'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $base|Out-Null
[IO.File]::WriteAllText((Join-Path $env:TEMP 'Qingxia-current-memory-audit.txt'),$base)
$exe=(Resolve-Path -LiteralPath $Executable).Path;$results=@()
foreach($variant in $Variants){
$mode=$variant.Split('-')[0];$root=Join-Path $base $variant;$data=Join-Path $root 'Data';$desktop=Join-Path $root 'Desktop';New-Item -ItemType Directory -Path $data,$desktop|Out-Null
$categories=@(0..5|ForEach-Object{@{Id=('c'+$_);Name=('测试分类'+$_)}})+@(@{Id='uncategorized';Name='未分类'})
$items=@(1..58|ForEach-Object{$id=[guid]::NewGuid().ToString('N');$file=('入口'+$_+'.url');$vault=Join-Path $data ('vault\'+$id);New-Item -ItemType Directory -Path $vault|Out-Null;$path=Join-Path $vault $file;[IO.File]::WriteAllText($path,"[InternetShortcut]`r`nURL=https://example.invalid/`r`n");@{Id=$id;Name=('入口'+$_);Kind='shortcut';CategoryId=$categories[($_-1)%7].Id;OriginalPath=(Join-Path $desktop $file);FileName=$file;Hash=(Get-FileHash $path).Hash}})
[IO.File]::WriteAllText((Join-Path $data 'state.json'),(@{Version=1;Categories=$categories;Items=$items}|ConvertTo-Json -Depth 7))
[IO.File]::WriteAllText((Join-Path $data 'preferences.json'),(@{CloseEndsProcess=$false;MotionMode=$mode}|ConvertTo-Json))
$arguments=@('--sandbox',('"'+$root+'"'),'--autostart');if(-not $ProductionPlacement){$arguments+='--keep-open-verification'}
if($Software){$arguments+='--software-rendering'}
$p=Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
try{
$null=$p.WaitForInputIdle(15000);Start-Sleep -Seconds 3;$p.Refresh();$h=$p.MainWindowHandle
if($h -eq [IntPtr]::Zero){throw 'No sandbox window'}
$samples=@();function Sample($stage){$p.Refresh();$m=[AuditWindow+Memory]::new();$m.Size=[Runtime.InteropServices.Marshal]::SizeOf($m);if(-not [AuditWindow]::GetProcessMemoryInfo($p.Handle,[ref]$m,$m.Size)){throw 'Private working set unavailable on this Windows version'};[pscustomobject]@{stage=$stage;privateWorkingSet=$m.PrivateWorking.ToUInt64();workingSet=$p.WorkingSet64;private=$p.PrivateMemorySize64;handles=$p.HandleCount;cpuMs=$p.TotalProcessorTime.TotalMilliseconds}}
$samples+=Sample 'startup-minimized'
[AuditWindow]::ShowWindowAsync($h,9)|Out-Null;Start-Sleep -Seconds 3;$samples+=Sample 'first-open'
foreach($n in 1..$Cycles){[AuditWindow]::SendMessage($h,0x112,[IntPtr]0xf020,[IntPtr]::Zero)|Out-Null;Start-Sleep -Milliseconds 650;[AuditWindow]::ShowWindowAsync($h,9)|Out-Null;Start-Sleep -Milliseconds 650}
$samples+=Sample ('after-'+$Cycles+'-cycles-open')
[AuditWindow]::SendMessage($h,0x112,[IntPtr]0xf020,[IntPtr]::Zero)|Out-Null;Start-Sleep -Seconds 3;$samples+=Sample 'idle-minimized-3s';Start-Sleep -Seconds 3;$samples+=Sample 'idle-minimized-6s'
Start-Sleep -Seconds 9;$samples+=Sample 'idle-minimized-15s'
$row=[pscustomobject]@{variant=$variant;cycles=$Cycles;items=58;categories=7;samples=$samples};$results+=$row;$row|ConvertTo-Json -Depth 7
[IO.File]::WriteAllText((Join-Path $base 'results.json'),($results|ConvertTo-Json -Depth 7))
}finally{$stop=Start-Process -FilePath $exe -ArgumentList @('--sandbox',('"'+$root+'"'),'--maintenance=stop') -WindowStyle Hidden -PassThru;$stop.WaitForExit();if($stop.ExitCode -ne 0){throw 'Sandbox stop failed'}}
}
