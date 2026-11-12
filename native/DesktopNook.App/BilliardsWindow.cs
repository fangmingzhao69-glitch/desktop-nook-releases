using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopNook.Games.Core;
using DesktopNook.Games.Network;

namespace DesktopNook.App;
internal sealed class BilliardsSurface : FrameworkElement,IDisposable
{
    internal readonly BilliardsRound Round;
    private readonly List<GameIcon> _icons;
    private readonly Dictionary<string,ImageSource> _images=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int,DrawingGroup> _ballArt=[];
    private readonly Stopwatch _clock=Stopwatch.StartNew();private double _last,_chargeStart;
    private readonly GameIconProvider _provider=new();
    internal readonly BilliardsSound Audio=new();
    private bool _frames,_disposed;private Vec _pointer=new(1100,450);
    internal bool Guide {get;set;}
    internal Action? Changed;
    internal Action? Completed;
    private int _reported;
    internal bool MeasureFrames;
    internal string InputTrace="";
    internal GamePeer? Peer;
    internal bool LocalBlocked;
    private bool _remoteCharge,_awaiting;
    internal Action<string>? NetworkNotice;
    private bool Charging=>Peer is null?Round.Phase==RoundPhase.Charging:_remoteCharge;
    private bool CanAim=>Peer is null?!Round.Paused&&Round.Phase is RoundPhase.Ready or RoundPhase.Charging:Peer.CanAct&&!LocalBlocked&&!_awaiting&&Round.Phase==RoundPhase.Ready;
    private readonly List<double> _frameTimes=[];
    private double _lastFrame;
    private double _nextFrame;
    internal double FrameP95=>_frameTimes.Count==0?0:_frameTimes.Order().ElementAt((int)((_frameTimes.Count-1)*.95));
    internal double FrameMean=>_frameTimes.Count==0?0:_frameTimes.Average();
    internal int FrameCount=>_frameTimes.Count;
    internal void ResetMetrics(){_frameTimes.Clear();_lastFrame=0;}
    private static SolidColorBrush ColorBrush(string value){var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));brush.Freeze();return brush;}
    private static readonly Brush Cloth=ColorBrush("#0B625A"),Rail=ColorBrush("#123F4C"),Trim=ColorBrush("#63BCD3"),Hole=ColorBrush("#071E29"),Ink=ColorBrush("#D6F2F2"),Shade=ColorBrush("#660A2435");
    private static readonly Brush Back=ColorBrush("#0A2636"),WhiteShade=ColorBrush("#DDE9EE"),Overlay=ColorBrush("#DA0A2636");
    private static readonly Pen HoleRim=new(Trim,1),WhiteRim=new(ColorBrush("#A1C5D1"),1),Cue=new(ColorBrush("#C4A57B"),7);
    private static readonly Typeface TextFace=new("Microsoft YaHei UI");
    private FormattedText? _brand;
    private readonly Pen _rim=new(Trim,2),_guide=new(Brushes.White,2){DashStyle=DashStyles.Dash};
    private readonly Pen _object=new(ColorBrush("#6FE7B2"),3),_white=new(ColorBrush("#70CFFF"),3);
    internal BilliardsSurface(List<GameIcon> icons,string rule,bool guide,int players=1)
    {
        _icons=icons;Round=new(icons.Count,rule,players);Round.Sound=Audio.Play;Guide=guide;Focusable=true;ClipToBounds=true;
        Loaded+=(_,_)=>{foreach(var icon in _icons)_images[Key(icon)]=ShellIntegration.AppIcon;_provider.Start(_icons,IconReady);InvalidateVisual();};
        Unloaded+=(_,_)=>Dispose();
        MouseMove+=(_,e)=>{_pointer=World(e.GetPosition(this));if(!Round.NeedsFrames)InvalidateVisual();if(Round.CanShoot){var hit=Round.Balls.FirstOrDefault(b=>b.Id>0&&!b.Sunk&&(b.Position-_pointer).Length<28);ToolTip=hit is null?null:$"{hit.Style.Number} 号 · {_icons[hit.Source].Name}";}};
        MouseLeftButtonDown+=(_,e)=>
        {
            Focus();_pointer=World(e.GetPosition(this));Sync();
            if(Peer is not null)
            {
                if(!Peer.CanAct||LocalBlocked||_awaiting)return;
                if(Round.Phase==RoundPhase.WhitePlacement){_=SendOnline("Place",_pointer,0);return;}
                if(CanAim&&Inside(_pointer)){_remoteCharge=true;_chargeStart=_clock.Elapsed.TotalSeconds;CaptureMouse();Wake();e.Handled=true;}return;
            }
            if(MeasureFrames)InputTrace=$"down ({_pointer.X:F1},{_pointer.Y:F1}) {Round.Phase} paused={Round.Paused}";
            if(Round.Phase==RoundPhase.WhitePlacement){Round.PlaceWhite(_pointer);InvalidateVisual();Changed?.Invoke();return;}
            if(_pointer.X<BilliardsRound.Left||_pointer.X>BilliardsRound.Right||_pointer.Y<BilliardsRound.Top||_pointer.Y>BilliardsRound.Bottom)return;
            if(Round.BeginCharge()){_chargeStart=_clock.Elapsed.TotalSeconds;CaptureMouse();Wake();e.Handled=true;}
        };
        MouseLeftButtonUp+=(_,e)=>{Sync();var target=World(e.GetPosition(this));if(Peer is not null){bool send=_remoteCharge&&Inside(target);_remoteCharge=false;ReleaseMouseCapture();if(send)_=SendOnline("Shot",target,Charge);return;}if(MeasureFrames)InputTrace+=$"; up ({target.X:F1},{target.Y:F1}) {Round.Phase}";if(Round.Phase==RoundPhase.Charging){if(Inside(target))Round.Shoot(target,Charge);else Round.CancelCharge();}ReleaseMouseCapture();Wake();Changed?.Invoke();};
        LostMouseCapture+=(_,_)=>{_remoteCharge=false;if(MeasureFrames)InputTrace+=$"; capture-lost {Round.Phase}";Round.CancelCharge();InvalidateVisual();};
    }
    private static string Key(GameIcon icon)=>icon.Recycle?"::recycle":icon.Path;
    private void IconReady(string key,ImageSource image)
    {
        if(_disposed||Dispatcher.HasShutdownStarted)return;
        Dispatcher.BeginInvoke(()=>{if(!_disposed){_images[key]=image;_ballArt.Clear();InvalidateVisual();Changed?.Invoke();}});
    }
    private (double Scale,double X,double Y) Projection(){double s=Math.Max(.01,Math.Min(ActualWidth/1600,ActualHeight/900));return(s,(ActualWidth-1600*s)/2,(ActualHeight-900*s)/2);}
    private Vec World(Point p){var(s,x,y)=Projection();return new((p.X-x)/s,(p.Y-y)/s);}
    internal double Charge=>Math.Clamp((_clock.Elapsed.TotalSeconds-_chargeStart)/.9,0,1);
    private static bool Inside(Vec p)=>p.X>=BilliardsRound.Left&&p.X<=BilliardsRound.Right&&p.Y>=BilliardsRound.Top&&p.Y<=BilliardsRound.Bottom;
    internal async Task SendOnline(string kind,Vec target,double power)
    {
        if(Peer is null||_awaiting)return;_awaiting=true;
        try{var ack=await Peer.Command(kind,target.X,target.Y,power);if(!ack.Accept)NetworkNotice?.Invoke(ack.Message);}finally{_awaiting=false;if(!_disposed)RefreshNetwork();}
    }
    internal void Sync(){double now=_clock.Elapsed.TotalSeconds;if(Peer is null)Round.Advance(now-_last);else if(Peer.ViewForRender() is { } view)Round.ApplyView(view);_last=now;}
    internal void RefreshNetwork(){if(_disposed)return;Sync();if(Peer?.CanAct!=true){_remoteCharge=false;ReleaseMouseCapture();}if(!LocalBlocked&&(Peer?.Animating==true||_remoteCharge))Wake();else StopFrames();if(!LocalBlocked)InvalidateVisual();Changed?.Invoke();ReportComplete();}
    internal void Wake(){_last=_clock.Elapsed.TotalSeconds;if(!_frames&&!_disposed){_lastFrame=0;_nextFrame=0;_frames=true;CompositionTarget.Rendering+=Frame;}InvalidateVisual();}
    private void Frame(object? sender,EventArgs e)
    {
        double tick=e is RenderingEventArgs frame?frame.RenderingTime.TotalMilliseconds:_clock.Elapsed.TotalMilliseconds;if(_nextFrame>0&&tick+.2<_nextFrame)return;
        _nextFrame=_nextFrame<=0||tick-_nextFrame>50?tick+1000d/60:_nextFrame+1000d/60;
        if(MeasureFrames){double now=_clock.Elapsed.TotalMilliseconds;if(_lastFrame>0){if(_frameTimes.Count>=600)_frameTimes.RemoveAt(0);_frameTimes.Add(now-_lastFrame);}_lastFrame=now;}
        Sync();InvalidateVisual();Changed?.Invoke();
        ReportComplete();
        if(Peer is null?!Round.NeedsFrames:LocalBlocked||!(Peer.Animating||_remoteCharge))StopFrames();
    }
    private void StopFrames(){if(_frames){CompositionTarget.Rendering-=Frame;_frames=false;}}
    private void ReportComplete(){if(Round.Phase==RoundPhase.Finished&&_reported!=Round.RoundNumber){_reported=Round.RoundNumber;Completed?.Invoke();}}
    internal bool HasFrameSubscription=>_frames;
    internal void Pause(bool paused){Sync();if(Peer is null)Round.SetPaused(paused);else LocalBlocked=paused;if(paused){_remoteCharge=false;Audio.Stop();ReleaseMouseCapture();StopFrames();}else if(Peer is null?Round.NeedsFrames:Peer.Animating)Wake();_last=_clock.Elapsed.TotalSeconds;InvalidateVisual();Changed?.Invoke();}
    internal void Restart(){Audio.Stop();StopFrames();Round.Reset(_icons.Count,Round.RuleId,Round.Match.Players);_last=_clock.Elapsed.TotalSeconds;InvalidateVisual();Changed?.Invoke();}
    internal void RefreshDpi(){_ballArt.Clear();InvalidateVisual();}
    internal DrawingGroup BallArt(Ball ball)
    {
        if(!_ballArt.TryGetValue(ball.Id,out var art)){_images.TryGetValue(Key(_icons[ball.Source]),out var image);art=BilliardsBallDrawing.Create(ball,image,VisualTreeHelper.GetDpi(this).PixelsPerDip);_ballArt[ball.Id]=art;}return art;
    }
    private static Point Point(Vec p)=>new(p.X,p.Y);
    private void Text(DrawingContext dc,string text,Vec p,double size,Brush brush,bool center=false)
    {
        var value=new FormattedText(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,TextFace,size,brush,VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(value,new(p.X-(center?value.Width/2:0),p.Y));
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Back,null,new Rect(RenderSize));var(s,x,y)=Projection();dc.PushTransform(new MatrixTransform(s,0,0,s,x,y));
        dc.DrawRoundedRectangle(Rail,_rim,new(35,35,1530,830),48,48);dc.DrawRoundedRectangle(Cloth,null,new(76,76,1448,748),30,30);
        for(int n=1;n<8;n++){double xx=80+n*180;dc.DrawEllipse(Trim,null,new(xx,53),3,3);dc.DrawEllipse(Trim,null,new(xx,847),3,3);}
        foreach(var pocket in Round.Pockets){dc.DrawEllipse(Shade,null,Point(pocket),39,39);dc.DrawEllipse(Hole,HoleRim,Point(pocket),33,33);}
        if(Guide&&CanAim&&Round.Phase is RoundPhase.Ready or RoundPhase.Charging)
        {
            var preview=Round.Preview(_pointer);dc.DrawLine(_guide,Point(Round.White.Position),Point(preview.Contact));
            if(preview.ObjectDirection is Vec od)dc.DrawLine(_object,Point(preview.Contact),Point(preview.Contact+od*130));
            if(preview.WhiteDirection is Vec wd&&wd.Length>.01)dc.DrawLine(_white,Point(preview.Contact),Point(preview.Contact+wd.Unit*100));
        }
        foreach(var ball in Round.Balls)
        {
            if(ball.Sunk&&ball.ExitAge>=.62)continue;
            double t=ball.Sunk?Math.Clamp(ball.ExitAge/.62,0,1):0;Vec pos=ball.Sunk?ball.ExitFrom*(1-t*t)+ball.Pocket*(t*t):ball.Position;double radius=ball.Radius*(1-t);
            dc.PushOpacity(1-t);bool rotate=Math.Abs(ball.Angle+t*130)>.01;if(rotate)dc.PushTransform(new RotateTransform(ball.Angle+t*130,pos.X,pos.Y));
            dc.DrawEllipse(Shade,null,new(pos.X+2,pos.Y+5),radius+3,radius+3);
            if(ball.Id==0){dc.DrawEllipse(Brushes.White,WhiteRim,Point(pos),radius,radius);dc.DrawEllipse(WhiteShade,null,new(pos.X+5,pos.Y+5),radius*.5,radius*.5);}
            else
            {
                dc.PushTransform(new MatrixTransform(1-t,0,0,1-t,pos.X,pos.Y));dc.DrawDrawing(BallArt(ball));dc.Pop();
            }
            if(ball.Impact>.02)dc.DrawEllipse(null,new Pen(Trim,ball.Impact*10),Point(pos),radius+5,radius+5);
            if(rotate)dc.Pop();dc.Pop();
        }
        if(Charging)
        {
            Vec d=(_pointer-Round.White.Position).Unit,p=Round.White.Position-d*(35+Charge*45);dc.DrawLine(Cue,Point(p),Point(p-d*110));
            dc.DrawRoundedRectangle(Hole,null,new(590,855,420,25),10,10);dc.DrawRoundedRectangle(Trim,null,new(594,859,412*Charge,17),7,7);Text(dc,$"力度 {Charge*100:0}%",new(800,858),12,Brushes.White,true);
        }
        if(Round.Phase==RoundPhase.WhitePlacement)Text(dc,Peer is null||Peer.CanAct?"点击台面空处摆放白球":$"等待玩家 {Round.Match.CurrentPlayer+1} 摆放白球",new(800,440),25,Brushes.White,true);
        if(Round.Paused||Round.Phase==RoundPhase.Finished)
        {
            dc.DrawRoundedRectangle(Overlay,_rim,new(420,300,760,260),25,25);
            string result=Round.Match.Players==1?"清台完成":Round.Match.Winner is int winner?$"玩家 {winner+1} 获胜":"双方平局";
            Text(dc,Round.Phase==RoundPhase.Finished?result:Peer is not null?"好友房间":"已暂停",new(800,345),38,Brushes.White,true);
            Text(dc,Round.Phase==RoundPhase.Finished?(Round.Match.Players==2?$"比分 {Round.Match.Scores[0]} : {Round.Match.Scores[1]}  ·  {Round.Shots} 杆":$"{(int)Round.Elapsed/60:00}:{(int)Round.Elapsed%60:00}  ·  {Round.Shots} 杆  ·  平均 {Round.Elapsed/Round.Total:0.00} 秒 / 图标"):Peer?.Status??"点击上方“继续”或按空格",new(800,420),20,Ink,true);
        }
        double dpi=VisualTreeHelper.GetDpi(this).PixelsPerDip;if(_brand is null||_brand.PixelsPerDip!=dpi)_brand=new FormattedText("轻匣 · 图标台球",CultureInfo.CurrentCulture,FlowDirection.LeftToRight,TextFace,13,Trim,dpi);
        dc.DrawText(_brand,new(800-_brand.Width/2,882));dc.Pop();
    }
    public void Dispose(){if(_disposed)return;_disposed=true;StopFrames();Round.Sound=null;Audio.Dispose();_provider.Dispose();_images.Clear();_ballArt.Clear();}
}

