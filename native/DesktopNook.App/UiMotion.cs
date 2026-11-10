using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DesktopNook.App;

public static class UiMotion
{
    private sealed record Job(AnimationClock Clock,EventHandler Completed);
    private sealed record RevealState(Transform Original,TranslateTransform Offset);
    private sealed class FeedbackState
    {
        internal Border? Surface;
        internal SolidColorBrush? Brush;
        internal int Revision;
    }
    private static readonly Dictionary<(DependencyObject Target,DependencyProperty Property),Job> Jobs=[];
    private static readonly Dictionary<FrameworkElement,RevealState> Reveals=[];
    private static readonly ConditionalWeakTable<Control,FeedbackState> FeedbackStates=new();
    private static bool _subscribed;
    internal static string Mode {get;private set;}="system";
    internal static bool Enabled=>Mode=="on"||(Mode=="system"&&SystemParameters.ClientAreaAnimation);
    internal static int ActiveClocks=>Jobs.Count;
    internal static void RefreshFeedback(Control control){if(control.IsLoaded)Paint(control,true);}
    public static readonly DependencyProperty FeedbackProperty=DependencyProperty.RegisterAttached("Feedback",typeof(string),typeof(UiMotion),new PropertyMetadata(null,FeedbackChanged));
    public static string? GetFeedback(DependencyObject value)=>(string?)value.GetValue(FeedbackProperty);
    public static void SetFeedback(DependencyObject value,string kind)=>value.SetValue(FeedbackProperty,kind);

