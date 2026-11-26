using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopNook.App;

internal static class FoldThemeVerification
{
    internal static async Task Run(MainWindow main, string root, List<string> checks)
    {
        if (!App.IsSandbox) throw new InvalidOperationException("主题检查仅允许沙箱。");
        var selected = App.Options.ThemeMode;
        var selectedColor = App.Options.ColorTheme;
        var selectedUpdateSource = App.Options.UpdateChannel;
        var originalNotes = main.NotesDraft;
        var settings = new SettingsWindow(main);
        try
        {
            main.FinishPanelMotion();
            main.UpdateLayout();
            Check(main.Title == "折页" && new DesktopNook.Core.NotesState().LeftFraction == .61,
                "折页窗口显示新品牌，新便签默认按 61:39 分栏");
            settings.Show();
            await settings.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var work = TaskbarPlacement.WorkArea(main);
            var windowBounds = new Rect(settings.Left, settings.Top, settings.ActualWidth, settings.ActualHeight);
            Check(work.Contains(windowBounds), "设置窗口首次打开时完整落在工作区");
            var narrowWork = new Rect(-480, 80, 480, 550);
            var nearEdge = new Rect(-630, 450, 220, 160);
            var fitted = SettingsWindow.InitialBounds(nearEdge, narrowWork);
            Check(narrowWork.Contains(fitted) && fitted.Left >= narrowWork.Left + 12 &&
                fitted.Bottom <= narrowWork.Bottom - 12, "设置窗口贴边或在左侧副屏时完整显示");

            var menu = Find<ScrollViewer>(settings).Single(item =>
                AutomationProperties.GetAutomationId(item)=="Settings-Navigation");
            var items = Find<Button>(settings).Where(button =>
                AutomationProperties.GetAutomationId(button).StartsWith("SettingsPage-", StringComparison.Ordinal)).ToArray();
            Check(items.Length == 5 && items.Count(Selector.GetIsSelected) == 1 &&
                items.Single(Selector.GetIsSelected).Background == ThemePalette.Brush("SettingsNavigationSelectedBrush") &&
                items.All(button => button.FontSize >= 14), "设置菜单有五个可辨认的入口和高亮当前页");
            var currentMenu = items.Single(Selector.GetIsSelected);
            Check(ReferenceEquals(FocusManager.GetFocusedElement(settings), currentMenu), "打开设置后焦点位于当前菜单");
            var side = (Border)menu.Parent;
            Check(side.CornerRadius.TopLeft >= 12 && side.Margin.Left > 0 &&
                ReferenceEquals(side.BorderBrush, ThemePalette.Brush("SettingsNavigationBorderBrush")),
                "设置菜单以独立浅色圆角区域和内容区分隔");
            var oldWidth = settings.Width;
            var oldHeight = settings.Height;
            settings.Width = 450;
            settings.Height = 480;
            await settings.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            settings.UpdateLayout();
            Check(menu.ActualWidth >= settings.ActualWidth - 30 && items.All(button =>
            {
                var origin = button.TranslatePoint(new Point(), menu);
                return origin.X >= 0 && origin.Y >= 0 &&
                    origin.X + button.ActualWidth <= menu.ActualWidth + 1 &&
                    origin.Y + button.ActualHeight <= menu.ActualHeight + 1;
            }), "窄窗口把五个菜单移到顶部，首屏可见");
            await Save(settings, "zheye-settings-navigation-compact.png");
            settings.Width = oldWidth;
            settings.Height = oldHeight;
            await settings.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(menu.ActualWidth < settings.ActualWidth / 2, "恢复宽窗口后菜单回到左侧");
            await Save(settings, "zheye-settings-navigation.png");
            foreach (var page in new[] { "常规", "外观", SettingsWindow.TaskbarPage, "数据与恢复", "关于与更新" })
            {
                settings.SelectPage(page);
                settings.UpdateLayout();
                Check(Find<TextBlock>(settings).Any(text => text.Text == page && text.FontSize >= 17),
                    "设置页可打开：" + page);
                Check(AutomationProperties.GetAutomationId(items.Single(Selector.GetIsSelected)) == "SettingsPage-" + page,
                    "设置菜单随页面高亮：" + page);
                Check(items.All(button =>
                {
                    button.ApplyTemplate();
                    return button.Template.FindName("SelectedMark", button) is Border marker &&
                        (marker.Visibility == Visibility.Visible) == Selector.GetIsSelected(button);
                }), "选中细条随菜单切换：" + page);
            }
            var sourceControl=Find<ComboBox>(settings).Single(item =>
                AutomationProperties.GetAutomationId(item)=="Settings-UpdateChannel");
            sourceControl.SelectedIndex=2;
            Check(App.Options.UpdateChannel==UpdateSource.GiteeOnly, "关于与更新可保存仅 Gitee");
            sourceControl.SelectedIndex=1;
            Check(App.Options.UpdateChannel==UpdateSource.GitHubOnly, "关于与更新可保存仅 GitHub");
            sourceControl.SelectedIndex=0;
            Check(App.Options.UpdateChannel==UpdateSource.Auto, "关于与更新可恢复 GitHub 优先的自动模式");
            settings.SelectPage("外观");
            settings.UpdateLayout();

            var sample = main.NotesStore.Draft();
            var project = DesktopNook.Core.NotesStore.AddProject(sample, "工作安排");
            var now = DateTime.UtcNow;
            sample.Notes.AddRange(
            [
                new() { ProjectId = project.Id, Title = "本周待办", Kind = "todo", CreatedUtc = now,
                    Tasks = [new() { Text = "整理项目资料" }, new() { Text = "确认会议时间", Done = true }] },
                new() { ProjectId = project.Id, Title = "灵感随记", Kind = "text", CreatedUtc = now,
                    Text = "把常用的工具和临时想法整理在一起。" },
                new() { ProjectId = project.Id, Title = "下次讨论", Kind = "text", CreatedUtc = now,
                    Text = "核对进度，安排接下来的工作。" }
            ]);
            main.NotesDraft = sample;
            main.RenderNotes();
            main.UpdateLayout();
            var task = Find<CheckBox>(main.NotesHost).First(item => item.IsChecked == true);
            Check(task.Template.FindName("Mark", task) is Border mark &&
                (mark.Background as SolidColorBrush)?.Color == ThemePalette.Brush("FoldMintBrush").Color,
                "双页布局里的完成待办使用主题薄荷绿");

            var savedRatio = main.NotesDraft.LeftFraction;
            var boundBrush = ThemePalette.Brush("PrimaryBrush");
            var previews = new HashSet<ImageSource>();
            foreach (var family in FoldTheme.All)
            {
                Select("Settings-Color-" + family.Id);
                previews.Add((ImageSource)Application.Current.FindResource("FoldMark"));
                foreach (var mode in new[] { "light", "dark" })
                {
                    Select("Settings-Theme-" + mode);
                    var expected = mode == "dark" ? family.Dark : family.Light;
                    await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    Check(ThemePalette.Family.Id == family.Id && ThemePalette.Mode == mode &&
                        ReferenceEquals(boundBrush, ThemePalette.Brush("PrimaryBrush")) &&
                        (SystemParameters.HighContrast ||
                         boundBrush.Color == (Color)ColorConverter.ConvertFromString(expected.Accent)),
                        family.Name + "在" + (mode == "dark" ? "深色" : "浅色") + "模式实时更新颜色");
                    var selectedMenu = items.Single(Selector.GetIsSelected);
                    if (mode == "light" && !SystemParameters.HighContrast)
                    {
                        var navigation = ((SolidColorBrush)side.Background).Color;
                        var page = ((SolidColorBrush)((Border)settings.Content).Background).Color;
                        Check(Luminance(navigation) >= .75 && Contrast(navigation, page) >= 1.07,
                            family.Name + "的浅色设置菜单使用可辨认的主题浅底");
                    }
                    Check(SystemParameters.HighContrast ||
                        (selectedMenu.Background is SolidColorBrush background && selectedMenu.Foreground is SolidColorBrush foreground &&
                         Contrast(background.Color, foreground.Color) >= 4.5), family.Name + "的设置菜单文字与选中底色清晰区分");
                    Check(SystemParameters.HighContrast || (side.Background is SolidColorBrush navBackground &&
                        items.Where(button => !Selector.GetIsSelected(button)).All(button =>
                            button.Background == Brushes.Transparent && button.Foreground is SolidColorBrush itemText &&
                            Contrast(navBackground.Color, itemText.Color) >= 4.5)), family.Name + "的其他设置菜单入口清晰可读");
                    await Save(main, $"zheye-{family.Id}-{mode}.png");
                }
            }
            Check(previews.Count == FoldTheme.All.Count && main.NotesDraft.LeftFraction == savedRatio,
                "各套主题分别使用对应图标，切换保留已保存的分栏");
            Select("Settings-Color-lime");
            Select("Settings-Theme-dark");
            await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(ThemePalette.IsDark && (main.Surface.Background as SolidColorBrush)?.Color ==
                ThemePalette.Brush("CanvasBrush").Color, "深色设置即时更新主面板的画布画刷");
            Check(((Border)settings.Content).Background is SolidColorBrush settingsBackground &&
                settingsBackground.Color == ThemePalette.Brush("SurfaceBrush").Color &&
                Find<TextBlock>(main.NotesHost).Any(text => text.Text == "便签与待办" &&
                    (text.Foreground as SolidColorBrush)?.Color == ThemePalette.Brush("TextPrimaryBrush").Color),
                "深色同步更新代码生成的设置背景和便签文字");
            var reopened = new StartupOptions(preferencesPath: Path.Combine(App.Store.DataDirectory, "preferences.json"), fileOnly: true);
            Check(reopened.ThemeMode == "dark" && reopened.ColorTheme == "lime" && reopened.MotionMode == App.Options.MotionMode &&
                reopened.CloseEndsProcess == App.Options.CloseEndsProcess, "主题保存与开合动画及关闭偏好相互独立");
            await Save(settings, "zheye-settings-dark.png");
            Select("Settings-Theme-system");
            Check(ThemePalette.Mode == "system" && reopened.ThemeMode == "system", "跟随系统选项可保存并重新读取");
            Select("Settings-Color-grape");
            Select("Settings-Theme-light");
            await Save(settings, "zheye-settings-light.png");
        }
        finally
        {
            settings.Close();
            main.NotesDraft = originalNotes;
            main.RenderNotes();
            App.Options.ThemeMode = selected;
            App.Options.ColorTheme = selectedColor;
            App.Options.UpdateChannel = selectedUpdateSource;
            ThemePalette.Apply(selected, selectedColor);
            main.PanelMotion.InvalidateSnapshot();
        }

        void Select(string automationId)
        {
            settings.UpdateLayout();
            var button = Find<Button>(settings).Single(item =>
                AutomationProperties.GetAutomationId(item) == automationId);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        void Check(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException(message);
            checks.Add(message);
        }
        async Task Save(Window source, string name)
        {
            await Task.Delay(250);
            source.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(source.ActualWidth),
                (int)Math.Ceiling(source.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(source);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(root, name));
            encoder.Save(output);
        }
    }

    private static double Luminance(Color color) => .2126 * color.ScR + .7152 * color.ScG + .0722 * color.ScB;

    private static double Contrast(Color a, Color b)
    {
        var first = Luminance(a);
        var second = Luminance(b);
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }

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
