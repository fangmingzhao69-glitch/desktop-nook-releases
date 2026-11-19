using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopNook.App;

internal static class PanelEffectVerification
{
    internal static async Task Run(MainWindow owner, string root, Action<bool, string> check)
    {
        var previous = owner.PanelMotion.Style;
        var previousPreference = App.Options.PanelStyle;
        try
        {
            await VerifyMenu();
            foreach (var style in PanelEffectFrame.Choices.Where(style => style.Effect != PanelEffect.Shutters))
            {
                owner.PanelMotion.Style = style.Effect;
                owner.BeginHidePanel();
                var closing = await Intermediate(style.Effect);
                check(owner.WindowState == WindowState.Normal && owner.PanelMotion.Closing &&
                    closing.CapturePoses().Any(pose => pose.Opacity > .1 && Moving(pose)),
                    $"{style.Label}收起期间窗口可见，并且有中间位移、裁剪或缩放");
                await Settled(WindowState.Minimized);

                owner.ShowPanel();
                var opening = await Intermediate(style.Effect);
                var poses = opening.CapturePoses();
                int parts = style.Effect switch
                {
                    PanelEffect.Relay or PanelEffect.Split => 3,
                    PanelEffect.Scatter or PanelEffect.Wave or PanelEffect.Radial => 2,
                    _ => 1
                };
                check(opening.PartCount >= parts && poses.Any(pose => pose.Opacity > .1 && Moving(pose)) &&
                    owner.PanelMotion.SnapshotBytes <= 8 * 1024 * 1024 && owner.PanelMotion.ActiveClocks == 1,
                    $"{style.Label}展开使用实际面板区域和一份有界截图");
                Capture(Path.Combine(root, $"panel-effect-{style.Effect}.png"));
                await Settled(WindowState.Normal);
                check(owner.PanelMotion.Failure is null && owner.PanelMotion.ActiveClocks == 0 &&
                    owner.PanelMotion.SnapshotBytes == 0 && owner.Surface.Opacity == 1,
                    $"{style.Label}开合结束后清除活动图层并恢复内容");
            }

            owner.PanelMotion.Style = PanelEffect.Split;
            owner.BeginHidePanel();
            var closingFrame = await Intermediate(PanelEffect.Split);
            var before = closingFrame.CapturePoses();
            owner.ShowPanel();
            var reversed = owner.PanelMotion.ActiveEffect?.CapturePoses();
            check(reversed is not null && before.SequenceEqual(reversed), "快速反向从当前画面继续");
            await Settled(WindowState.Normal);
            owner.BeginHidePanel();
            await Intermediate(PanelEffect.Split);
            UiMotion.Configure("off");
            check(owner.WindowState == WindowState.Minimized && !owner.PanelMotion.Active, "关闭动画立即结束正在播放的新效果");
            owner.ShowPanel();
            check(!owner.PanelMotion.Active && owner.PanelMotion.SnapshotBytes == 0, "关闭动画时恢复面板保持直接显示");
            UiMotion.Configure("on");
        }
        finally
        {
            owner.FinishPanelMotion();
            owner.PanelMotion.Style = previous;
            App.Options.PanelStyle = previousPreference;
        }

        async Task VerifyMenu()
        {
            var more = Find<Button>(owner.NotesHost)
                .Single(button => AutomationProperties.GetName(button) == "更多操作");
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var menu = owner.LastMenu ?? throw new InvalidOperationException("没有打开更多菜单。");
            var motion = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "动画效果"));
            var styles = motion.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "开合样式"));
            var entries = styles.Items.OfType<MenuItem>().ToArray();
            check(entries.Select(item => item.Header as string).SequenceEqual(PanelEffectFrame.Choices.Select(style => style.Label)),
                "开合样式菜单包括两份演示的十种效果和百叶翻转");
            menu.IsOpen = false;
            entries.Single(item => Equals(item.Header, "胶囊生长")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            App.Options.PersistForUpdate();
            var reopened = new StartupOptions(preferencesPath: Path.Combine(App.Store.DataDirectory, "preferences.json"));
            check(owner.PanelMotion.Style == PanelEffect.Capsule && reopened.PanelStyle == PanelEffect.Capsule,
                "菜单选择立即生效，重读偏好仍为所选样式");
        }

        async Task<PanelEffectFrame> Intermediate(PanelEffect effect)
        {
            for (int i = 0; i < 25; i++)
            {
                await Task.Delay(16);
                var frame = owner.PanelMotion.ActiveEffect;
                if (frame is { Progress: >= .28 and <= .78 } && frame.Preset == effect)
                    return frame;
            }
            throw new InvalidOperationException($"未采到 {effect} 的中间帧。{owner.PanelMotion.Failure}");
        }

        async Task Settled(WindowState state)
        {
            for (int i = 0; i < 45; i++)
            {
                if (owner.WindowState == state && !owner.PanelMotion.Active) return;
                await Task.Delay(25);
            }
            throw new InvalidOperationException("窗口开合没有结束。");
        }

        void Capture(string path)
        {
            owner.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)owner.ActualWidth, (int)owner.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(owner);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(path);
            encoder.Save(file);
        }
    }

    private static bool Moving(PanelEffectFrame.Pose pose) =>
        Math.Abs(pose.X) > 1 || Math.Abs(pose.Y) > 1 || Math.Abs(pose.Scale - 1) > .01 ||
        pose.InsetX > .01 || pose.InsetTop > .01 || pose.InsetBottom > .01;

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T item) yield return item;
            foreach (var nested in Find<T>(child)) yield return nested;
        }
    }
}