    internal static void Configure(string mode)
    {
        if(mode is not ("system" or "on" or "off"))throw new ArgumentException("无效动画设置。");
        Mode=mode;
        if(!_subscribed){SystemParameters.StaticPropertyChanged+=SystemChanged;_subscribed=true;}
        CancelAll();
        foreach(var pair in FeedbackStates)if(pair.Key.IsLoaded)Paint(pair.Key,false);
    }
    private static void SystemChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName==nameof(SystemParameters.ClientAreaAnimation))Application.Current?.Dispatcher.BeginInvoke(()=>Configure(Mode));
    }
    private static void Stop(DependencyObject target,DependencyProperty property)
    {
        if(!Jobs.Remove((target,property),out var job))return;
        job.Clock.Completed-=job.Completed;
        ((IAnimatable)target).ApplyAnimationClock(property,null);
    }
    private static void Animate(DependencyObject target,DependencyProperty property,AnimationTimeline animation,Action? completed=null)
    {
        Stop(target,property);
        var clock=animation.CreateClock();
        EventHandler? done=null;
        done=(_,_)=>{if(Jobs.TryGetValue((target,property),out var current)&&ReferenceEquals(current.Clock,clock)){Stop(target,property);completed?.Invoke();}};
        clock.Completed+=done;
        Jobs[(target,property)]=new(clock,done);
        ((IAnimatable)target).ApplyAnimationClock(property,clock,HandoffBehavior.SnapshotAndReplace);
    }
    internal static void StopReveal(FrameworkElement target)
    {
        if(!Reveals.Remove(target,out var state))return;
        target.Unloaded-=RevealUnloaded;
        Stop(target,UIElement.OpacityProperty);Stop(state.Offset,TranslateTransform.YProperty);
        target.RenderTransform=state.Original;
    }
    internal static void Reveal(FrameworkElement target,int milliseconds=150,double distance=6)
    {
        bool continuing=Reveals.TryGetValue(target,out var previous);
        double from=continuing?target.Opacity:.45,fromY=continuing?previous!.Offset.Y:distance;
        StopReveal(target);
        if(!Enabled||!target.IsLoaded||!target.IsVisible||Window.GetWindow(target) is Window {WindowState:WindowState.Minimized})return;
        var original=target.RenderTransform;var offset=new TranslateTransform();
        target.RenderTransform=new TransformGroup{Children=new TransformCollection{original,offset}};
        Reveals[target]=new(original,offset);
        target.Unloaded+=RevealUnloaded;
        var ease=new QuadraticEase{EasingMode=EasingMode.EaseOut};
        Animate(offset,TranslateTransform.YProperty,new DoubleAnimation(fromY,0,TimeSpan.FromMilliseconds(milliseconds)){EasingFunction=ease,FillBehavior=FillBehavior.Stop});
        Animate(target,UIElement.OpacityProperty,new DoubleAnimation(from,(double)target.GetAnimationBaseValue(UIElement.OpacityProperty),TimeSpan.FromMilliseconds(milliseconds)){EasingFunction=ease,FillBehavior=FillBehavior.Stop},()=>StopReveal(target));
    }
    internal static void CancelAll()
    {
        foreach(var target in Reveals.Keys.ToArray())StopReveal(target);
        foreach(var key in Jobs.Keys.ToArray())Stop(key.Target,key.Property);
    }
    private static void RevealUnloaded(object sender,RoutedEventArgs e)=>StopReveal((FrameworkElement)sender);
    private static void FeedbackChanged(DependencyObject sender,DependencyPropertyChangedEventArgs e)
    {
        if(sender is not Control control)return;
        if(e.OldValue is not null){control.Loaded-=Loaded;control.Unloaded-=Unloaded;Detach(control);}
        if(e.NewValue is not null){control.Loaded+=Loaded;control.Unloaded+=Unloaded;if(control.IsLoaded)Attach(control);}
    }
    private static void Loaded(object sender,RoutedEventArgs e)=>Attach((Control)sender);
    private static void Unloaded(object sender,RoutedEventArgs e)=>Detach((Control)sender);
    private static void Attach(Control control)
    {
        Detach(control);control.ApplyTemplate();
        string part=GetFeedback(control)=="primary"?"MotionTint":GetFeedback(control)=="button"?"Chrome":"Tile";
        if(control.Template?.FindName(part,control) is not Border border)return;
        var state=new FeedbackState{Surface=border};
        if(GetFeedback(control)!="primary"){state.Brush=new SolidColorBrush(Colors.Transparent);border.Background=state.Brush;}
        FeedbackStates.Add(control,state);
        control.MouseEnter+=InputChanged;control.MouseLeave+=InputChanged;control.PreviewMouseLeftButtonDown+=InputChanged;control.PreviewMouseLeftButtonUp+=InputChanged;
        control.PreviewKeyDown+=InputChanged;control.PreviewKeyUp+=InputChanged;control.IsEnabledChanged+=EnabledChanged;
        if(control is ToggleButton toggle){toggle.Checked+=InputChanged;toggle.Unchecked+=InputChanged;}
        Paint(control,false);
    }
    private static void Detach(Control control)
    {
        control.MouseEnter-=InputChanged;control.MouseLeave-=InputChanged;control.PreviewMouseLeftButtonDown-=InputChanged;control.PreviewMouseLeftButtonUp-=InputChanged;
        control.PreviewKeyDown-=InputChanged;control.PreviewKeyUp-=InputChanged;control.IsEnabledChanged-=EnabledChanged;
        if(control is ToggleButton toggle){toggle.Checked-=InputChanged;toggle.Unchecked-=InputChanged;}
        if(!FeedbackStates.TryGetValue(control,out var state))return;
        state.Revision++;
        if(state.Brush is not null)Stop(state.Brush,SolidColorBrush.ColorProperty);
        if(state.Surface is not null){Stop(state.Surface,UIElement.OpacityProperty);if(state.Brush is not null)state.Surface.ClearValue(Border.BackgroundProperty);}
        FeedbackStates.Remove(control);
    }
    private static void EnabledChanged(object sender,DependencyPropertyChangedEventArgs e)=>Queue((Control)sender);
    private static void InputChanged(object sender,RoutedEventArgs e)
    {
        if(e.RoutedEvent==UIElement.PreviewMouseLeftButtonDownEvent||e.RoutedEvent==Keyboard.PreviewKeyDownEvent)
            foreach(var target in Reveals.Keys.ToArray())if(ReferenceEquals(target,sender)||target.IsAncestorOf((Control)sender))StopReveal(target);
        Queue((Control)sender);
    }
    private static void Queue(Control control)
    {
        if(!FeedbackStates.TryGetValue(control,out var state))return;
        int revision=++state.Revision;
        control.Dispatcher.BeginInvoke(()=>{if(control.IsLoaded&&FeedbackStates.TryGetValue(control,out var current)&&ReferenceEquals(current,state)&&revision==state.Revision)Paint(control,true);},DispatcherPriority.Input);
    }
    private static void Paint(Control control,bool animate)
    {
        if(!FeedbackStates.TryGetValue(control,out var state)||state.Surface is null)return;
        animate&=control.IsVisible&&Window.GetWindow(control) is not Window {WindowState:WindowState.Minimized};
        bool pressed=control is ButtonBase button&&button.IsPressed;
        if(GetFeedback(control)=="primary")
        {
            double to=!control.IsEnabled?0:pressed ? .22 : control.IsMouseOver ? .12 : 0,from=state.Surface.Opacity;
            Stop(state.Surface,UIElement.OpacityProperty);state.Surface.Opacity=to;
            if(animate&&Enabled&&Math.Abs(from-to)>.001)Animate(state.Surface,UIElement.OpacityProperty,new DoubleAnimation(from,to,TimeSpan.FromMilliseconds(90)){FillBehavior=FillBehavior.Stop});
            return;
        }
        bool selected=control is ToggleButton {IsChecked:true};
        var baseColor=GetFeedback(control) is "tile" or "icon"?Colors.Transparent:control.Background is SolidColorBrush background?background.Color:Colors.Transparent;
        Color toColor=selected?Color.FromRgb(221,242,255):!control.IsEnabled?baseColor:pressed?Color.FromRgb(208,233,249):control.IsMouseOver?Color.FromRgb(225,243,255):baseColor;
        var brush=state.Brush!;var fromColor=brush.Color;
        Stop(brush,SolidColorBrush.ColorProperty);brush.Color=toColor;
        if(animate&&Enabled&&fromColor!=toColor)Animate(brush,SolidColorBrush.ColorProperty,new ColorAnimation(fromColor,toColor,TimeSpan.FromMilliseconds(selected?100:90)){FillBehavior=FillBehavior.Stop});
    }
}
