using System.Windows;
using System.Windows.Interop;

namespace DesktopNook.App;

public partial class MainWindow
{
    private ShutterMotion _shutter=null!;
    private bool _hidePending,_committingHide;
    private int _hideRevision;
    private readonly System.Windows.Threading.DispatcherTimer _idleRelease=new(){Interval=TimeSpan.FromSeconds(5)};
    private HwndSource? _suspendedSource;
    private System.Windows.Media.Visual? _suspendedRoot;
    private RenderMode _awakeRenderMode;
    internal bool ContentParked=>_suspendedSource is not null;
    private void EnsurePanelContent()
    {
        if(_suspendedSource is null)return;
        var source=_suspendedSource;var root=_suspendedRoot;_suspendedSource=null;_suspendedRoot=null;
        Opacity=0; // Mask native reentrant paints until the shutter is ready.
        if(source.CompositionTarget is HwndTarget target)target.RenderMode=_awakeRenderMode;
        source.RootVisual=root;_shutter.InvalidateSnapshot();
    }
    internal ShutterMotion PanelMotion=>_shutter;
    private void InitializePanelMotion()
    {
        _shutter=new(Surface,ShutterOverlay);
        IconsScroll.ScrollChanged+=(_,e)=>{if(e.VerticalChange!=0||e.HorizontalChange!=0)_shutter.InvalidateSnapshot();};
        Surface.MouseEnter+=(_,_)=>{if(!_shutter.Active&&!_hidePending&&WindowState==WindowState.Normal)_shutter.InvalidateSnapshot();};
        Surface.MouseLeave+=(_,_)=>{if(!_shutter.Active&&!_hidePending&&WindowState==WindowState.Normal)_shutter.InvalidateSnapshot();};
        PreviewKeyDown+=(_,_)=>_shutter.InvalidateSnapshot();
        SourceInitialized+=(_,_)=>HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(MotionWindowMessage);
        UiMotion.ModeChanged+=OnMotionModeChanged;
        _idleRelease.Tick+=(_,_)=>ReleaseIdleResources();
        Closed+=(_,_)=>{_idleRelease.Stop();UiMotion.ModeChanged-=OnMotionModeChanged;_shutter.Stop();_shutter.ReleaseBuffer();};
        PreviewMouseDown+=(_,e)=>{if(_shutter.Active){FinishPanelMotion();e.Handled=true;}};
    }
    private IntPtr MotionWindowMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if(message is 0x7e or 0x2e0 or 0x1a)TaskbarPlacement.Invalidate();
        if(message==0x112&&(wParam.ToInt64()&0xfff0)==0xf020&&!_committingHide&&!_allowExit&&UiMotion.Enabled)
        {handled=true;TogglePanel();}
        return IntPtr.Zero;
    }
    internal void FinishPanelMotion(){_hidePending=false;_hideRevision++;_shutter.Stop();}
    private void OpenPanelMotion()
    {
        _idleRelease.Stop();EnsurePanelContent();UpdateSizingLayout();UpdateLayout();
        foreach(var icon in FindVisualChildren<ShellIcon>(Icons)){icon.Resume();icon.PrimeForReveal(this);}
        _hidePending=false;_hideRevision++;_shutter.Play(true);
        if(_shutter.Active||!UiMotion.Enabled||_shutter.Failure is not null||(Surface.IsLoaded&&Surface.IsVisible))Opacity=1;
    }
    private void ScheduleIdleRelease()
    {
        foreach(var icon in FindVisualChildren<ShellIcon>(Icons))icon.Pause();
        _idleRelease.Stop();_idleRelease.Start();
    }
    internal void ReleaseIdleResources()
    {
        if(WindowState!=WindowState.Minimized){_idleRelease.Stop();return;}
        if(_dragging||_interactions>0||_menus>0||_onlineBusy)return;
        _idleRelease.Stop();_shutter.ReleaseBuffer();ShellIntegration.TrimIdleIcons();
        if(LastMenu?.IsOpen!=true)LastMenu=null;
        // Detach the root from the native render target, retaining logical state
        // and the HWND used by the taskbar. No working-set manipulation.
        if(_suspendedSource is null&&HwndSource.FromHwnd(new WindowInteropHelper(this).Handle) is HwndSource source&&source.RootVisual is not null)
        {_suspendedSource=source;_suspendedRoot=source.RootVisual;source.RootVisual=null;if(source.CompositionTarget is HwndTarget target){_awakeRenderMode=target.RenderMode;target.RenderMode=RenderMode.SoftwareOnly;}}
    }
    internal void BeginHidePanel()
    {
        if(_hidePending||WindowState==WindowState.Minimized||_dragging||_interactions>0)return;
        UiMotion.CancelAll();_hidePending=true;int revision=++_hideRevision;
        _shutter.Play(false,()=>{if(_hidePending&&revision==_hideRevision)CommitPanelHide();});
    }
    private void CommitPanelHide()
    {
        _hidePending=false;_hideRevision++;_shutter.Stop();_committingHide=true;
        try{WindowState=WindowState.Minimized;}finally{_committingHide=false;}
    }
    private void OnMotionModeChanged()
    {
        if(UiMotion.Enabled)return;
        if(_hidePending)CommitPanelHide();else FinishPanelMotion();
        _shutter.ReleaseBuffer();
    }
}
