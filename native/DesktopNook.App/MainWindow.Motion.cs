using System.Windows;
using System.Windows.Interop;

namespace DesktopNook.App;

public partial class MainWindow
{
    private ShutterMotion _shutter=null!;
    private bool _hidePending,_committingHide;
    private int _hideRevision;
    internal ShutterMotion PanelMotion=>_shutter;
    private void InitializePanelMotion()
    {
        _shutter=new(Surface,ShutterOverlay);
        SourceInitialized+=(_,_)=>HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(MotionWindowMessage);
        UiMotion.ModeChanged+=OnMotionModeChanged;
        Closed+=(_,_)=>{UiMotion.ModeChanged-=OnMotionModeChanged;_shutter.Stop();};
        PreviewMouseDown+=(_,e)=>{if(_shutter.Active){FinishPanelMotion();e.Handled=true;}};
    }
    private IntPtr MotionWindowMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if(message==0x112&&(wParam.ToInt64()&0xfff0)==0xf020&&!_committingHide&&!_allowExit&&UiMotion.Enabled)
        {handled=true;TogglePanel();}
        return IntPtr.Zero;
    }
    internal void FinishPanelMotion(){_hidePending=false;_hideRevision++;_shutter.Stop();}
    private void OpenPanelMotion(){_hidePending=false;_hideRevision++;_shutter.Play(true);}
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
    }
}
