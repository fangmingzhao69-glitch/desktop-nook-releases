using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopNook.Games.Core;

namespace DesktopNook.App;
internal sealed class BilliardsSurface : FrameworkElement,IDisposable
{
    internal readonly BilliardsRound Round;
    private readonly List<GameIcon> _icons;
    private readonly Dictionary<string,ImageSource> _images=new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch _clock=Stopwatch.StartNew();private double _last,_chargeStart;
    private readonly GameIconProvider _provider=new();
    private bool _frames,_disposed;private Vec _pointer=new(1100,450);
    internal bool Guide {get;set;}
    internal Action? Changed;
    internal Action? Completed;
    private int _reported;
    internal bool MeasureFrames;
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
    private static readonly Pen IconRim=new(ColorBrush("#667ECBD0"),1);
    private static readonly Typeface TextFace=new("Microsoft YaHei UI");
    private FormattedText? _brand;
    private readonly Pen _rim=new(Trim,2),_guide=new(Brushes.White,2){DashStyle=DashStyles.Dash};
    private readonly Pen _object=new(ColorBrush("#6FE7B2"),3),_white=new(ColorBrush("#70CFFF"),3);
    internal BilliardsSurface(List<GameIcon> icons,int count,bool guide)
    {
        _icons=icons;Round=new(icons.Count,count);Guide=guide;Focusable=true;ClipToBounds=true;
        Loaded+=(_,_)=>{foreach(var icon in _icons)_images[Key(icon)]=ShellIntegration.AppIcon;_provider.Start(_icons,IconReady);InvalidateVisual();};
        Unloaded+=(_,_)=>Dispose();
        MouseMove+=(_,e)=>{_pointer=World(e.GetPosition(this));if(!Round.NeedsFrames)InvalidateVisual();if(Round.CanShoot){var hit=Round.Balls.FirstOrDefault(b=>b.Id>0&&!b.Sunk&&(b.Position-_pointer).Length<28);ToolTip=hit is null?null:_icons[hit.Source].Name;}};
        MouseLeftButtonDown+=(_,e)=>
        {
            Focus();_pointer=World(e.GetPosition(this));Sync();
            if(Round.Phase==RoundPhase.WhitePlacement){Round.PlaceWhite(_pointer);InvalidateVisual();Changed?.Invoke();return;}
            if(_pointer.X<BilliardsRound.Left||_pointer.X>BilliardsRound.Right||_pointer.Y<BilliardsRound.Top||_pointer.Y>BilliardsRound.Bottom)return;
            if(Round.BeginCharge()){_chargeStart=_clock.Elapsed.TotalSeconds;CaptureMouse();Wake();e.Handled=true;}
        };
        MouseLeftButtonUp+=(_,e)=>{Sync();if(Round.Phase==RoundPhase.Charging){var target=World(e.GetPosition(this));if(target.X>=BilliardsRound.Left&&target.X<=BilliardsRound.Right&&target.Y>=BilliardsRound.Top&&target.Y<=BilliardsRound.Bottom)Round.Shoot(target,Charge);else Round.CancelCharge();}ReleaseMouseCapture();Wake();Changed?.Invoke();};
        LostMouseCapture+=(_,_)=>{Round.CancelCharge();InvalidateVisual();};
    }
    private static string Key(GameIcon icon)=>icon.Recycle?"::recycle":icon.Path;
    private void IconReady(string key,ImageSource image)
    {
        if(_disposed||Dispatcher.HasShutdownStarted)return;
        Dispatcher.BeginInvoke(()=>{if(!_disposed){_images[key]=image;InvalidateVisual();}});
    }
    private (double Scale,double X,double Y) Projection(){double s=Math.Max(.01,Math.Min(ActualWidth/1600,ActualHeight/900));return(s,(ActualWidth-1600*s)/2,(ActualHeight-900*s)/2);}
    private Vec World(Point p){var(s,x,y)=Projection();return new((p.X-x)/s,(p.Y-y)/s);}
    internal double Charge=>Math.Clamp((_clock.Elapsed.TotalSeconds-_chargeStart)/.9,0,1);
    internal void Sync(){double now=_clock.Elapsed.TotalSeconds;Round.Advance(now-_last);_last=now;}
    internal void Wake(){_last=_clock.Elapsed.TotalSeconds;if(!_frames&&!_disposed){_lastFrame=0;_nextFrame=0;_frames=true;CompositionTarget.Rendering+=Frame;}InvalidateVisual();}
    private void Frame(object? sender,EventArgs e)
    {
        double tick=e is RenderingEventArgs frame?frame.RenderingTime.TotalMilliseconds:_clock.Elapsed.TotalMilliseconds;if(_nextFrame>0&&tick+.2<_nextFrame)return;
        _nextFrame=_nextFrame<=0||tick-_nextFrame>50?tick+1000d/60:_nextFrame+1000d/60;
        if(MeasureFrames){double now=_clock.Elapsed.TotalMilliseconds;if(_lastFrame>0){if(_frameTimes.Count>=600)_frameTimes.RemoveAt(0);_frameTimes.Add(now-_lastFrame);}_lastFrame=now;}
        Sync();InvalidateVisual();Changed?.Invoke();
        if(Round.Phase==RoundPhase.Finished&&_reported!=Round.RoundNumber){_reported=Round.RoundNumber;Completed?.Invoke();}
        if(!Round.NeedsFrames)StopFrames();
    }
    private void StopFrames(){if(_frames){CompositionTarget.Rendering-=Frame;_frames=false;}}
    internal bool HasFrameSubscription=>_frames;
    internal void Pause(bool paused){Sync();Round.SetPaused(paused);if(paused){ReleaseMouseCapture();StopFrames();}else if(Round.NeedsFrames)Wake();_last=_clock.Elapsed.TotalSeconds;InvalidateVisual();Changed?.Invoke();}
    internal void Restart(){StopFrames();Round.Reset(_icons.Count,Round.Total);_last=_clock.Elapsed.TotalSeconds;InvalidateVisual();Changed?.Invoke();}
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
        if(Guide&&!Round.Paused&&Round.Phase is RoundPhase.Ready or RoundPhase.Charging)
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
                dc.DrawEllipse(null,IconRim,Point(pos),radius,radius);
                if(_images.TryGetValue(Key(_icons[ball.Source]),out var image))dc.DrawImage(image,new Rect(pos.X-radius,pos.Y-radius,radius*2,radius*2));
            }
            if(ball.Impact>.02)dc.DrawEllipse(null,new Pen(Trim,ball.Impact*10),Point(pos),radius+5,radius+5);
            if(rotate)dc.Pop();dc.Pop();
        }
        if(Round.Phase==RoundPhase.Charging)
        {
            Vec d=(_pointer-Round.White.Position).Unit,p=Round.White.Position-d*(35+Charge*45);dc.DrawLine(Cue,Point(p),Point(p-d*110));
            dc.DrawRoundedRectangle(Hole,null,new(590,855,420,25),10,10);dc.DrawRoundedRectangle(Trim,null,new(594,859,412*Charge,17),7,7);Text(dc,$"力度 {Charge*100:0}%",new(800,858),12,Brushes.White,true);
        }
        if(Round.Phase==RoundPhase.WhitePlacement)Text(dc,"点击台面空处摆放白球",new(800,440),25,Brushes.White,true);
        if(Round.Paused||Round.Phase==RoundPhase.Finished)
        {
            dc.DrawRoundedRectangle(Overlay,_rim,new(420,300,760,260),25,25);
            Text(dc,Round.Phase==RoundPhase.Finished?"清台完成":"已暂停",new(800,345),38,Brushes.White,true);
            Text(dc,Round.Phase==RoundPhase.Finished?$"{(int)Round.Elapsed/60:00}:{(int)Round.Elapsed%60:00}  ·  {Round.Shots} 杆  ·  平均 {Round.Elapsed/Round.Total:0.00} 秒 / 图标":"点击上方“继续”或按空格",new(800,420),20,Ink,true);
        }
        double dpi=VisualTreeHelper.GetDpi(this).PixelsPerDip;if(_brand is null||_brand.PixelsPerDip!=dpi)_brand=new FormattedText("轻匣 · 图标台球",CultureInfo.CurrentCulture,FlowDirection.LeftToRight,TextFace,13,Trim,dpi);
        dc.DrawText(_brand,new(800-_brand.Width/2,882));dc.Pop();
    }
    public void Dispose(){if(_disposed)return;_disposed=true;StopFrames();_provider.Dispose();_images.Clear();}
}

