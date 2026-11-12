using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using DesktopNook.Core;

namespace DesktopNook.App;
internal static class BilliardsVerification
{
    [StructLayout(LayoutKind.Sequential)]private struct CursorPoint{public int X,Y;}
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out CursorPoint p);
    [DllImport("user32.dll")]private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")]private static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")]private static extern IntPtr WindowFromPoint(CursorPoint point);
    [DllImport("user32.dll")]private static extern IntPtr GetAncestor(IntPtr h,uint flags);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    [DllImport("user32.dll")]private static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
    private static void Attach(MainWindow owner,BilliardsController controller)=>typeof(MainWindow).GetField("_games",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.SetValue(owner,controller);
    internal static async Task Performance(MainWindow owner,string root)
    {
        BilliardsController? controller=null;string failure="";var samples=new List<object>();
        try
        {
            if(!App.IsSandbox)throw new IOException("Only sandbox performance probes are allowed.");owner.KeepOpenForVerification=true;owner.WindowState=WindowState.Minimized;
            var icons=new List<GameIcon>();for(int n=0;n<45;n++){string path=Path.Combine(App.Store.DesktopRoots[0],n+".url");File.WriteAllText(path,"[InternetShortcut]\r\nURL=https://example.invalid/\r\n");icons.Add(new(n.ToString(),n.ToString(),path));}
            controller=new(App.Store.DataDirectory);await controller.Launch(icons,new(){Count=45},new(0,0,1920,1040),true);await Task.Delay(1000);await controller.Probe("shoot");await Task.Delay(2000);
            var cold=await controller.Probe();samples.Add(new{phase="first-shot",cold.PrivateWorkingSet,cold.Commit,cold.FrameP95,cold.FrameMean,cold.FrameCount});await controller.Probe("metrics-reset");
            for(int n=0;n<3;n++){await controller.Probe("restart");await controller.Probe("shoot");await Task.Delay(4000);}
            var active=await controller.Probe();long parentMemory=GameMemory.PrivateWorkingSet();samples.Add(new{phase="three-rounds-warm",active.PrivateWorkingSet,active.Commit,active.FrameP95,active.FrameMean,active.FrameCount,approximateFps=active.FrameMean>0?1000/active.FrameMean:0,parentPrivateWorkingSet=parentMemory,totalPrivateWorkingSet=parentMemory+active.PrivateWorkingSet});
            await controller.Probe("minimize");var before=await controller.Probe();await Task.Delay(2000);var after=await controller.Probe();samples.Add(new{phase="minimized-2s",after.PrivateWorkingSet,after.Commit,cpuMs=after.CpuMs-before.CpuMs,after.FrameSubscribed,after.Paused});
            if(active.FrameCount<100||active.PrivateWorkingSet>120L*1024*1024||!after.Paused||after.FrameSubscribed)throw new IOException("Performance/resource checks failed.");
        }
        catch(Exception ex){failure=ex.ToString();}
        finally{try{controller?.Dispose();}catch(Exception ex){failure+="\n"+ex;}File.WriteAllText(Path.Combine(root,"billiards-performance.json"),JsonSerializer.Serialize(new{failure,samples,forcedGc=false},new JsonSerializerOptions{WriteIndented=true}));Application.Current.Shutdown(failure.Length==0?0:1);}
    }
    internal static async Task ParentProbe(MainWindow owner,string root)
    {
        if(!App.IsSandbox)throw new IOException("Only sandbox parents may be probed.");
        var path=Path.Combine(App.Store.DesktopRoots[0],"parent.url");File.WriteAllText(path,"[InternetShortcut]\r\nURL=https://example.invalid/\r\n");
        var controller=new BilliardsController(App.Store.DataDirectory);Attach(owner,controller);
        await controller.Launch([new("parent","parent",path)],new(),new(40,40,1000,750));
        File.WriteAllText(Path.Combine(root,"game-ready.txt"),controller.ChildPid!.Value.ToString());
    }
    private static async Task<bool> VerifyParentExit(string root,bool maintenance)
    {
        Directory.CreateDirectory(root);var info=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false};
        foreach(var arg in new[]{"--sandbox",root,"--autostart","--billiards-parent-probe"})info.ArgumentList.Add(arg);
        using var parent=Process.Start(info)!;Process? child=null;
        try
        {
            string ready=Path.Combine(root,"game-ready.txt");for(int n=0;n<100&&!File.Exists(ready)&&!parent.HasExited;n++)await Task.Delay(100);
            child=Process.GetProcessById(int.Parse(File.ReadAllText(ready)));
            if(maintenance){using var signal=EventWaitHandle.OpenExisting("Local\\DesktopNook-Stop-"+DataPaths.Signature(root));signal.Set();}
            else parent.Kill();
            using var timeout=new CancellationTokenSource(3000);await Task.WhenAll(parent.WaitForExitAsync(timeout.Token),child.WaitForExitAsync(timeout.Token));return true;
        }
        finally{if(!parent.HasExited)parent.Kill();if(child is not null){if(!child.HasExited)child.Kill();child.Dispose();}}
    }
    internal static async Task Run(MainWindow owner,string root)
    {
        var checks=new List<string>();var samples=new List<object>();string failure="";BilliardsController? controller=null;GetCursorPos(out var cursor);
        void Check(bool condition,string text){if(!condition)throw new IOException(text);checks.Add(text);}
        try
        {
            if(!App.IsSandbox)throw new IOException("台球检查仅允许沙箱。");owner.KeepOpenForVerification=true;
            for(int n=0;n<3;n++){var path=Path.Combine(App.Store.DesktopRoots[0],n+".url");File.WriteAllText(path,"[InternetShortcut]\r\nURL=https://example.invalid/\r\n");if(n<2)App.Store.Collect(path,"office");}
            owner.Render();var collected=GameIconCatalog.Collected(App.Store,"office");var desktop=await GameIconCatalog.Desktop(App.Store,true);
            Check(collected.Count==2&&desktop.Count==1,"Collected and desktop catalogues remain independent read-only sources");
            var before=VerifiedContent.Snapshot(App.Store.DataDirectory);var payloads=App.Store.State.Items.ToDictionary(i=>i.Id,i=>NookStore.HashFile(App.Store.ManagedPath(i)));
            var storage=new GameStorage(App.Store.DataDirectory);var setup=new BilliardsSetupWindow(App.Store,"office",true,storage){Owner=owner};
            setup.Loaded+=async(_,_)=>{await Task.Delay(300);setup.DialogResult=false;};setup.ShowDialog();
            Check(setup.Selection.Count==2,"Setup defaults to the current category and can cancel without changing data");
            bool emptyDisabled=false;var empty=new BilliardsSetupWindow(App.Store,"nonexistent",true,storage){Owner=owner};empty.Loaded+=async(_,_)=>{await Task.Delay(150);emptyDisabled=empty.Selection.Count==0&&!(bool)((System.Windows.Controls.Button)typeof(BilliardsSetupWindow).GetField("_start",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(empty)!).IsEnabled;empty.DialogResult=false;};empty.ShowDialog();Check(emptyDisabled,"Empty icon sources disable the start button");
            var history=new GameStorage(Path.Combine(root,"score-storage"));history.SaveScore(new(DateTime.UtcNow,"current",15,1,15000));for(int n=0;n<105;n++)history.SaveScore(new(DateTime.UtcNow,"current",15,2,30000+n));
            Check(history.Scores().Count==100&&history.Best?.Milliseconds==15000,"History retains 100 recent rounds and preserves an older all-time best");
            owner.WindowState=WindowState.Minimized;await Task.Delay(6000);long parentBefore=GameMemory.PrivateWorkingSet();controller=new(App.Store.DataDirectory);
            await controller.Launch(collected,new(){Source="current",Count=15,Guide=true},new(40,40,1200,850),true);
            Check(controller.Running,"Same executable starts a separate authenticated game process");int? pid=controller.ChildPid;
            await controller.Launch(collected,new(){Count=15},new(40,40,1200,850),true);
            Check(controller.ChildPid==pid,"Repeated start activates the existing game instead of duplicating it");
            await Task.Delay(700);var initial=await controller.Probe();Check(initial.Count==15&&!initial.FrameSubscribed,"Sparse sources clone only game bodies; a resting table has no rendering subscription");samples.Add(new{phase="initial",initial.PrivateWorkingSet,initial.Commit,initial.CpuMs});
            var preview=await controller.Probe("preview");File.WriteAllBytes(Path.Combine(root,"billiards-preview.png"),Convert.FromBase64String(preview.Image));
            IntPtr gameHandle;
            using(var child=Process.GetProcessById(controller.ChildPid!.Value)){child.Refresh();gameHandle=(IntPtr)initial.WindowHandle;SetWindowPos(gameHandle,(IntPtr)(-1),0,0,0,0,0x43);SetForegroundWindow(gameHandle);}
            await Task.Delay(150);await controller.Probe("resume");SetCursorPos((int)initial.ScreenX,(int)initial.ScreenY);await Task.Delay(80);
            var hit=GetAncestor(WindowFromPoint(new(){X=(int)initial.ScreenX,Y=(int)initial.ScreenY}),2);GetWindowThreadProcessId(hit,out var hitPid);samples.Add(new{phase="native-hit",initial.ScreenX,initial.ScreenY,expected=gameHandle.ToInt64(),hit=hit.ToInt64(),hitPid,expectedPid=controller.ChildPid});
            Check(hit==gameHandle,"Native mouse hit belongs to the owned game window");
            mouse_event(2,0,0,0,UIntPtr.Zero);await Task.Delay(300);mouse_event(4,0,0,0,UIntPtr.Zero);
            await Task.Delay(250);var shot=await controller.Probe();samples.Add(new{phase="mouse-shot",shot.Shots,shot.Paused,shot.FrameSubscribed});Check(shot.Shots==1&&shot.FrameSubscribed,"Real mouse hold/release starts a shot, simulation and active rendering");
            await controller.Probe("pause");var paused=await controller.Probe();await Task.Delay(400);var pausedAgain=await controller.Probe();Check(paused.Paused&&!paused.FrameSubscribed&&Math.Abs(paused.Milliseconds-pausedAgain.Milliseconds)<1,"Pause detaches frames and excludes paused time");
            await controller.Probe("resume");await Task.Delay(100);Check(!(await controller.Probe()).Paused,"Resume restores the round");
            await controller.Probe("guide");await Task.Delay(100);Check(!storage.LoadSettings().Guide,"Guide changes travel through IPC and persist only in game settings");
            await controller.Probe("finish");for(int n=0;n<30&&storage.Scores().Count==0;n++)await Task.Delay(100);
            Check(storage.Scores().Count==1,"Completed round is recorded by the parent process");await controller.Probe("duplicate-score");await Task.Delay(150);Check(storage.Scores().Count==1,"Repeated round completion cannot duplicate a score");
            await controller.Probe("restart");var reset=await controller.Probe();Check(reset.Shots==0&&reset.Milliseconds==0,"Restart clears round state");
            await controller.Probe("minimize");var hidden=await controller.Probe();Check(hidden.Paused&&!hidden.FrameSubscribed,"Minimizing the game pauses physics and rendering");
            controller.Stop();Check(!controller.Running,"Close ends the child and its IPC session");
            for(int n=0;n<30;n++){await controller.Launch(collected,new(){Count=15},new(40,40,1000,750),true);controller.Stop();}Check(!controller.Running,"30 repeated starts and stops leave no game process");
            await controller.Launch(collected,new(),new(40,40,1000,750),true);Attach(owner,controller);Check(owner.TryBeginStorageMove()&&!controller.Running,"Storage maintenance closes the game before acquiring data access");owner.EndStorageMove();
            Check(await VerifyParentExit(Path.Combine(root,"maintenance-parent"),true),"Normal maintenance exit closes both launcher and game within three seconds");
            Check(await VerifyParentExit(Path.Combine(root,"abnormal-parent"),false),"Abnormal parent exit closes the orphan game within three seconds");
            await controller.Launch(collected,new(),new(40,40,1000,750),true);try{await controller.Probe("invalid-message");}catch(Exception ex)when(ex is TimeoutException or IOException or OperationCanceledException){}for(int n=0;n<30&&controller.Running;n++)await Task.Delay(100);Check(!controller.Running,"Invalid protocol disconnects and closes the game instead of leaving a stuck session");controller.Stop();
            var many=new List<GameIcon>();for(int n=0;n<45;n++){var path=Path.Combine(App.Store.DesktopRoots[0],"image-"+n+".url");File.WriteAllText(path,"[InternetShortcut]\r\nURL=https://example.invalid/\r\n");many.Add(new("image-"+n,n.ToString(),path));}
            await controller.Launch(many,new(){Count=45},new(0,0,1920,1040),true);await Task.Delay(1000);await controller.Probe("shoot");await Task.Delay(2000);var maximum=await controller.Probe();samples.Add(new{phase="45-icons-1920x1040",maximum.PrivateWorkingSet,maximum.Commit,maximum.CpuMs,maximum.FrameP95});
            using(var child=Process.GetProcessById(controller.ChildPid!.Value)){child.Kill();await child.WaitForExitAsync();}await Task.Delay(200);Check(!controller.Running,"Abnormal game exit does not stop the launcher");controller.Stop();
            await Task.Delay(15000);samples.Add(new{phase="parent-after-exit-15s",beforePrivateWorkingSet=parentBefore,afterPrivateWorkingSet=GameMemory.PrivateWorkingSet(),forcedGc=false});
            await Task.Delay(45000);samples.Add(new{phase="parent-after-exit-60s",beforePrivateWorkingSet=parentBefore,afterPrivateWorkingSet=GameMemory.PrivateWorkingSet(),forcedGc=false});
            var after=VerifiedContent.Snapshot(App.Store.DataDirectory);var nonGame=after.Where(p=>p.Key!="Games"&&!p.Key.StartsWith("Games/",StringComparison.Ordinal)).ToDictionary(p=>p.Key,p=>p.Value);
            Check(VerifiedContent.Equal(before,nonGame)&&App.Store.State.Items.All(i=>NookStore.HashFile(App.Store.ManagedPath(i))==payloads[i.Id]),"Game, restart and abnormal exit leave collection metadata, order and payloads unchanged");
        }
        catch(Exception ex){failure=ex.ToString();}
        finally{try{controller?.Dispose();}catch(Exception ex){failure+="\nCleanup: "+ex;}SetCursorPos(cursor.X,cursor.Y);File.WriteAllText(Path.Combine(root,"billiards-verification.json"),JsonSerializer.Serialize(new{passed=checks.Count,failure,checks,samples},new JsonSerializerOptions{WriteIndented=true}));Application.Current.Shutdown(failure.Length==0?0:1);}
    }
}
