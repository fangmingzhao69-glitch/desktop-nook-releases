using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopNook.App;

internal static class GlassMaterialVerification
{
    internal static FrameworkElement ReferenceBackdrop()
    {
        var scene = new Canvas { Width = 1280, Height = 840,
            Background = new LinearGradientBrush(new GradientStopCollection { new(Color.FromRgb(178,201,226),0),
                new(Color.FromRgb(224,211,218),.5), new(Color.FromRgb(224,173,153),1) }, new Point(0,0),new Point(1,1)) };
        scene.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,440 C270,90 340,590 650,380 C940,130 1140,340 1280,190 L1280,840 H0 Z"),
            Fill = new SolidColorBrush(Color.FromArgb(135,87,125,158)) });
        scene.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,670 C300,320 530,770 790,530 C1070,320 1160,640 1280,400 L1280,840 H0 Z"),
            Fill = new SolidColorBrush(Color.FromArgb(125,79,115,116)) });
        return new Viewbox { Stretch = Stretch.Fill, Child = scene };
    }

    internal static async Task CheckSurface(MainWindow main, Window board, string root, Action<bool,string> check)
    {
        main.FinishPanelMotion(); main.KeepOpenForVerification = true;
        board.Content = new Border { Background = new LinearGradientBrush(Colors.SlateBlue, Colors.DarkSalmon, 35) };
        GroupPreviewVerification.PlaceBehind(board, main); main.Activate();
        for (int i = 0; i < 25 && main.GlassMaterial?.HasFrame != true; i++) await Task.Delay(100);
        check(main.GlassMaterial is { HasFrame: true, Running: true }, "连续玻璃使用实际后方背景，独立于前景内容绘制");
        board.Content = new Border { Background = Brushes.CadetBlue };
        await Task.Delay(1300);
        int before = main.GlassMaterial!.UpdatedFrames;
        board.Content = new Border { Background = Brushes.SandyBrown };
        for (int i = 0; i < 20 && main.GlassMaterial.UpdatedFrames == before; i++) await Task.Delay(100);
        check(main.GlassMaterial.UpdatedFrames > before, "后方背景改变时玻璃材质会更新");
        await Task.Delay(1000);
        before = main.GlassMaterial.UpdatedFrames;
        main.SetStatus("独立沙箱：前景变化不进入背景采样");
        await Task.Delay(1000);
        check(main.GlassMaterial.UpdatedFrames == before, "前景自身被排除，状态文字更新不造成背景反馈");
        check(GetWindowDisplayAffinity(new WindowInteropHelper(main).Handle, out uint affinity) && affinity == 0,
            "普通截图和屏幕共享继续包含折页窗口");
        main.TogglePanel(); await Task.Delay(700);
        check(main.WindowState == WindowState.Minimized && main.GlassMaterial is { Running: false, BufferBytes: 0, CaptureSuspended: true } &&
            main.GlassBackdropImage.Source is null, "收起时停止采样并释放持有的背景缓冲");
        main.ShowPanel(); await Task.Delay(800); main.FinishPanelMotion();
        check(main.GlassMaterial is { HasFrame: true, Running: true }, "重新展开时恢复玻璃背景");
        ThemePalette.Apply(ThemePalette.Mode, "grape");
        check(main.GlassMaterial is { Running: false, BufferBytes: 0, CaptureSuspended: true }, "切换其他主题时停止采样并清空背景像素");
        ThemePalette.Apply(ThemePalette.Mode, "glass");
        await Task.Delay(900);
        check(main.GlassMaterial is { HasFrame: true, CaptureInstances: 1 }, "重新展开及切回玻璃主题复用同一原生采集器");
        main.GlassMaterial!.ResetDisplay(); await Task.Delay(1000);
        check(main.GlassMaterial is { HasFrame: true, CaptureInstances: 1 }, "显示区域刷新保留原生采集器并恢复有效背景");
        board.Content = ReferenceBackdrop(); await Task.Delay(1100);
        GroupPreviewVerification.Snapshot(main, Path.Combine(root, "glass-material-final.png"));
    }
    internal static async Task RunCapture(string root)
    {
        Directory.CreateDirectory(root);
        string? failure = null;
        using var capture = new GlassBackdropCapture();
        var board = new Window { Width = 520, Height = 340, Left = 100, Top = 100, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, Topmost = true, Background = new SolidColorBrush(Color.FromRgb(35, 140, 215)) };
        var cover = new Window { Width = 400, Height = 240, Left = 150, Top = 150, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, Topmost = true, Background = Brushes.Red };
        try
        {
            board.Show(); cover.Show(); cover.Activate(); await Task.Delay(250);
            var rect = new GlassBackdropCapture.NativeRect { Left = 170, Top = 170, Right = 530, Bottom = 350 };
            var image = capture.Capture(rect, [new WindowInteropHelper(cover).Handle]) ?? throw new Exception(capture.Status);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var file = File.Create(Path.Combine(root, "filtered-background.png"))) encoder.Save(file);
            GroupPreviewVerification.Snapshot(board, Path.Combine(root, "visible-test-board.png"));
            var unfiltered = capture.Capture(rect, []);
            if (unfiltered is not null)
            {
                var second = new PngBitmapEncoder(); second.Frames.Add(BitmapFrame.Create(unfiltered));
                using var secondFile = File.Create(Path.Combine(root, "unfiltered-background.png")); second.Save(secondFile);
            }
            var pixel = new byte[4]; image.CopyPixels(new Int32Rect(100, 70, 1, 1), pixel, 4, 0);
            if (Math.Abs(pixel[0] - 215) > 4 || Math.Abs(pixel[1] - 140) > 4 || Math.Abs(pixel[2] - 35) > 4)
                throw new Exception("过滤或颜色通道不正确：" + string.Join(',', pixel));
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            cover.Close(); board.Close();
            File.WriteAllText(Path.Combine(root, "glass-capture-verification.json"), JsonSerializer.Serialize(new { success = failure is null,
                capture.Status, capture.Format, failure }, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Shutdown(failure is null ? 0 : 1);
        }
    }
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
}
