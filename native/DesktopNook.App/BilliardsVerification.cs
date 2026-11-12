using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using DesktopNook.Core;
using DesktopNook.Games.Core;

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
    [DllImport("user32.dll")]private static extern bool ScreenToClient(IntPtr h,ref CursorPoint point);
    [DllImport("user32.dll")]private static extern IntPtr SendMessage(IntPtr h,uint message,IntPtr wParam,IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)]private struct NativeMouse{public int X,Y;public uint Data,Flags,Time;public UIntPtr Extra;}
    [StructLayout(LayoutKind.Explicit,Size=40)]private struct NativeInput{[FieldOffset(0)]public uint Type;[FieldOffset(8)]public NativeMouse Mouse;}
    [DllImport("user32.dll",SetLastError=true)]private static extern uint SendInput(uint count,NativeInput[] inputs,int size);
    [DllImport("user32.dll")]private static extern int GetSystemMetrics(int index);
    private static void MouseButton(int x,int y,bool down)
    {
        int left=GetSystemMetrics(76),top=GetSystemMetrics(77),width=GetSystemMetrics(78),height=GetSystemMetrics(79);
        var input=new NativeInput{Mouse=new(){X=(int)Math.Round((x-left)*65535d/(width-1)),Y=(int)Math.Round((y-top)*65535d/(height-1)),Flags=0xC001u|(down?2u:4u)}};
        if(SendInput(1,[input],Marshal.SizeOf<NativeInput>())!=1)throw new IOException("Native mouse injection failed.");
    }
    private static void Attach(MainWindow owner,BilliardsController controller)=>typeof(MainWindow).GetField("_games",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.SetValue(owner,controller);
    internal static async Task Performance(MainWindow owner,string root)
    {
        BilliardsController? controller=null;string failure="";var samples=new List<object>();
        try
        {
            if(!App.IsSandbox)throw new IOException("Only sandbox performance probes are allowed.");owner.KeepOpenForVerification=true;owner.WindowState=WindowState.Minimized;
            var icons=new List<GameIcon>();for(int n=0;n<21;n++){string path=Path.Combine(App.Store.DesktopRoots[0],n+".url");File.WriteAllText(path,"[InternetShortcut]\r\nURL=https://example.invalid/\r\n");icons.Add(new(n.ToString(),n.ToString(),path));}
            controller=new(App.Store.DataDirectory);await controller.Launch(icons,new(){Rule=DesktopNook.Games.Core.BilliardsRules.Snooker},new(0,0,1920,1040),true,true);await Task.Delay(1000);await controller.Probe("shoot");await Task.Delay(2000);
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
            await controller.Launch([new("parent","parent",path)],new(),new(40,40,1000,750),true,true);
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
    internal static async Task Run(MainWindow owner,string root,bool nativeMouse=true)
    {
        var checks=new List<string>();var samples=new List<object>();string failure="";BilliardsController? controller=null;GetCursorPos(out var cursor);
        void Check(bool condition,string text){if(!condition)throw new IOException(text);checks.Add(text);}
        Task Launch(List<GameIcon> icons,GameSettings settings,Rect work)=>controller!.Launch(icons,settings,work,true,!nativeMouse);
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
            history.SaveScore(new(DateTime.UtcNow,"current",15,3,45000){Rule=BilliardsRules.Eight});history.SaveScore(new(DateTime.UtcNow,"current",9,1,9000){Rule=BilliardsRules.Nine});for(int n=0;n<105;n++)history.SaveScore(new(DateTime.UtcNow,"current",21,2,60000+n){Rule=BilliardsRules.Snooker});
            Check(history.BestForRule(BilliardsRules.Eight)?.Milliseconds==45000&&history.BestForRule(BilliardsRules.Nine)?.Milliseconds==9000,"Rule-specific best scores survive 100 newer rounds without mixing different rules");
            var legacy=new GameStorage(Path.Combine(root,"legacy-settings"));Directory.CreateDirectory(legacy.Root);File.WriteAllText(Path.Combine(legacy.Root,"settings.json"),"{\"Source\":\"desktop\",\"Count\":45,\"Guide\":false}");var migrated=legacy.LoadSettings();
            Check(migrated.Count==15&&migrated.Rule==BilliardsRules.Eight&&migrated.Source=="desktop"&&!migrated.Guide,"Legacy free-count settings retain source and guide and default to fixed eight-ball");
            legacy.SaveSettings(new(){Rule=BilliardsRules.Nine});Check(legacy.LoadSettings().Rule==BilliardsRules.Nine&&legacy.LoadSettings().Count==9,"Nine-ball preset persists with its fixed count");
            Check(Math.Abs(BilliardsPlacement.Initial(new(-1280,120,1280,720)).Width*BilliardsPlacement.Initial(new(-1280,120,1280,720)).Height/(1280*720)-.4)<.0001&&Math.Abs(BilliardsPlacement.Initial(new(-1280,120,1280,720)).X+BilliardsPlacement.Initial(new(-1280,120,1280,720)).Width/2+640)<.01,"Forty-percent-area placement centres on offset monitors");
            var art=BilliardsBallDrawing.Create(new(){Radius=19,Style=BilliardsRules.Style(BilliardsRules.Eight,0)},ShellIntegration.AppIcon,1);var visual=new System.Windows.Media.DrawingVisual();using(var drawing=visual.RenderOpen()){drawing.PushTransform(new System.Windows.Media.MatrixTransform(2,0,0,2,48,48));drawing.DrawDrawing(art);drawing.Pop();}var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(96,96,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(visual);var pixels=new byte[96*96*4];bitmap.CopyPixels(pixels,96*4,0);
            Check(pixels[(12*96+12)*4+3]==0&&pixels[(48*96+48)*4+3]>0&&pixels[(16*96+48)*4+3]>0,"Ball drawing clips icon and surface to a circular silhouette");
            owner.WindowState=WindowState.Minimized;await Task.Delay(6000);long parentBefore=GameMemory.PrivateWorkingSet();controller=new(App.Store.DataDirectory);
            await Launch(collected,new(){Source="current",Rule=DesktopNook.Games.Core.BilliardsRules.Eight,Guide=true},new(0,0,1920,1080));
            Check(controller.Running,"Same executable starts a separate authenticated game process");int? pid=controller.ChildPid;
            await Launch(collected,new(){Rule=DesktopNook.Games.Core.BilliardsRules.Eight},new(0,0,1920,1080));
            Check(controller.ChildPid==pid,"Repeated start activates the existing game instead of duplicating it");
            await Task.Delay(700);var initial=await controller.Probe();Check(initial.Count==15&&!initial.FrameSubscribed,"Sparse sources clone only game bodies; a resting table has no rendering subscription");samples.Add(new{phase="initial",initial.PrivateWorkingSet,initial.Commit,initial.CpuMs,initial.Width,initial.Height,initial.Left,initial.Top});
            Check(initial.RulesVisible&&initial.Paused&&initial.Shots==0&&initial.Milliseconds==0,"Opening instructions block play and the clock until the start button");
            Check(Math.Abs(initial.Width*initial.Height/(1920*1080)-.4)<.001&&Math.Abs(initial.Left+initial.Width/2-960)<1.5&&Math.Abs(initial.Top+initial.Height/2-540)<1.5,"Game initially occupies forty percent of the screen area and is centred");Check(!initial.HasFooter,"Game has neither a bottom license button nor a window-size hint footer");
            var preview=await controller.Probe("preview");File.WriteAllBytes(Path.Combine(root,"billiards-preview.png"),Convert.FromBase64String(preview.Image));
            IntPtr gameHandle;
            if(nativeMouse)
            {
            using(var child=Process.GetProcessById(controller.ChildPid!.Value)){child.Refresh();gameHandle=(IntPtr)initial.WindowHandle;SetWindowPos(gameHandle,(IntPtr)(-1),0,0,0,0,0x43);SetForegroundWindow(gameHandle);}
            await Task.Delay(150);await controller.Probe("resume");SetCursorPos((int)initial.ScreenX,(int)initial.ScreenY);await Task.Delay(80);
            var hit=GetAncestor(WindowFromPoint(new(){X=(int)initial.ScreenX,Y=(int)initial.ScreenY}),2);GetWindowThreadProcessId(hit,out var hitPid);samples.Add(new{phase="native-hit",initial.ScreenX,initial.ScreenY,expected=gameHandle.ToInt64(),hit=hit.ToInt64(),hitPid,expectedPid=controller.ChildPid});
            Check(hit==gameHandle,"Native mouse hit belongs to the owned game window");
            await Task.Run(async()=>{MouseButton((int)initial.ScreenX,(int)initial.ScreenY,true);await Task.Delay(100);GetCursorPos(out var native);var held=await controller.Probe();samples.Add(new{phase="mouse-hold",held.InputTrace,nativeX=native.X,nativeY=native.Y});await Task.Delay(200);MouseButton((int)initial.ScreenX,(int)initial.ScreenY,false);});
            }
            else
            {
                await controller.Probe("shoot");
            }
            await Task.Delay(250);var shot=await controller.Probe();samples.Add(new{phase="shot",shot.Shots,shot.Paused,shot.FrameSubscribed,shot.InputTrace});Check(shot.Shots==1&&shot.FrameSubscribed,nativeMouse?"Real mouse hold/release starts a shot, simulation and active rendering":"Authenticated IPC shot starts simulation and rendering");
            if(Environment.GetCommandLineArgs().Contains("--billiards-mouse-self-test"))return;
            await controller.Probe("pause");var paused=await controller.Probe();await Task.Delay(400);var pausedAgain=await controller.Probe();Check(paused.Paused&&!paused.FrameSubscribed&&Math.Abs(paused.Milliseconds-pausedAgain.Milliseconds)<1,"Pause detaches frames and excludes paused time");
            await controller.Probe("resume");await Task.Delay(100);Check(!(await controller.Probe()).Paused,"Resume restores the round");
            await controller.Probe("guide");await Task.Delay(100);Check(!storage.LoadSettings().Guide,"Guide changes travel through IPC and persist only in game settings");
            await controller.Probe("sound");await Task.Delay(100);Check(!storage.LoadSettings().Sound&&!storage.LoadSettings().Guide,"Sound preference persists without overwriting the guide setting");
            await controller.Probe("finish");for(int n=0;n<30&&storage.Scores().Count==0;n++)await Task.Delay(100);
            Check(storage.Scores().Count==1,"Completed round is recorded by the parent process");await controller.Probe("duplicate-score");await Task.Delay(150);Check(storage.Scores().Count==1,"Repeated round completion cannot duplicate a score");
            await controller.Probe("restart");var reset=await controller.Probe();Check(reset.Shots==0&&reset.Milliseconds==0,"Restart clears round state");
            await controller.Probe("minimize");var hidden=await controller.Probe();Check(hidden.Paused&&!hidden.FrameSubscribed,"Minimizing the game pauses physics and rendering");
            controller.Stop();Check(!controller.Running,"Close ends the child and its IPC session");
            foreach(var rule in BilliardsRules.All)
            {
                await Launch(collected,new(){Rule=rule.Id},new(0,0,1920,1080));await Task.Delay(400);var state=await controller.Probe();Check(state.Rule==rule.Id&&state.Count==rule.Count&&Math.Abs(state.Width-1920*Math.Sqrt(.4))<1.5&&!state.HasFooter,rule.Name+" uses its fixed count in a compact game window");
                await controller.Probe("begin");var picture=await controller.Probe("preview");File.WriteAllBytes(Path.Combine(root,"billiards-"+rule.Id+".png"),Convert.FromBase64String(picture.Image));controller.Stop();
            }
            await Launch(collected,new(){Rule=BilliardsRules.Eight},new(0,0,1920,1080));await controller.Probe("begin");await controller.Probe("pocket");await Task.Delay(700);var tray=await controller.Probe();Check(tray.TrayCount==1,"Pocket tray collects a physically pocketed target and excludes the white");
            var trayPreview=await controller.Probe("preview");File.WriteAllBytes(Path.Combine(root,"billiards-tray.png"),Convert.FromBase64String(trayPreview.Image));
            await controller.Probe("rules");Check((await controller.Probe()).RulesVisible,"Rules can be reopened during a round");await controller.Probe("begin");
            var audio=await controller.Probe("audio-test");await Task.Delay(250);var audioDone=await controller.Probe();samples.Add(new{phase="audio-device",audio.AudioSubmitted,audio.AudioUnavailable,audioDone.AudioPending});Check(audioDone.AudioPending==0,"Short audio buffers are released after playback or unavailable-device fallback");controller.Stop();
            int recordsBeforeDuel=storage.Scores().Count;await Launch(collected,new(){Rule=BilliardsRules.Nine,Players=2,Sound=false},new(0,0,1920,1080));var duel=await controller.Probe();Check(duel.Players==2&&duel.RulesVisible&&duel.CurrentPlayer==0,"Dual mode opens its own rules with player one to break");
            var duelIntro=await controller.Probe("preview");File.WriteAllBytes(Path.Combine(root,"billiards-duel-rules.png"),Convert.FromBase64String(duelIntro.Image));await controller.Probe("duel-finish");for(int n=0;n<30&&storage.Scores().Count==recordsBeforeDuel;n++)await Task.Delay(100);
            var duelResult=await controller.Probe();Check(duelResult.Winner==0&&storage.Scores().Last().Players==2&&storage.Scores().Last().Winner==0,"Dual victory is displayed and stored as a separate two-player result");Check(storage.BestForRule(BilliardsRules.Nine) is null,"Two-player results do not replace solo best records");
            await Task.Delay(700);var resultPreview=await controller.Probe("preview");File.WriteAllBytes(Path.Combine(root,"billiards-duel-result.png"),Convert.FromBase64String(resultPreview.Image));await controller.Probe("restart");var newDuel=await controller.Probe();Check(newDuel.RulesVisible&&newDuel.Score1==0&&newDuel.Score2==0&&newDuel.TrayCount==0,"Restart resets the match, tray and opening rules");controller.Stop();
            for(int n=0;n<30;n++){await Launch(collected,new(){Rule=DesktopNook.Games.Core.BilliardsRules.Eight},new(40,40,1000,750));controller.Stop();}Check(!controller.Running,"30 repeated starts and stops leave no game process");
            await Launch(collected,new(),new(40,40,1000,750));Attach(owner,controller);Check(owner.TryBeginStorageMove()&&!controller.Running,"Storage maintenance closes the game before acquiring data access");owner.EndStorageMove();
            Check(await VerifyParentExit(Path.Combine(root,"maintenance-parent"),true),"Normal maintenance exit closes both launcher and game within three seconds");
            Check(await VerifyParentExit(Path.Combine(root,"abnormal-parent"),false),"Abnormal parent exit closes the orphan game within three seconds");
            await Launch(collected,new(),new(40,40,1000,750));try{await controller.Probe("invalid-message");}catch(Exception ex)when(ex is TimeoutException or IOException or OperationCanceledException){}for(int n=0;n<30&&controller.Running;n++)await Task.Delay(100);Check(!controller.Running,"Invalid protocol disconnects and closes the game instead of leaving a stuck session");controller.Stop();
            var many=new List<GameIcon>();for(int n=0;n<21;n++){var path=Path.Combine(App.Store.DesktopRoots[0],"image-"+n+".url");File.WriteAllText(path,"[InternetShortcut]\r\nURL=https://example.invalid/\r\n");many.Add(new("image-"+n,n.ToString(),path));}
            await Launch(many,new(){Rule=DesktopNook.Games.Core.BilliardsRules.Snooker},new(0,0,1920,1040));await Task.Delay(1000);await controller.Probe("shoot");await Task.Delay(2000);var maximum=await controller.Probe();samples.Add(new{phase="21-icons-quarter-screen",maximum.PrivateWorkingSet,maximum.Commit,maximum.CpuMs,maximum.FrameP95});
            using(var child=Process.GetProcessById(controller.ChildPid!.Value)){child.Kill();await child.WaitForExitAsync();}await Task.Delay(200);Check(!controller.Running,"Abnormal game exit does not stop the launcher");controller.Stop();
            await Task.Delay(15000);samples.Add(new{phase="parent-after-exit-15s",beforePrivateWorkingSet=parentBefore,afterPrivateWorkingSet=GameMemory.PrivateWorkingSet(),forcedGc=false});
            await Task.Delay(45000);samples.Add(new{phase="parent-after-exit-60s",beforePrivateWorkingSet=parentBefore,afterPrivateWorkingSet=GameMemory.PrivateWorkingSet(),forcedGc=false});
            var after=VerifiedContent.Snapshot(App.Store.DataDirectory);var nonGame=after.Where(p=>p.Key!="Games"&&!p.Key.StartsWith("Games/",StringComparison.Ordinal)).ToDictionary(p=>p.Key,p=>p.Value);
            Check(VerifiedContent.Equal(before,nonGame)&&App.Store.State.Items.All(i=>NookStore.HashFile(App.Store.ManagedPath(i))==payloads[i.Id]),"Game, restart and abnormal exit leave collection metadata, order and payloads unchanged");
        }
        catch(Exception ex){failure=ex.ToString();}
        finally{try{controller?.Dispose();}catch(Exception ex){failure+="\nCleanup: "+ex;}if(nativeMouse)SetCursorPos(cursor.X,cursor.Y);File.WriteAllText(Path.Combine(root,"billiards-verification.json"),JsonSerializer.Serialize(new{inputMode=nativeMouse?"native-pointer":"ipc-simulation",passed=checks.Count,failure,checks,samples},new JsonSerializerOptions{WriteIndented=true}));Application.Current.Shutdown(failure.Length==0?0:1);}
    }
}