internal sealed partial class BilliardsWindow : Window
{
    internal readonly BilliardsSurface Scene;
    private readonly Func<GameMessage,Task> _send;
    private readonly DispatcherTimer _hud=new(){Interval=TimeSpan.FromSeconds(1)};
    private readonly TextBlock _stats=new(){FontSize=13,Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center};
    private readonly TextBlock _hint=new(){Foreground=new SolidColorBrush(Color.FromRgb(181,214,228)),FontSize=11,VerticalAlignment=VerticalAlignment.Center};
    private readonly Button _pause=new(){Content="暂停",MinWidth=65};
    private bool _ready;
    internal BilliardsWindow(GameMessage start,Func<GameMessage,Task> send)
    {
        _send=send;Title="轻匣 · 图标台球";Icon=new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/AppIcon.ico"));
        WindowStyle=WindowStyle.SingleBorderWindow;ResizeMode=ResizeMode.CanResize;Background=new SolidColorBrush(Color.FromRgb(10,38,54));
        var fallback=SystemParameters.WorkArea;Width=double.IsFinite(start.Width)&&start.Width>0?Math.Min(start.Width,4096):fallback.Width;Height=double.IsFinite(start.Height)&&start.Height>0?Math.Min(start.Height,2160):fallback.Height;
        MinWidth=Math.Min(800,Width);MinHeight=Math.Min(600,Height);
        Left=double.IsFinite(start.Left)?start.Left:fallback.Left;Top=double.IsFinite(start.Top)?start.Top:fallback.Top;WindowStartupLocation=WindowStartupLocation.Manual;
        Scene=new(start.Icons,start.Count,start.Guide){MeasureFrames=start.TestMode};if(start.TestMode)Topmost=true;var root=new DockPanel();var top=new DockPanel{Margin=new Thickness(20,12,20,12)};
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(actions,Dock.Right);
        var guide=new CheckBox{Content="辅助线",IsChecked=start.Guide,Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,15,0)};
        guide.Checked+=(_,_)=>SetGuide(true);guide.Unchecked+=(_,_)=>SetGuide(false);actions.Children.Add(guide);actions.Children.Add(_pause);
        var restart=new Button{Content="重新开始",Margin=new Thickness(8,0,8,0)};restart.Click+=(_,_)=>{Scene.Pause(true);if(UiDialogs.Show(this,"结束当前对局并重新摆球？","重新开始",MessageBoxButton.OKCancel)==MessageBoxResult.OK)Scene.Restart();UpdateHud();};actions.Children.Add(restart);
        var close=new Button{Content="退出游戏"};close.Click+=(_,_)=>Close();actions.Children.Add(close);top.Children.Add(actions);top.Children.Add(_stats);DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
        var bottom=new DockPanel{Margin=new Thickness(20,8,20,10)};var license=new Button{Content="第三方许可",FontSize=10,Padding=new Thickness(5)};license.Click+=(_,_)=>{Scene.Pause(true);using var stream=typeof(BilliardsWindow).Assembly.GetManifestResourceStream("DesktopNook.BilliardsLicense");using var reader=new StreamReader(stream!);UiDialogs.Show(this,"参考 Desktop Icon Billiards · LXG\n\n"+reader.ReadToEnd(),"开源许可");};DockPanel.SetDock(license,Dock.Right);bottom.Children.Add(license);
        _hint.Text=$"来源 {start.Icons.Count} 个图标 · 本局 {start.Count} 个"+(start.Icons.Count<start.Count?"（游戏内补球）":"")+" · 按住蓄力，松手击球 · Esc 退出";bottom.Children.Add(_hint);DockPanel.SetDock(bottom,Dock.Bottom);root.Children.Add(bottom);root.Children.Add(Scene);Content=root;
        foreach(var button in actions.Children.OfType<Button>().Append(license)){button.Background=new SolidColorBrush(Color.FromRgb(220,241,250));button.Foreground=new SolidColorBrush(Color.FromRgb(21,63,85));button.Padding=new Thickness(12,7,12,7);}
        _pause.Click+=(_,_)=>Scene.Pause(!Scene.Round.Paused);Scene.Changed=UpdateHud;Scene.Completed=()=>{_=Notify(new(){Kind="RoundFinished",Round=Scene.Round.RoundNumber,Count=Scene.Round.Total,Shots=Scene.Round.Shots,Milliseconds=Scene.Round.Elapsed*1000});UpdateHud();};
        _hud.Tick+=(_,_)=>{Scene.Sync();UpdateHud();};Loaded+=(_,_)=>{_ready=true;UpdateHud();Scene.Focus();};
        Deactivated+=(_,_)=>{if(_ready)Scene.Pause(true);};StateChanged+=(_,_)=>{if(WindowState==WindowState.Minimized)Scene.Pause(true);};
        SizeChanged+=(_,_)=>{if(_ready){Scene.Pause(true);_hint.Text="窗口尺寸已变化，画面已适配；点击继续。";}};
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){e.Handled=true;Close();}else if(e.Key==Key.Space&&e.OriginalSource is not CheckBox){e.Handled=true;Scene.Pause(!Scene.Round.Paused);}};
        Closed+=(_,_)=>{_hud.Stop();Scene.Dispose();};
    }
    private void SetGuide(bool value){Scene.Guide=value;Scene.InvalidateVisual();_=Notify(new(){Kind="SettingsChanged",Guide=value});}
    private async Task Notify(GameMessage value){try{await _send(value);}catch{Close();}}
    private void UpdateHud()
    {
        var round=Scene.Round;string text=$"图标台球   {(int)round.Elapsed/60:00}:{(int)round.Elapsed%60:00}   进球 {round.Pocketed}/{round.Total}   {round.Shots} 杆";if(_stats.Text!=text)_stats.Text=text;
        _pause.Content=round.Paused?"继续":"暂停";
        if(round.Started&&!round.Paused&&round.Phase!=RoundPhase.Finished)_hud.Start();else _hud.Stop();
    }
    protected override void OnDpiChanged(DpiScale oldDpi,DpiScale newDpi){base.OnDpiChanged(oldDpi,newDpi);if(_ready){Scene.Pause(true);Scene.InvalidateVisual();_hint.Text="显示缩放已变化，画面已适配；点击继续。";}}
}
