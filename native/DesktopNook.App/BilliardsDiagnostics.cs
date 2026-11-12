using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopNook.Games.Core;

namespace DesktopNook.App;
internal static class GameMemory
{
    [StructLayout(LayoutKind.Sequential)]private struct Counters{public uint Size,Faults;public UIntPtr PeakWorking,Working,PeakPaged,Paged,PeakNonPaged,NonPaged,Pagefile,PeakPagefile,Private,PrivateWorking;public ulong Shared;}
    [DllImport("psapi.dll")]private static extern bool GetProcessMemoryInfo(IntPtr process,ref Counters counters,uint size);
    internal static long PrivateWorkingSet(){using var process=Process.GetCurrentProcess();var c=new Counters{Size=(uint)Marshal.SizeOf<Counters>()};return GetProcessMemoryInfo(process.Handle,ref c,c.Size)?(long)c.PrivateWorking.ToUInt64():0;}
}
internal sealed partial class BilliardsWindow
{
    private static bool LegacyFooter(System.Windows.DependencyObject node)
    {
        if(node is System.Windows.Controls.Button button&&Equals(button.Content,"第三方许可"))return true;
        if(node is System.Windows.Controls.TextBlock text&&(text.Text.StartsWith("窗口尺寸已变化")||text.Text.StartsWith("显示缩放已变化")))return true;
        for(int n=0;n<VisualTreeHelper.GetChildrenCount(node);n++)if(LegacyFooter(VisualTreeHelper.GetChild(node,n)))return true;return false;
    }
    [StructLayout(LayoutKind.Sequential)]private struct Memory{public uint Size,Faults;public UIntPtr PeakWorking,Working,PeakPaged,Paged,PeakNonPaged,NonPaged,Pagefile,PeakPagefile,Private,PrivateWorking;public ulong Shared;}
    [DllImport("psapi.dll")]private static extern bool GetProcessMemoryInfo(IntPtr process,ref Memory memory,uint size);
    internal GameMessage Test(string action)
    {
        switch(action)
        {
            case "begin":_begin.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));break;
            case "rules":ShowRules();break;
            case "sound":_soundCheck.IsChecked=!_soundEnabled;break;
            case "audio-test":Scene.Audio.Enabled=true;Scene.Audio.Play(new(BallSoundKind.Cue,.2));break;
            case "audio-stop":Scene.Audio.Stop();break;
            case "shoot":if(RulesVisible)BeginGame();Scene.Pause(false);Scene.Round.Shoot(new(1200,450),.65);Scene.Wake();break;
            case "pause":Scene.Pause(true);break;
            case "resume":if(RulesVisible)BeginGame();Scene.Pause(false);break;
            case "finish":
                if(RulesVisible)BeginGame();
                Scene.Pause(false);if(Scene.Round.CanShoot)Scene.Round.Shoot(new(1200,450),.2);
                foreach(var ball in Scene.Round.Balls.Where(b=>b.Id!=0)){ball.Sunk=true;ball.ExitAge=.1;}
                Scene.Round.Advance(.05);Scene.Round.Advance(.05);Scene.Wake();break;
            case "duplicate-score":Scene.Completed?.Invoke();break;
            case "pocket":
                if(RulesVisible)BeginGame();Scene.Pause(false);if(Scene.Round.CanShoot)Scene.Round.Shoot(new(1200,450),.2);Scene.Round.Advance(.05);Scene.Round.Advance(.05);foreach(var b in Scene.Round.Balls)b.Velocity=new();Scene.Round.Balls[1].Position=Scene.Round.Pockets[0];Scene.Round.Advance(.02);Scene.Wake();break;
            case "duel-finish":
                if(RulesVisible)BeginGame();Scene.Pause(false);Scene.Round.Shoot(new(1200,450),.2);Scene.Round.Advance(.05);Scene.Round.Advance(.05);foreach(var b in Scene.Round.Balls)b.Velocity=new();Scene.Round.Balls[1].Position=Scene.Round.White.Position+new Vec(34,0);Scene.Round.White.Velocity=new(120,0);Scene.Round.Advance(.005);foreach(var b in Scene.Round.Balls)b.Velocity=new();Scene.Round.Balls[9].Position=Scene.Round.Pockets[0];Scene.Round.Advance(.02);Scene.Wake();break;
            case "restart":RestartRound();break;
            case "guide":SetGuide(!Scene.Guide);break;
            case "invalid-message":_=Notify(new(){Kind="InvalidProtocol"});break;
            case "metrics-reset":Scene.ResetMetrics();break;
            case "minimize":WindowState=WindowState.Minimized;break;
        }
        UpdateHud();using var process=Process.GetCurrentProcess();var memory=new Memory{Size=(uint)Marshal.SizeOf<Memory>()};GetProcessMemoryInfo(process.Handle,ref memory,memory.Size);
        var screen=Scene.PointToScreen(new Point(Scene.ActualWidth*.72,Scene.ActualHeight*.5));
        var result=new GameMessage{Rule=Scene.Round.RuleId,Left=Left,Top=Top,Width=Width,Height=Height,WindowHandle=new System.Windows.Interop.WindowInteropHelper(this).Handle.ToInt64(),Count=Scene.Round.Total,Round=Scene.Round.RoundNumber,Shots=Scene.Round.Shots,Milliseconds=Scene.Round.Elapsed*1000,Paused=Scene.Round.Paused,FrameSubscribed=Scene.HasFrameSubscription,PrivateWorkingSet=(long)memory.PrivateWorking.ToUInt64(),Commit=process.PrivateMemorySize64,CpuMs=process.TotalProcessorTime.TotalMilliseconds,ScreenX=screen.X,ScreenY=screen.Y};
        if(action=="preview")
        {
            UpdateLayout();var surface=(FrameworkElement)Content;var dpi=VisualTreeHelper.GetDpi(this);var image=new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(surface.ActualHeight*dpi.DpiScaleY),96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);image.Render(surface);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using var stream=new MemoryStream();png.Save(stream);result.Image=Convert.ToBase64String(stream.ToArray());
        }
        result.Players=Scene.Round.Match.Players;result.Winner=Scene.Round.Match.Winner;result.Score1=Scene.Round.Match.Scores[0];result.Score2=Scene.Round.Match.Scores[1];result.CurrentPlayer=Scene.Round.Match.CurrentPlayer;result.Sound=_soundEnabled;result.TrayCount=Scene.Round.PocketOrder.Count;result.RulesVisible=RulesVisible;result.AudioSubmitted=Scene.Audio.Submitted;result.AudioPending=Scene.Audio.Pending;result.AudioUnavailable=Scene.Audio.Unavailable;
        result.DisplayState=WindowState.ToString();var mouse=System.Windows.Input.Mouse.GetPosition(Scene);result.InputTrace=Scene.InputTrace+$"; mouse=({mouse.X:F1},{mouse.Y:F1}) hit={InputHitTest(System.Windows.Input.Mouse.GetPosition(this))?.GetType().Name} over={System.Windows.Input.Mouse.DirectlyOver?.GetType().Name} enabled={Scene.IsEnabled} active={IsActive} button={System.Windows.Input.Mouse.LeftButton}";result.HasFooter=LegacyFooter((System.Windows.DependencyObject)Content);result.FrameP95=Scene.FrameP95;result.FrameMean=Scene.FrameMean;result.FrameCount=Scene.FrameCount;return result;
    }
}
