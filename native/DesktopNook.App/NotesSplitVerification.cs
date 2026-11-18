using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopNook.App;

internal static class NotesSplitVerification
{
    [StructLayout(LayoutKind.Sequential)] private struct Pointer { public int X,Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Pointer point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);

    internal static async Task DragNote(MainWindow window, Thumb handle, Border target, bool cancel = false)
    {
        if (!App.IsSandbox) throw new InvalidOperationException("便签拖动检查仅允许沙箱。");
        GetCursorPos(out var original);
        bool pressed = false;
        try
        {
            window.Activate();
            handle.BringIntoView();
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
            var from = handle.PointToScreen(new Point(handle.ActualWidth / 2, handle.ActualHeight / 2));
            var to = target.PointToScreen(new Point(24, target.ActualHeight / 4));
            SetCursorPos((int)from.X, (int)from.Y);
            mouse_event(2, 0, 0, 0, UIntPtr.Zero);
            pressed = true;
            await Task.Delay(80);
            if (!handle.IsDragging) throw new Exception("真实鼠标未捕获序号拖动手柄。");
            SetCursorPos((int)to.X, (int)to.Y);
            await Task.Delay(100);
            if (cancel)
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            mouse_event(4, 0, 0, 0, UIntPtr.Zero);
            pressed = false;
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
        }
        finally
        {
            if (pressed) mouse_event(4, 0, 0, 0, UIntPtr.Zero);
            SetCursorPos(original.X, original.Y);
        }
    }
    internal static async Task Run(MainWindow window,List<string> checks)
    {
        if(!App.IsSandbox)throw new InvalidOperationException("分栏检查仅允许沙箱。");
        void Check(bool value,string name){if(!value)throw new Exception(name);checks.Add(name);}
        async Task Layout(){window.UpdateLayout();await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);window.UpdateLayout();}
        GetCursorPos(out var original);bool pressed=false;
        try
        {
            window.KeepOpenForVerification=true;window.Activate();window.PanelMotion.Stop();window.Opacity=1;await Layout();
            var dpi=VisualTreeHelper.GetDpi(window);var start=window.NotesSplit.PointToScreen(new Point(2,window.NotesSplit.ActualHeight/2));
            double before=window.LauncherColumn.ActualWidth;
            UiMotion.Reveal(window.Icons,5000,10);
            SetCursorPos((int)start.X,(int)start.Y);mouse_event(2,0,0,0,UIntPtr.Zero);pressed=true;await Task.Delay(80);
            Check(window.NotesSplit.IsDragging,"真实鼠标捕获分隔线开始拖动");
            for(int step=1;step<=8;step++)
            {
                SetCursorPos((int)(start.X-220*dpi.DpiScaleX*step/8),(int)start.Y);await Task.Delay(35);await Layout();
                Check(!window.PanelMotion.Active&&window.Surface.Opacity>.99&&window.Icons.Opacity>.99,$"分栏拖动第 {step} 帧两侧保持可见，无入场或百叶残影");
            }
            double moving=window.LauncherColumn.ActualWidth;
            Check(moving<before-100&&window.IconColumns<6,"拖动中即时减少图标列数，不等松手才重排");
            window.RenderNotes();await Layout();
            Check(Math.Abs(window.LauncherColumn.ActualWidth-moving)<2,"拖动时刷新便签不按旧比例拉回分隔线");
            mouse_event(4,0,0,0,UIntPtr.Zero);pressed=false;await Layout();
            Check(Math.Abs(window.LauncherColumn.ActualWidth-moving)<2&&window.FlushNotes(false),"松手保持当前位置并保存比例");
            var saved=window.NotesDraft.LeftFraction;
            Check(Math.Abs(new DesktopNook.Core.NotesStore(App.Store.DataDirectory).State.LeftFraction-saved)<.001,"分栏比例重读一致");
            window.BeginHidePanel();await Task.Delay(650);window.ShowPanel();await Task.Delay(650);await Layout();
            Check(Math.Abs(window.LauncherColumn.ActualWidth-moving)<2&&window.PanelMotion.Failure is null,"重新展开使用新分栏尺寸，动画无捕获异常");
            start=window.NotesSplit.PointToScreen(new Point(2,window.NotesSplit.ActualHeight/2));
            SetCursorPos((int)start.X,(int)start.Y);mouse_event(2,0,0,0,UIntPtr.Zero);pressed=true;await Task.Delay(50);
            SetCursorPos((int)(start.X+100*dpi.DpiScaleX),(int)start.Y);await Task.Delay(80);
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent});mouse_event(4,0,0,0,UIntPtr.Zero);pressed=false;await Layout();
            Check(Math.Abs(window.NotesDraft.LeftFraction-saved)<.001&&Math.Abs(window.LauncherColumn.ActualWidth-moving)<2,"Esc 取消拖动恢复上次比例及两侧布局");
        }
        finally
        {
            if(pressed)mouse_event(4,0,0,0,UIntPtr.Zero);
            SetCursorPos(original.X,original.Y);
        }
    }
}
