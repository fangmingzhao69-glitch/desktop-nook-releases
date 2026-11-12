using System.IO;
using System.Text.Json;
using System.Windows;
using DesktopNook.Core;
using DesktopNook.Games.Core;

namespace DesktopNook.App;
internal static class FriendVerification
{
    internal static async Task Run(MainWindow owner,string root)
    {
        var checks=new List<string>();var samples=new List<object>();string failure="";BilliardsController? host=null,guest=null;
        void Check(bool value,string text){if(!value)throw new IOException(text);checks.Add(text);}
        async Task Wait(Func<Task<bool>> predicate,int timeout=8000){long end=Environment.TickCount64+timeout;while(Environment.TickCount64<end){if(await predicate())return;await Task.Delay(50);}throw new IOException("双端状态等待超时。");}
        try
        {
            if(!App.IsSandbox)throw new IOException("好友检查仅允许沙箱。");owner.KeepOpenForVerification=true;owner.WindowState=WindowState.Minimized;
            string file=Path.Combine(App.Store.DesktopRoots[0],"host.url");File.WriteAllText(file,"[InternetShortcut]\r\nURL=https://example.invalid/\r\n");App.Store.Collect(file,"office");
            var before=VerifiedContent.Snapshot(App.Store.DataDirectory);var icons=GameIconCatalog.Collected(App.Store,"office");var options=new GameSettings{Rule=BilliardsRules.Nine,Players=2,Sound=false};
            var setup=new BilliardsSetupWindow(App.Store,"office",true,new GameStorage(App.Store.DataDirectory)){Owner=owner};
            setup.Loaded+=async(_,_)=>{await Task.Delay(150);var mode=(System.Windows.Controls.ComboBox)typeof(BilliardsSetupWindow).GetField("_players",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(setup)!;mode.SelectedIndex=2;var start=(System.Windows.Controls.Button)typeof(BilliardsSetupWindow).GetField("_start",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(setup)!;start.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));};
            Check(setup.ShowDialog()==true&&setup.Online?.Role=="host"&&setup.Settings.Players==2,"Actual setup selector produces a two-player host launch without writing network credentials");
            owner.WindowState=WindowState.Minimized;
            host=new(Path.Combine(root,"HostData"));guest=new(Path.Combine(root,"GuestData"));
            await host.Launch(icons,options,new(0,0,1920,1080),true,true,new(){Role="host",LoopbackOnly=true});
            await Wait(async()=>!(await host.Probe()).Invitation.Equals(""));var invitation=(await host.Probe()).Invitation;
            var join=new FriendJoinWindow(false){Owner=owner};join.Loaded+=async(_,_)=>{await Task.Delay(100);((System.Windows.Controls.TextBox)typeof(FriendJoinWindow).GetField("_invite",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(join)!).Text=invitation;System.Windows.Controls.Button? button=null;void Find(System.Windows.DependencyObject node){if(node is System.Windows.Controls.Button candidate&&Equals(candidate.Content,"加入房间"))button=candidate;for(int n=0;n<System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);n++)Find(System.Windows.Media.VisualTreeHelper.GetChild(node,n));}Find((System.Windows.DependencyObject)join.Content);button!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));};
            Check(join.ShowDialog()==true&&join.Invitation==invitation,"Actual invitation dialog validates pasted invitation and returns the selected room");owner.WindowState=WindowState.Minimized;
            Check(host.ChildPid.HasValue,"Host runs in its own game process and exposes an ephemeral invitation");
            await guest.Launch([new("guest-local","不同的本地图案","")],new(){Rule=BilliardsRules.Nine,Players=2,Sound=false},new(0,0,1920,1080),true,true,new(){Role="join",Invitation=invitation,LoopbackOnly=true});
            await Wait(async()=>(await host.Probe()).NetworkConnected&&(await guest.Probe()).NetworkConnected);
            Check(host.ChildPid!=guest.ChildPid,"Two independent game processes join through actual TCP/TLS");
            var a=await host.Probe();var b=await guest.Probe();Check(a.LocalSeat==0&&b.LocalSeat==1&&a.RulesVisible&&b.RulesVisible,"Each player receives its seat and opening instructions");
            samples.Add(new{phase="connected-opening-rules",hostPrivateWorkingSet=a.PrivateWorkingSet,guestPrivateWorkingSet=b.PrivateWorkingSet,totalPrivateWorkingSet=a.PrivateWorkingSet+b.PrivateWorkingSet});
            await host.Probe("begin");await Task.Delay(100);Check(!(await host.Probe()).NetworkStarted,"One ready player cannot start the shared game");await guest.Probe("begin");
            await Wait(async()=>(await guest.Probe()).NetworkStarted);Check((await host.Probe()).NetworkStarted,"Both ready starts the single authoritative round");
            await guest.Probe("net-shoot");await Task.Delay(150);Check((await host.Probe()).Shots==0,"Guest cannot shoot during the host turn");
            await host.Probe("net-shoot");await Wait(async()=>(await guest.Probe()).Shots==1);
            await Wait(async()=>{var one=await host.Probe();var two=await guest.Probe();return one.CurrentPlayer==1&&one.NetworkHash==two.NetworkHash;});
            Check((await host.Probe()).NetworkHash==(await guest.Probe()).NetworkHash,"Both processes agree on settled ball positions, tray and turn");
            await guest.Probe("net-place");await Task.Delay(100);await guest.Probe("net-disconnect");await Wait(async()=>!(await host.Probe()).NetworkConnected,3000);
            await Wait(async()=>(await host.Probe()).NetworkConnected&&(await guest.Probe()).NetworkConnected,10000);
            Check((await host.Probe()).Shots==1&&(await guest.Probe()).Shots==1,"Transport loss restores the reserved guest seat without replaying the shot");
            await host.Probe("net-pause");await guest.Probe("net-pause");await Wait(async()=>(await guest.Probe()).Paused);Check((await host.Probe()).Paused,"Shared pause requires and receives both votes");
            await Task.Delay(2000);var beforePauseHost=await host.Probe();var beforePauseGuest=await guest.Probe();await Task.Delay(3000);var pausedHost=await host.Probe();var pausedGuest=await guest.Probe();samples.Add(new{phase="paused-warm-3s",hostPrivateWorkingSet=pausedHost.PrivateWorkingSet,guestPrivateWorkingSet=pausedGuest.PrivateWorkingSet,totalPrivateWorkingSet=pausedHost.PrivateWorkingSet+pausedGuest.PrivateWorkingSet,hostCpuMs=pausedHost.CpuMs-beforePauseHost.CpuMs,guestCpuMs=pausedGuest.CpuMs-beforePauseGuest.CpuMs,hostFrameSubscribed=pausedHost.FrameSubscribed,guestFrameSubscribed=pausedGuest.FrameSubscribed});
            Check(!pausedHost.FrameSubscribed&&!pausedGuest.FrameSubscribed,"Globally paused network views stop their rendering subscriptions");
            await host.Probe("net-resume");await guest.Probe("net-resume");await Wait(async()=>!(await guest.Probe()).Paused);
            await host.Probe("net-restart");await guest.Probe("net-restart");await Wait(async()=>(await host.Probe()).Round==2&&(await guest.Probe()).Round==2);
            Check((await host.Probe()).RulesVisible&&(await guest.Probe()).RulesVisible,"Consensual restart resets both players and reopens the rules");
            var shot=await host.Probe("preview");File.WriteAllBytes(Path.Combine(root,"friend-host.png"),Convert.FromBase64String(shot.Image));var other=await guest.Probe("preview");File.WriteAllBytes(Path.Combine(root,"friend-guest.png"),Convert.FromBase64String(other.Image));
            await host.Probe("begin");await guest.Probe("begin");await Wait(async()=>(await guest.Probe()).NetworkStarted);await host.Probe("net-finish");await Wait(async()=>(await host.Probe()).Winner==0&&(await guest.Probe()).Winner==0);
            var hostStore=new GameStorage(Path.Combine(root,"HostData"));var guestStore=new GameStorage(Path.Combine(root,"GuestData"));await Wait(()=>Task.FromResult(hostStore.Scores().Count==1&&guestStore.Scores().Count==1));
            Check(hostStore.Scores()[0].Connection=="online"&&hostStore.Scores()[0].LocalSeat==0&&guestStore.Scores()[0].Connection=="online"&&guestStore.Scores()[0].LocalSeat==1,"Authoritative network victory is recorded once on each side with separate local seats");
            Check(hostStore.BestForRule(BilliardsRules.Nine) is null&&guestStore.BestForRule(BilliardsRules.Nine) is null,"Network records never replace solo best scores");
            await Task.Delay(700);var result=await guest.Probe("preview");File.WriteAllBytes(Path.Combine(root,"friend-result.png"),Convert.FromBase64String(result.Image));
            guest.Stop();host.Stop();Check(!guest.Running&&!host.Running,"Owner maintenance closes both game processes even while rules are open");
            Check(VerifiedContent.Equal(before,VerifiedContent.Snapshot(App.Store.DataDirectory)),"Network game never changes collection metadata, sorting or collected content");
            Check(hostStore.Scores().Count==1&&guestStore.Scores().Count==1,"Only the completed second round is recorded; the interrupted first round is omitted");
        }
        catch(Exception ex){failure=ex.ToString();}
        finally
        {
            try{guest?.Dispose();host?.Dispose();}catch(Exception ex){failure+="\nCleanup: "+ex;}
            File.WriteAllText(Path.Combine(root,"friend-verification.json"),JsonSerializer.Serialize(new{passed=checks.Count,failure,scope="two isolated game processes; loopback TCP/TLS; no external network claim",checks,samples,forcedGc=false},new JsonSerializerOptions{WriteIndented=true}));Application.Current.Shutdown(failure.Length==0?0:1);
        }
    }
}