internal sealed partial class BilliardsWindow : Window
{
    internal readonly BilliardsSurface Scene;
    private readonly Func<GameMessage,Task> _send;
    private readonly DispatcherTimer _hud=new(){Interval=TimeSpan.FromSeconds(1)};
    private readonly TextBlock _stats=new(){FontSize=12,Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};
    private readonly Button _pause=new(){Content="暂停",MinWidth=65};
    private readonly CheckBox _soundCheck=new(){Content="音效",VerticalAlignment=VerticalAlignment.Center,Foreground=Brushes.White,Margin=new Thickness(0,0,12,0)};
    private readonly Border _rules;
    private readonly Button _begin=new(){Content="开始练习",MinWidth=110,Padding=new Thickness(16,8,16,8)};
    private readonly BilliardsTray _leftTray,_rightTray;
    private readonly TextBlock _turn=new(){FontSize=12,Foreground=Brushes.LightCyan,Margin=new Thickness(14,0,14,5)};
    private bool _intro=true,_resumeAfterRules;
    internal bool RulesVisible=>_rules.Visibility==Visibility.Visible;
    private bool _soundEnabled;
    private bool _ready;
    internal BilliardsWindow(GameMessage start,Func<GameMessage,Task> send)
    {
        _send=send;_onlineRequested=start.Online is not null;Title="轻匣 · 图标台球";Icon=new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/AppIcon.ico"));
        WindowStyle=WindowStyle.SingleBorderWindow;ResizeMode=ResizeMode.CanResize;Background=new SolidColorBrush(Color.FromRgb(10,38,54));
        var fallback=SystemParameters.WorkArea;var screen=double.IsFinite(start.Width)&&double.IsFinite(start.Height)&&double.IsFinite(start.Left)&&double.IsFinite(start.Top)&&start.Width>0&&start.Height>0?new Rect(start.Left,start.Top,start.Width,start.Height):fallback;
        var bounds=BilliardsPlacement.Initial(screen);Width=bounds.Width;Height=bounds.Height;MinWidth=Math.Min(560,Width);MinHeight=Math.Min(320,Height);Left=bounds.Left;Top=bounds.Top;WindowStartupLocation=WindowStartupLocation.Manual;
        Scene=new(start.Icons,start.Rule,start.Guide,start.Players){MeasureFrames=start.TestMode};_soundEnabled=start.Sound;Scene.Audio.Enabled=start.Sound&&!start.TestMode;Scene.Round.SetPaused(true);
        if(start.TestMode){Topmost=!start.QuietTest;ShowActivated=!start.QuietTest;}var root=new DockPanel{Background=Background};var top=new Grid{Margin=new Thickness(14,8,14,8)};top.RowDefinitions.Add(new(){Height=GridLength.Auto});top.RowDefinitions.Add(new(){Height=GridLength.Auto});top.ColumnDefinitions.Add(new());top.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};Grid.SetColumn(actions,1);
        var guide=new CheckBox{Content="辅助线",IsChecked=start.Guide,Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,15,0)};
        guide.Checked+=(_,_)=>SetGuide(true);guide.Unchecked+=(_,_)=>SetGuide(false);actions.Children.Add(guide);_soundCheck.IsChecked=start.Sound;_soundCheck.ToolTip="力度越大，击球声越强；可随时关闭";_soundCheck.Checked+=(_,_)=>SetSound(true);_soundCheck.Unchecked+=(_,_)=>SetSound(false);actions.Children.Add(_soundCheck);actions.Children.Add(_pause);
        var instructions=new Button{Content="规则",Margin=new Thickness(8,0,0,0)};instructions.Click+=(_,_)=>ShowRules();actions.Children.Add(instructions);
        var restart=new Button{Content="重新开始",Margin=new Thickness(8,0,8,0)};restart.Click+=(_,_)=>{if(_onlineRequested){if(UiDialogs.Show(this,"向好友请求重新开始？双方同意后重置本局。","重开请求",MessageBoxButton.OKCancel)==MessageBoxResult.OK)_=OnlineCommand("Restart");return;}Scene.Pause(true);if(UiDialogs.Show(this,"结束当前对局并重新摆球？","重新开始",MessageBoxButton.OKCancel)==MessageBoxResult.OK)RestartRound();UpdateHud();};actions.Children.Add(restart);
        var close=new Button{Content="退出游戏"};close.Click+=(_,_)=>Close();actions.Children.Add(close);top.Children.Add(actions);top.Children.Add(_stats);DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);if(_onlineRequested){var bar=NetworkBar();DockPanel.SetDock(bar,Dock.Top);root.Children.Add(bar);}DockPanel.SetDock(_turn,Dock.Top);root.Children.Add(_turn);
        _leftTray=new(Scene,0){Width=110};_rightTray=new(Scene,1){Width=110};var arena=new DockPanel{Margin=new Thickness(2,0,2,6)};DockPanel.SetDock(_leftTray,Dock.Left);DockPanel.SetDock(_rightTray,Dock.Right);arena.Children.Add(_leftTray);arena.Children.Add(_rightTray);arena.Children.Add(Scene);root.Children.Add(arena);
        var card=new StackPanel{Margin=new Thickness(24)};card.Children.Add(new TextBlock{Text=BilliardsRules.Get(start.Rule).Name+" · "+(_onlineRequested?"好友联机规则":start.Players==2?"双人对战规则":"练习说明"),FontSize=20,FontWeight=FontWeights.SemiBold,Foreground=Brushes.White,Margin=new Thickness(0,0,0,14)});
        var explanation=new ScrollViewer{MaxHeight=Math.Max(120,Math.Min(420,Height*.52)),VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=new TextBlock{Text=OnlineInstructions(start),TextWrapping=TextWrapping.Wrap,Foreground=Brushes.LightCyan,FontSize=13,LineHeight=22}};card.Children.Add(explanation);_begin.Content=_onlineRequested?"准备对战":start.Players==2?"开始对战":"开始练习";_begin.IsEnabled=!_onlineRequested;_begin.Style=(Style)Application.Current.FindResource("PrimaryButton");_begin.IsDefault=true;_begin.Click+=(_,_)=>BeginGame();var ruleActions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,18,0,0)};var ruleExit=new Button{Content="退出游戏",Foreground=Brushes.LightCyan,Margin=new Thickness(0,0,12,0),Padding=new Thickness(12,8,12,8)};ruleExit.Click+=(_,_)=>Close();ruleActions.Children.Add(ruleExit);ruleActions.Children.Add(_begin);card.Children.Add(ruleActions);
        _rules=new Border{Background=new SolidColorBrush(Color.FromArgb(244,12,45,60)),BorderBrush=Brushes.CadetBlue,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(15),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Width=Math.Max(280,Math.Min(640,Width-60)),Child=card};KeyboardNavigation.SetTabNavigation(_rules,KeyboardNavigationMode.Cycle);var layers=new Grid();layers.Children.Add(root);var veil=new Border{Background=new SolidColorBrush(Color.FromArgb(175,3,18,28)),Child=_rules};layers.Children.Add(veil);_rules.Tag=veil;Content=layers;
        foreach(var button in actions.Children.OfType<Button>()){button.Background=new SolidColorBrush(Color.FromRgb(220,241,250));button.Foreground=new SolidColorBrush(Color.FromRgb(21,63,85));button.FontSize=11;button.Padding=new Thickness(8,5,8,5);}
        _pause.Click+=(_,_)=>{if(!RulesVisible){if(_onlineRequested)_=OnlinePause();else Scene.Pause(!Scene.Round.Paused);}};Scene.Changed=UpdateHud;Scene.Completed=()=>{var match=Scene.Round.Match;_=Notify(new(){Kind="RoundFinished",Connection=_onlineRequested?"online":"local",LocalSeat=_peer?.Seat??0,Rule=Scene.Round.RuleId,Players=match.Players,Winner=match.Winner,Score1=match.Scores[0],Score2=match.Scores[1],Round=Scene.Round.RoundNumber,Count=Scene.Round.Total,Shots=Scene.Round.Shots,Milliseconds=Scene.Round.Elapsed*1000});UpdateHud();};
        _hud.Tick+=(_,_)=>{Scene.Sync();UpdateHud();};Loaded+=(_,_)=>{_ready=true;UpdateHud();if(!start.QuietTest)_begin.Focus();};
        Deactivated+=(_,_)=>{if(_ready)Scene.Pause(true);};StateChanged+=(_,_)=>{if(WindowState==WindowState.Minimized)Scene.Pause(true);};
        SizeChanged+=(_,_)=>{bool compact=ActualWidth<900;Grid.SetRow(actions,compact?1:0);Grid.SetColumn(actions,compact?0:1);Grid.SetColumnSpan(actions,compact?2:1);Grid.SetColumnSpan(_stats,compact?2:1);actions.Margin=compact?new Thickness(0,6,0,0):new Thickness(0);_leftTray.Width=_rightTray.Width=ActualWidth<800?80:110;if(_ready){_rules.Width=Math.Max(200,Math.Min(640,ActualWidth-60));explanation.MaxHeight=Math.Max(80,Math.Min(420,ActualHeight*.52));Scene.Pause(true);}};
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){e.Handled=true;Close();}else if(e.Key==Key.Space&&!RulesVisible&&e.OriginalSource is not CheckBox&&WindowState!=WindowState.Minimized){e.Handled=true;if(_onlineRequested)_=OnlinePause();else Scene.Pause(!Scene.Round.Paused);}};
        Closed+=(_,_)=>{_hud.Stop();CloseNetwork();Scene.Dispose();};ConfigureNetwork(start);
    }
    private void SetGuide(bool value){Scene.Guide=value;Scene.InvalidateVisual();_=Notify(new(){Kind="SettingsChanged",Guide=value,Sound=_soundEnabled});}
    private void SetSound(bool value){_soundEnabled=value;Scene.Audio.Enabled=value;if(!value)Scene.Audio.Stop();_=Notify(new(){Kind="SettingsChanged",Guide=Scene.Guide,Sound=value});}
    internal void ShowRules(){_resumeAfterRules=!Scene.Round.Paused;Scene.Pause(true);_rules.Visibility=Visibility.Visible;((Border)_rules.Tag).Visibility=Visibility.Visible;_begin.Content=_intro?(Scene.Round.Match.Players==2?"开始对战":"开始练习"):"返回游戏";_begin.Focus();}
    internal void BeginGame(){bool first=_intro;_rules.Visibility=Visibility.Collapsed;((Border)_rules.Tag).Visibility=Visibility.Collapsed;if(_intro||_resumeAfterRules||_onlineRequested)Scene.Pause(false);_intro=false;if(first&&_onlineRequested)_=OnlineCommand("Ready");Scene.Focus();UpdateHud();}
    internal void RestartRound(){Scene.Restart();_intro=true;ShowRules();}
    private async Task Notify(GameMessage value){try{await _send(value);}catch{Close();}}
    private void UpdateHud()
    {
        var round=Scene.Round;string text=$"{BilliardsRules.Get(round.RuleId).Name}  {(int)round.Elapsed/60:00}:{(int)round.Elapsed%60:00}  {round.Pocketed}/{round.Total}  {round.Shots} 杆";if(_stats.Text!=text)_stats.Text=text;
        _pause.Content=_onlineRequested?(_peer?.Latest is {Started:true,Connected:true}&&round.Paused?"同意继续":"请求暂停"):round.Paused?"继续":"暂停";
        var match=round.Match;_turn.Text=match.Players==2?$"玩家 1：{match.Scores[0]}    玩家 2：{match.Scores[1]}    ·    当前玩家 {match.CurrentPlayer+1}    ·    目标：{match.Target}    ·    {match.LastMessage}":"单人练习";_leftTray.Refresh();_rightTray.Refresh();if(Scene.Audio.Unavailable)_soundCheck.ToolTip="当前音频设备不可用；游戏可继续，重新打开游戏可重试。";
        if(round.Started&&!round.Paused&&round.Phase!=RoundPhase.Finished)_hud.Start();else _hud.Stop();
    }
    protected override void OnDpiChanged(DpiScale oldDpi,DpiScale newDpi){base.OnDpiChanged(oldDpi,newDpi);if(_ready){Scene.Pause(true);Scene.RefreshDpi();}}
}
