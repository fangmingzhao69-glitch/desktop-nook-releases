using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopNook.Games.Core;
using DesktopNook.Games.Network;

namespace DesktopNook.App;
internal sealed partial class BilliardsWindow
{
    private readonly bool _onlineRequested;
    private readonly CancellationTokenSource _netLife=new();
    private readonly TextBlock _netStatus=new(){Foreground=Brushes.LightSkyBlue,FontSize=12,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};
    private readonly Button _copyInvite=new(){Content="复制邀请",IsEnabled=false,Margin=new Thickness(0,0,8,0)};
    private GamePeer? _peer;
    private GameRoomHost? _room;
    private string _invitation="",_feedback="";
    private DateTime _feedbackUntil;
    private bool _forceClose;
    private int _netScheduled,_netRound;
    private static string OnlineInstructions(GameMessage start)
    {
        string rules=BilliardsRules.Instructions(start.Rule,start.Players);
        return start.Online is null?rules:"好友联机：双方点击准备后开始，房主为玩家 1，朋友为玩家 2。两端使用各自图标，球号和规则相同。失焦和缩放只暂停本机操作，整局暂停 / 重开需要双方同意。断线保留 60 秒重连，房主退出结束本局。\n\n"+rules.Replace("本地双人轮流操作","双方轮流操作").Replace("简化本地对战规则","简化对战规则").Replace("空格暂停","空格申请暂停 / 继续").Replace("失焦或改变窗口大小自动暂停","联机时失焦或改变窗口大小只停止本机操作");
    }
    private FrameworkElement NetworkBar()
    {
        var panel=new DockPanel{Margin=new Thickness(14,0,14,7)};var actions=new StackPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(actions,Dock.Right);
        _copyInvite.Click+=(_,_)=>{try{GameInvite.Parse(_invitation);Clipboard.SetText(_invitation);Feedback("邀请已复制，有效期 2 小时，请发给朋友加入。");}catch(System.IO.InvalidDataException ex){Feedback(ex.Message);}catch{Feedback("剪贴板暂时不可用，可稍后重试。");}};actions.Children.Add(_copyInvite);
        var help=new Button{Content="连接帮助"};help.Click+=(_,_)=>
        {
            Scene.Pause(true);UiDialogs.Show(this,"同网可搜索房间，也可直接复制邀请。异地优先使用邀请内的公网 IPv6；组网工具提供的地址也可连接。\n\nTCP 默认端口 38471（冲突时会换用可用端口），局域网发现 UDP 38472。连接失败请确认房间仍在、邀请未过期，以及 Windows / 路由器是否允许所选端口入站。软件不会自动修改防火墙或路由器。\n\n局域网发现不包含邀请密钥，无密钥加入需房主确认。图标、文件名和文件内容不在网络上传送。本版本先完成本机双端测试，跨电脑 / 跨公网请另行验收。","好友连接帮助");if(IsActive&&!RulesVisible)Scene.Pause(false);
        };actions.Children.Add(help);foreach(var button in actions.Children.OfType<Button>()){button.Background=new SolidColorBrush(Color.FromRgb(220,241,250));button.Foreground=new SolidColorBrush(Color.FromRgb(21,63,85));button.FontSize=11;button.Padding=new Thickness(8,5,8,5);}panel.Children.Add(actions);panel.Children.Add(_netStatus);return panel;
    }
    private void ConfigureNetwork(GameMessage start)
    {
        if(!_onlineRequested)return;
        Scene.NetworkNotice=Feedback;_netStatus.Text=start.Online!.Role=="host"?"正在创建好友房间…":"正在连接好友房间…";
        Loaded+=async(_,_)=>await OpenNetwork(start);
        Activated+=(_,_)=>{if(_ready&&!RulesVisible&&WindowState==WindowState.Normal)Scene.Pause(false);};
        StateChanged+=(_,_)=>{if(WindowState==WindowState.Normal&&!RulesVisible)Scene.Pause(false);};
        SizeChanged+=(_,_)=>{if(_ready&&IsActive&&!RulesVisible)Dispatcher.BeginInvoke(()=>{if(IsActive&&WindowState==WindowState.Normal&&!RulesVisible)Scene.Pause(false);});};
        Closing+=(_,e)=>
        {
            if(_forceClose||_peer is null)return;
            if(UiDialogs.Show(this,"退出会断开好友房间。未完成的本局不记为正常胜负，确认退出？","退出好友对局",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)e.Cancel=true;
        };
    }
    private async Task OpenNetwork(GameMessage start)
    {
        try
        {
            var options=start.Online!;
            if(options.Role=="host")
            {
                _room=await Task.Run(()=>new GameRoomHost(start.Rule,options.LoopbackOnly?IPAddress.Loopback:null,options.LoopbackOnly?0:GameRoomHost.DefaultPort),_netLife.Token);
                if(_netLife.IsCancellationRequested){_room.Dispose();return;}
                _room.ApproveLanJoin=async name=>await Dispatcher.InvokeAsync(()=>UiDialogs.Show(this,name+" 请求加入当前局域网房间，是否允许？","好友加入请求",MessageBoxButton.OKCancel)==MessageBoxResult.OK);
                if(!options.LoopbackOnly)try{_room.Announce();}catch(System.Net.Sockets.SocketException){Feedback("房间可通过邀请加入，局域网发现暂不可用。");}
                _invitation=_room.Invitation.Encode();_copyInvite.IsEnabled=true;_peer=GamePeer.ForHost(_room);
            }
            else _peer=await GamePeer.Join(GameInvite.Parse(options.Invitation),_netLife.Token,message=>Dispatcher.BeginInvoke(()=>_netStatus.Text=message));
            if(_netLife.IsCancellationRequested){_peer.Dispose();return;}
            Scene.Peer=_peer;Scene.LocalBlocked=true;_peer.Changed+=PeerChanged;_peer.Sound+=RemoteSound;_begin.IsEnabled=true;PeerChanged();
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(!_netLife.IsCancellationRequested){_netStatus.Text="好友连接未完成："+ex.Message;_begin.IsEnabled=false;}}
    }
    private void RemoteSound(BallSound sound)
    {
        if(_netLife.IsCancellationRequested||Dispatcher.HasShutdownStarted)return;Dispatcher.BeginInvoke(()=>{if(!_netLife.IsCancellationRequested&&!Scene.LocalBlocked)Scene.Audio.Play(sound);});
    }
    private void PeerChanged()
    {
        if(_netLife.IsCancellationRequested||Dispatcher.HasShutdownStarted||Interlocked.Exchange(ref _netScheduled,1)!=0)return;
        Dispatcher.BeginInvoke(()=>
        {
            Interlocked.Exchange(ref _netScheduled,0);if(_netLife.IsCancellationRequested||_peer is null)return;
            Scene.RefreshNetwork();var view=_peer.Latest;
            if(view is not null)
            {
                if(_netRound>0&&view.Round.Round!=_netRound){_intro=true;ShowRules();}_netRound=view.Round.Round;
                _netStatus.Text=$"你是玩家 {_peer.Seat+1} · "+(DateTime.UtcNow<_feedbackUntil?_feedback:_peer.Status);
                _pause.IsEnabled=view.Started&&view.Connected&&!view.Ended;
                if(!view.Started)_begin.Content=view.HostReady&&_peer.Seat==0||view.GuestReady&&_peer.Seat==1?"已准备":"准备对战";
            }
        });
    }
    private void Feedback(string text){_feedback=text;_feedbackUntil=DateTime.UtcNow.AddSeconds(4);if(_onlineRequested)_netStatus.Text=text;}
    private async Task OnlineCommand(string kind)
    {
        if(_peer is null){Feedback("尚未连接到房间。");return;}var ack=await _peer.Command(kind);if(!ack.Accept)Feedback(ack.Message);PeerChanged();
    }
    private Task OnlinePause()=>OnlineCommand(_peer?.Latest is {Started:true,Connected:true} view&&view.Round.Paused?"Resume":"Pause");
    private void CloseNetwork(){_netLife.Cancel();if(_peer is not null){_peer.Changed-=PeerChanged;_peer.Sound-=RemoteSound;_peer.Dispose();}else _room?.Dispose();}
    internal void CloseFromOwner(){_forceClose=true;Close();}
}
