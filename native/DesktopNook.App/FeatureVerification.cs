using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopNook.Core;

namespace DesktopNook.App;

internal static class FeatureVerification
{
    internal static async Task Run(MainWindow main, string root)
    {
        var checks = new List<string>();
        string? failure = null;
        Window? other = null;
        try
        {
            if (!App.IsSandbox) throw new InvalidOperationException("仅允许隔离验证。");
            main.KeepOpenForVerification = true;
            main.FinishPanelMotion();
            var pin = main.PinButton;
            main.UpdateLayout();
            Snapshot(main, "pin-unpinned");
            pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            main.UpdateLayout();
            Snapshot(main, "pin-pinned");
            Check(App.Options.PanelPinned, "顶栏图钉启用保持展开");
            App.Options.VisualStyle = "crystal";
            App.Options.ThemeMode = "dark";
            App.Options.WriteWindowSize(App.Options.ReadWindowSize());
            var saved = new StartupOptions(preferencesPath: Path.Combine(App.Store.DataDirectory, "preferences.json"), fileOnly: true);
            Check(saved.PanelPinned && saved.VisualStyle == "crystal" && saved.ThemeMode == "dark", "主题和尺寸修改、重新读取均保留钉住偏好");
            main.KeepOpenForVerification = false;
            other = new Window { Title = "折页隔离焦点验收", Width = 180, Height = 90, Topmost = true };
            other.Show();
            other.Activate();
            await Task.Delay(700);
            Check(!main.IsActive && main.WindowState == WindowState.Normal, "切换至其他窗口后钉住面板仍展开");
            other.Close(); other = null;
            main.KeepOpenForVerification = true;
            main.ShowPanel();
            main.TogglePanel();
            await Task.Delay(650);
            Check(main.WindowState == WindowState.Minimized, "钉住时仍能主动收起面板");
            main.ShowPanel();
            pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!App.Options.PanelPinned && AutomationProperties.GetName(pin) == "钉住面板", "取消钉住恢复原操作状态");

            var category = NotesStore.AddProject(main.NotesDraft, "功能验收");
            var note = new ProjectNote { ProjectId = category.Id, Title = "便签文字格式", Kind = "text", Text = "常用与灵感，折页之间。\n选中文字可以加粗和标注。" };
            main.NotesDraft.Notes.Add(note);
            main.NotesStore.Commit(main.NotesDraft);
            main.RenderNotes();
            await Layout();
            var editor = Find<NoteTextEditor>(main.NotesHost).Single();
            editor.Focus(); editor.SelectCharacters(0, 5);
            var bold = Find<Button>(main.NotesHost).Single(button => AutomationProperties.GetAutomationId(button).EndsWith("-Bold", StringComparison.Ordinal));
            bold.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(note.BodyBolds?.SequenceEqual([new NoteBoldRange(0, 5)]) == true && editor.SelectionIsBold(), "通过标题栏为选中文字加粗");
            editor.Highlight("#BBEEDF");
            editor.ToggleStrike();
            Check(main.FlushNotes(false), "加粗、标注、删除线同时保存");
            var reloaded = new NotesStore(App.Store.DataDirectory).State.Notes.Single();
            Check(reloaded.BodyBolds?.SequenceEqual(note.BodyBolds!) == true && reloaded.BodyHighlights?.Length == 1 && reloaded.BodyStrikes?.Length == 1,
                "重新读取保留三种重叠格式");
            main.RenderNotes(); await Layout();
            editor = Find<NoteTextEditor>(main.NotesHost).Single();
            editor.SelectCharacters(0, 5);
            Check(editor.SelectionIsBold() && editor.ReadHighlights()?.Length == 1 && editor.SelectionIsStruck(), "重绘便签恢复三种格式");
            editor.SelectCharacters(0, 0); editor.Selection.Text = "新";
            Check(editor.ReadBolds()?.Any(range => range.Start <= 1 && range.Start + range.Length >= 6) == true, "在格式文字前输入后加粗随文字移动");
            editor.Undo();
            editor.SelectCharacters(0, 5);
            bold = Find<Button>(main.NotesHost).Single(button => AutomationProperties.GetAutomationId(button).EndsWith("-Bold", StringComparison.Ordinal));
            bold.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(editor.ReadBolds() is null, "同一按钮可以取消加粗");
            editor.ToggleBold();
            main.FlushNotes(false);
            Check(NotesMarkdown.Export(main.NotesDraft, note).Contains("<strong>常用与灵感</strong>"), "Markdown 导出保留加粗");

            var backdrop = new Grid();
            backdrop.ColumnDefinitions.Add(new()); backdrop.ColumnDefinitions.Add(new());
            backdrop.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(35, 140, 215)) });
            var warm = new Border { Background = new SolidColorBrush(Color.FromRgb(240, 140, 65)) }; Grid.SetColumn(warm, 1); backdrop.Children.Add(warm);
            other = new Window { Title = "玻璃隔离背景", WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
                Left = main.Left, Top = main.Top, Width = main.ActualWidth, Height = main.ActualHeight, Content = backdrop };
            other.Show(); GroupPreviewVerification.PlaceBehind(other, main); main.Activate();
            foreach (var mode in new[] { "light", "dark" })
            {
                ThemePalette.Apply(mode);
                foreach (var style in PanelVisualStyle.Choices)
                {
                    PanelVisualStyle.Select(style.Id);
                    main.UpdateLayout();
                    Check(main.Surface.Background is SolidColorBrush && main.GlassSheen.IsHitTestVisible == false,
                        mode + " / " + style.Name + " 保留可用表面和输入");
                }
                PanelVisualStyle.Select("crystal");
                main.FinishPanelMotion();
                main.Activate();
                await Layout();
                await Task.Delay(200);
                Snapshot(main, "glass-" + mode);
                pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                main.UpdateLayout();
                Snapshot(main, "pin-pinned-" + mode);
                pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                GroupPreviewVerification.Snapshot(main, Path.Combine(root, "glass-" + mode + "-native.png"));
            }
            other.Close(); other = null;
            var session = new TaskbarAppearanceSession(new TaskbarAppearanceStore(Path.Combine(root, "appearance")), new QuietBackend());
            var settings = new Window { Width = 540, Height = 620, Title = "任务栏设置验收", Owner = main };
            try
            {
                var page = new TaskbarAppearancePage(settings, session);
                settings.Content = page;
                UiDialogs.Chrome(settings);
                settings.Show(); await Task.Delay(250); await Layout(); settings.UpdateLayout();
                var advanced = Find<Expander>(settings).Single();
                Check(!advanced.IsExpanded && Find<Button>(settings).Any(button => AutomationProperties.GetAutomationId(button) == "AppearanceApply"), "任务栏高级选项默认折叠且应用入口可见");
                Snapshot(settings, "taskbar-simple");
                advanced.IsExpanded = true; settings.UpdateLayout();
                Check(Find<ComboBox>(settings).Any(control => AutomationProperties.GetAutomationId(control) == "AppearanceScene"), "高级设置保留场景配置");
            }
            finally { settings.Close(); }
            UiMotion.Configure("off");
            Exception? colorFailure = null;
            _ = main.Dispatcher.BeginInvoke(() =>
            {
                var picker = Application.Current.Windows.OfType<Window>().Single(window => window.Title == "选择任务栏颜色");
                try
                {
                    picker.UpdateLayout();
                    var standard = Find<Button>(picker).Where(button => button.Content is Border).ToList();
                    Check(standard.Count == 30 && standard.All(button => ((Border)button.Content).ActualWidth >= 24 &&
                        ((Border)button.Content).ActualHeight >= 16), "30 个标准色色块完整可见");
                    standard.Single(button => AutomationProperties.GetName(button) == "暖灰 #B9ADA1")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    picker.UpdateLayout();
                    Snapshot(picker, "standard-colors");
                    picker.DialogResult = true;
                }
                catch (Exception error) { colorFailure = error; picker.DialogResult = false; }
            }, DispatcherPriority.ContextIdle);
            var selectedColor = TaskbarColorPicker.Choose(main, "#152D42");
            if (colorFailure is not null) throw colorFailure;
            Check(selectedColor == "#B9ADA1", "点击标准色后确认返回准确颜色值");
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            other?.Close();
            File.WriteAllText(Path.Combine(root, "features-verification.json"), JsonSerializer.Serialize(new { success = failure is null, passed = checks.Count, checks, failure }, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Shutdown(failure is null ? 0 : 1);
        }
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); }
        async Task Layout() => await main.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ContextIdle);
        void Snapshot(Window window, string name)
        {
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(root, name + ".png")); encoder.Save(file);
        }
    }
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var item in Find<T>(child)) yield return item;
        }
    }
    private sealed class QuietBackend : ITaskbarAppearanceBackend
    {
        public string? Restriction => null;
        public bool NeedsDownload => false;
        public bool Running => false;
        public Task ApplyAsync(TaskbarAppearanceSettings settings, bool allowDownload) => Task.CompletedTask;
        public bool Stop() => true;
    }
}
