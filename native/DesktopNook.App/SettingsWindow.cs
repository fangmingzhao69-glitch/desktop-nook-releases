using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopNook.App;

internal sealed class SettingsWindow : Window
{
    private static readonly Size PreferredSize = new(820, 540);
    private const double SidebarWidth = 226;
    private const double CompactWidth = 610;
    private const double CompactHeight = 370;
    private const double WorkAreaInset = 12;
    internal const string TaskbarPage = "任务栏外观设置";
    internal enum ActionAfterClose { None, RefreshEntry, PinHelp, StopGame, Quit, Restore, OpenData, CheckUpdate, LocalUpdate, Releases, License, GameLicense }
    private readonly MainWindow _owner;
    private readonly StackPanel _content = new();
    private readonly ScrollViewer _scroller;
    private readonly ContentControl _taskbarContent = new()
    {
        Visibility = Visibility.Collapsed,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch
    };
    private readonly TaskbarAppearanceSession? _taskbarSession;
    private TaskbarAppearancePage? _taskbarPage;
    private bool _taskbarSelected;
    private bool _leavingTaskbar;
    private bool _closeReady;
    private readonly Dictionary<string, Button> _navigation = new();
    private readonly StartupOptions _options = App.Options;
    internal ActionAfterClose NextAction { get; private set; }

    internal SettingsWindow(MainWindow owner, TaskbarAppearanceSession? taskbarSession = null)
    {
        _owner = owner;
        _taskbarSession = taskbarSession;
        Owner = owner;
        Title = "设置";
        Width = PreferredSize.Width;
        Height = PreferredSize.Height;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontSize = 13;

        var root = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        var sidebarColumn = new ColumnDefinition { Width = new(SidebarWidth) };
        root.ColumnDefinitions.Add(sidebarColumn);
        root.ColumnDefinitions.Add(new());
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new());
        var sidebar = new StackPanel { Margin = new Thickness(12, 12, 10, 12) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 9) };
        var mark = new Image { Width = 24, Height = 24, Margin = new Thickness(0, 0, 8, 0) };
        mark.SetResourceReference(Image.SourceProperty, "FoldMark");
        brand.Children.Add(mark);
        var brandName = Text("设置菜单", 19, "SettingsNavigationTextBrush");
        brandName.FontWeight = FontWeights.SemiBold;
        brand.Children.Add(brandName);
        sidebar.Children.Add(brand);
        var navigation = new WrapPanel();
        var groupLabels = new List<TextBlock>();
        sidebar.Children.Add(navigation);
        AddNavigationLabel("偏好设置");
        AddNavigation("常规", "M2,4 H14 M2,8 H14 M2,12 H14 M6,2 V6 M10,6 V10 M5,10 V14");
        AddNavigation("外观", "M3,3 H13 V13 H3 Z M6,3 V13 M6,9 L11,3 M7,13 L13,7");
        AddNavigation(TaskbarPage, "M2,3 H14 V13 H2 Z M2,10 H14 M6,11.5 H10");
        AddNavigationLabel("支持");
        AddNavigation("数据与恢复", "M2,5 H7 L8,6 H14 V13 H2 Z M2,5 V3 H7 L8,5");
        AddNavigation("关于与更新", "M8,2 A6,6 0 1 1 8,14 A6,6 0 1 1 8,2 M8,7 V11 M8,5 V5.1");
        var navigationScroll = new ScrollViewer
        {
            Content = sidebar, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        AutomationProperties.SetAutomationId(navigationScroll, "Settings-Navigation");
        AutomationProperties.SetName(navigationScroll, "设置菜单");
        var navigationSurface = new Border
        {
            Background = ThemePalette.Brush("SettingsNavigationBrush"), BorderBrush = ThemePalette.Brush("SettingsNavigationBorderBrush"),
            Child = navigationScroll
        };
        root.Children.Add(navigationSurface);
        _content.Margin = new Thickness(24, 8, 24, 20);
        _scroller = new ScrollViewer { Content = _content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(_scroller, 1);
        Grid.SetColumn(_taskbarContent, 1);
        Grid.SetRow(_scroller, 1);
        Grid.SetRow(_taskbarContent, 1);
        root.Children.Add(_scroller);
        root.Children.Add(_taskbarContent);
        Content = root;
        UiDialogs.Chrome(this);
        SelectPage("常规");
        Loaded += (_, _) =>
        {
            var current = _navigation.Values.FirstOrDefault(Selector.GetIsSelected);
            if (current is not null) Keyboard.Focus(current);
        };
        Closing += OnClosing;
        Closed += (_, _) => _taskbarPage?.StopPreviewTimer();
        bool? compact = null;
        root.SizeChanged += (_, args) => LayOutNavigation(args.NewSize);
        SourceInitialized += (_, _) =>
        {
            var work = TaskbarPlacement.WorkArea(owner);
            var origin = new Point(double.IsFinite(owner.Left) ? owner.Left : work.Left,
                double.IsFinite(owner.Top) ? owner.Top : work.Top);
            var bounds = InitialBounds(new Rect(origin, new Size(owner.ActualWidth, owner.ActualHeight)), work);
            MaxWidth = Math.Max(1, work.Width - WorkAreaInset * 2);
            MaxHeight = Math.Max(1, work.Height - WorkAreaInset * 2);
            Width = bounds.Width;
            Height = bounds.Height;
            Left = bounds.Left;
            Top = bounds.Top;
            LayOutNavigation(bounds.Size);
        };

        void LayOutNavigation(Size available)
        {
            bool top = available.Width < CompactWidth || available.Height < CompactHeight;
            if (compact != top)
            {
                compact = top;
                sidebarColumn.Width = new GridLength(top ? 0 : SidebarWidth);
                Grid.SetRow(navigationSurface, top ? 0 : 1);
                Grid.SetColumnSpan(navigationSurface, top ? 2 : 1);
                navigationSurface.BorderThickness = top ? new Thickness(0, 0, 0, 1) : new Thickness(0, 0, 1, 0);
                navigationSurface.CornerRadius = top ? new CornerRadius(0) : new CornerRadius(0, 0, 0, 13);
                navigation.ItemWidth = top ? double.NaN : SidebarWidth - 29;
                foreach (var label in groupLabels) label.Visibility = top ? Visibility.Collapsed : Visibility.Visible;
                _content.Margin = top ? new Thickness(14, 12, 14, 16) : new Thickness(24, 8, 24, 20);
            }
            navigationScroll.MaxHeight = top
                ? Math.Min(available.Height * .55, Math.Max(155, available.Height * .42))
                : double.PositiveInfinity;
        }

        void AddNavigation(string page, string geometry)
        {
            var button = new Button
            {
                Style = (Style)FindResource("SettingsNavigationButton"),
                Margin = new Thickness(0, 0, 6, 5)
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var glyph = DesignGlyphs.Path(geometry);
            glyph.Margin = new Thickness(0, 0, 8, 0);
            glyph.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new Binding("Foreground") { Source = button });
            content.Children.Add(glyph);
            content.Children.Add(new TextBlock { Text = page, VerticalAlignment = VerticalAlignment.Center });
            button.Content = content;
            AutomationProperties.SetAutomationId(button, "SettingsPage-" + page);
            AutomationProperties.SetName(button, page);
            button.Click += async (_, _) =>
            {
                if (page == TaskbarPage || await LeaveTaskbarAsync()) SelectPage(page);
            };
            navigation.Children.Add(button);
            _navigation.Add(page, button);
        }
        void AddNavigationLabel(string label)
        {
            var text = Text(label, 12, "SettingsNavigationMutedBrush");
            text.FontWeight = FontWeights.SemiBold;
            text.Margin = new Thickness(4, 8, 0, 5);
            groupLabels.Add(text);
            navigation.Children.Add(text);
        }
    }

    internal static Rect InitialBounds(Rect owner, Rect work)
    {
        var width = Math.Min(PreferredSize.Width, Math.Max(1, work.Width - WorkAreaInset * 2));
        var height = Math.Min(PreferredSize.Height, Math.Max(1, work.Height - WorkAreaInset * 2));
        var horizontalInset = Math.Min(WorkAreaInset, Math.Max(0, (work.Width - width) / 2));
        var verticalInset = Math.Min(WorkAreaInset, Math.Max(0, (work.Height - height) / 2));
        var left = Math.Clamp(owner.X + (owner.Width - width) / 2,
            work.Left + horizontalInset, Math.Max(work.Left + horizontalInset, work.Right - horizontalInset - width));
        var top = Math.Clamp(owner.Y + (owner.Height - height) / 2,
            work.Top + verticalInset, Math.Max(work.Top + verticalInset, work.Bottom - verticalInset - height));
        return new Rect(left, top, width, height);
    }

    internal void SelectPage(string page)
    {
        foreach (var (name, button) in _navigation)
        {
            Selector.SetIsSelected(button, name == page);
            AutomationProperties.SetItemStatus(button, name == page ? "当前页" : "");
        }
        _taskbarSelected = page == TaskbarPage;
        bool showTaskbar = _taskbarSelected && !_owner.StartingTaskbarAppearance;
        _taskbarContent.Visibility = showTaskbar ? Visibility.Visible : Visibility.Collapsed;
        _scroller.Visibility = showTaskbar ? Visibility.Collapsed : Visibility.Visible;
        if (showTaskbar)
        {
            _taskbarContent.Content ??= _taskbarPage = new TaskbarAppearancePage(this, _taskbarSession ?? _owner.TaskbarAppearance());
            return;
        }

        _scroller.ScrollToTop();
        _content.Children.Clear();
        var heading = Text(page, 17);
        heading.FontWeight = FontWeights.SemiBold;
        heading.Margin = new Thickness(0, 0, 0, 16);
        _content.Children.Add(heading);

        switch (page)
        {
            case "常规":
                Toggle("开机自启", _options.Enabled, enabled => _options.SetEnabled(enabled), !App.IsSandbox);
                Toggle("关闭时结束进程", _options.CloseEndsProcess, enabled => _options.CloseEndsProcess = enabled);
                Pick("添加图标默认视图", null, _options.DefaultCollectView,
                    [("icons", "图标"), ("details", "详细列表")], value => _options.DefaultCollectView = value);
                ButtonRow("创建或刷新程序入口", () => ContinueWith(ActionAfterClose.RefreshEntry), enabled: !App.IsSandbox);
                ButtonRow("任务栏固定与名称修复", () => ContinueWith(ActionAfterClose.PinHelp));
                if (_owner.BilliardsRunning) ButtonRow("退出台球", () => ContinueWith(ActionAfterClose.StopGame));
                ButtonRow("退出并结束进程", () => ContinueWith(ActionAfterClose.Quit));
                break;
            case "外观":
                ColorThemeCards();
                ThemeCards();
                var motionHeading = Text("面板与动效", 14);
                motionHeading.FontWeight = FontWeights.SemiBold;
                motionHeading.Margin = new Thickness(0, 18, 0, 10);
                _content.Children.Add(motionHeading);
                Pick("开合样式", "Settings-PanelStyle", _options.PanelStyle,
                    PanelEffectFrame.Choices.Select(choice => (choice.Effect, choice.Label)).ToArray(), _owner.SelectPanelEffect);
                Pick("界面反馈动画", "Settings-Motion", _options.MotionMode,
                    [("system", "跟随系统"), ("on", "开启"), ("off", "关闭")], value =>
                    {
                        _options.MotionMode = value;
                        UiMotion.Configure(value);
                    });
                var sizes = WindowSizePreference.Presets.Select(value => (value, $"{value * 100:0}%")).Append((0d, "自由调整")).ToArray();
                Pick("窗口尺寸", null, _owner.SizePreference.Free ? 0 : _owner.SizePreference.Zoom, sizes, value =>
                {
                    _owner.SelectWindowSize(value > 0 ? value : null);
                    SelectPage("外观");
                });
                Toggle("锁定宽高比", _owner.SizePreference.Locked, value => _owner.SelectWindowSize(locked: value), _owner.SizePreference.Free);
                ButtonRow("使用当前窗口尺寸", () => { _owner.SelectWindowSize(); SelectPage("外观"); });
                ButtonRow("恢复默认尺寸", () => { _owner.SelectWindowSize(reset: true); SelectPage("外观"); });
                break;
            case TaskbarPage:
                Info("正在启动已保存的任务栏外观。");
                ButtonRow("重新载入", () => SelectPage(TaskbarPage));
                break;
            case "数据与恢复":
                Info(App.Store.DataDirectory);
                Info("备份时复制整个数据目录。恢复收纳内容前会要求确认。");
                ButtonRow("打开数据目录", () => ContinueWith(ActionAfterClose.OpenData));
                ButtonRow("全部移回桌面", () => ContinueWith(ActionAfterClose.Restore), enabled: App.Store.State.Items.Count > 0,
                    foreground: "DangerBrush");
                break;
            case "关于与更新":
                Info($"折页  {MainWindow.CurrentVersionLabel}");
                Info("常用与灵感，折页之间。");
                Pick("在线更新来源", "Settings-UpdateChannel", _options.UpdateChannel,
                    [(UpdateSource.Auto, "自动（GitHub 优先）"), (UpdateSource.GitHubOnly, "仅 GitHub"), (UpdateSource.GiteeOnly, "仅 Gitee")],
                    value =>
                    {
                        if (value == _options.UpdateChannel) return;
                        _options.UpdateChannel = value;
                        _owner.UpdateChannelChanged();
                    });
                Info("自动模式在 GitHub 访问失败时切换到 Gitee。");
                ButtonRow("检查在线更新", () => ContinueWith(ActionAfterClose.CheckUpdate), primary: true);
                ButtonRow("从本地文件更新", () => ContinueWith(ActionAfterClose.LocalUpdate));
                ButtonRow("打开官方发布页", () => ContinueWith(ActionAfterClose.Releases));
                ButtonRow("开源许可与源码", () => ContinueWith(ActionAfterClose.License));
                ButtonRow("图标台球许可", () => ContinueWith(ActionAfterClose.GameLicense));
                break;
        }
    }

    private async Task<bool> LeaveTaskbarAsync()
    {
        if (_leavingTaskbar) return false;
        if (!_taskbarSelected || _taskbarPage is not { NeedsExitCheck: true } page) return true;
        _leavingTaskbar = true;
        try { return await page.TryLeaveAsync(); }
        finally { _leavingTaskbar = false; }
    }

    private async void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_closeReady || !_taskbarSelected || _taskbarPage is not { NeedsExitCheck: true }) return;
        args.Cancel = true;
        if (!await LeaveTaskbarAsync()) return;
        _closeReady = true;
        await Dispatcher.InvokeAsync(Close);
    }

    private void ColorThemeCards()
    {
        var heading = Text("配色", 14);
        heading.FontWeight = FontWeights.SemiBold;
        heading.Margin = new Thickness(0, 0, 0, 8);
        _content.Children.Add(heading);
        var options = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var theme in FoldTheme.All)
        {
            bool current = _options.ColorTheme == theme.Id;
            var stack = new StackPanel { Orientation = Orientation.Horizontal };
            stack.Children.Add(new Image { Source = FoldTheme.Mark(theme), Width = 25, Height = 25, Margin = new Thickness(0, 0, 7, 0) });
            stack.Children.Add(Text(theme.Name, 12));
            var button = new Button
            {
                Content = stack, MinWidth = 106, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(7),
                Background = ThemePalette.Brush(current ? "SelectionBrush" : "SurfaceSubtleBrush")
            };
            AutomationProperties.SetName(button, theme.Name);
            AutomationProperties.SetAutomationId(button, "Settings-Color-" + theme.Id);
            if (current) AutomationProperties.SetItemStatus(button, "当前配色");
            button.Click += (_, _) => Save(() =>
            {
                bool changed = _options.ColorTheme != theme.Id;
                _options.ColorTheme = theme.Id;
                _owner.FinishPanelMotion();
                ThemePalette.Apply(_options.ThemeMode, theme.Id);
                if (changed) _owner.ApplyShellTheme(refreshPins: true);
                _owner.PanelMotion.InvalidateSnapshot();
                SelectPage("外观");
                _content.Children.OfType<WrapPanel>().First().Children.OfType<Button>()
                    .Single(item => AutomationProperties.GetAutomationId(item) == "Settings-Color-" + theme.Id).Focus();
            });
            options.Children.Add(button);
        }
        _content.Children.Add(options);
    }

    private void ThemeCards()
    {
        var label = Text("明暗模式", 14);
        label.FontWeight = FontWeights.SemiBold;
        label.Margin = new Thickness(0, 0, 0, 8);
        _content.Children.Add(label);
        var cards = new Grid();
        foreach (var (mode, caption, canvas, surface) in new[]
        {
            ("light", "浅色", ThemePalette.Family.Light.Canvas, ThemePalette.Family.Light.Accent),
            ("dark", "深色", ThemePalette.Family.Dark.Canvas, ThemePalette.Family.Dark.Accent),
            ("system", "跟随系统", ThemePalette.Family.Light.Canvas, ThemePalette.Family.Dark.Accent)
        })
        {
            int column = cards.ColumnDefinitions.Count;
            cards.ColumnDefinitions.Add(new());
            var preview = new Grid { Height = 28 };
            preview.ColumnDefinitions.Add(new() { Width = new GridLength(2, GridUnitType.Star) });
            preview.ColumnDefinitions.Add(new());
            preview.Children.Add(new Border { Background = SampleBrush(canvas), CornerRadius = new CornerRadius(4,0,0,4) });
            var accent = new Border { Background = SampleBrush(surface), CornerRadius = new CornerRadius(0,4,4,0) };
            Grid.SetColumn(accent, 1);
            preview.Children.Add(accent);
            var content = new StackPanel { MinWidth = 90 };
            content.Children.Add(preview);
            content.Children.Add(new TextBlock { Text = caption, FontSize = 12, Margin = new Thickness(0,6,0,0) });
            var button = new Button { Content = content, Margin = new Thickness(0,0,8,0),
                Padding = new Thickness(8), Background = ThemePalette.Brush(_options.ThemeMode == mode ? "SelectionBrush" : "SurfaceSubtleBrush") };
            AutomationProperties.SetAutomationId(button, "Settings-Theme-" + mode);
            AutomationProperties.SetName(button, caption);
            if (_options.ThemeMode == mode) AutomationProperties.SetItemStatus(button, "当前主题");
            button.Click += (_, _) => Save(() =>
            {
                _options.ThemeMode = mode;
                _owner.FinishPanelMotion();
                ThemePalette.Apply(mode);
                _owner.PanelMotion.InvalidateSnapshot();
                SelectPage("外观");
                FindNavigationFocus(mode);
            });
            Grid.SetColumn(button, column);
            cards.Children.Add(button);
        }
        _content.Children.Add(cards);

        void FindNavigationFocus(string mode)
        {
            if (_content.Children.OfType<Grid>().FirstOrDefault() is not { } options) return;
            options.Children.OfType<Button>().FirstOrDefault(button =>
                AutomationProperties.GetAutomationId(button) == "Settings-Theme-" + mode)?.Focus();
        }

        static SolidColorBrush SampleBrush(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }
    }

    private void ContinueWith(ActionAfterClose action)
    {
        NextAction = action;
        DialogResult = true;
    }

    private static TextBlock Text(string value, int size = 13, string color = "TextPrimaryBrush") => new()
    {
        Text = value, FontSize = size, Foreground = ThemePalette.Brush(color),
        VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap
    };

    private void Info(string value)
    {
        var text = Text(value, 13, "TextSecondaryBrush");
        text.Margin = new Thickness(0, 0, 0, 14);
        _content.Children.Add(text);
    }

    private void ButtonRow(string title, Action change, bool primary = false, bool enabled = true, string? foreground = null)
    {
        var button = new Button { Content = title, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 8), MinHeight = 34, IsEnabled = enabled };
        if (primary) button.Style = (Style)FindResource("PrimaryButton");
        if (foreground is not null) button.Foreground = ThemePalette.Brush(foreground);
        button.Click += (_, _) => Save(change);
        _content.Children.Add(button);
    }

    private void Toggle(string title, bool value, Action<bool> change, bool enabled = true)
    {
        var control = new CheckBox { Content = title, IsChecked = value, IsEnabled = enabled, MinHeight = 34,
            Margin = new Thickness(0, 0, 0, 6) };
        control.Click += (_, _) => Save(() => change(control.IsChecked == true));
        _content.Children.Add(control);
    }

    private void Pick<T>(string title, string? id, T value, (T Value, string Label)[] options, Action<T> change)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var control = new ComboBox { Width = 176, MinHeight = 34, ItemsSource = options.Select(item => item.Label) };
        if (id is not null) AutomationProperties.SetAutomationId(control, id);
        AutomationProperties.SetName(control, title);
        control.SelectedIndex = Array.FindIndex(options, item => EqualityComparer<T>.Default.Equals(item.Value, value));
        control.SelectionChanged += (_, _) =>
        {
            if (control.SelectedIndex >= 0) Save(() => change(options[control.SelectedIndex].Value));
        };
        DockPanel.SetDock(control, Dock.Right);
        row.Children.Add(control);
        row.Children.Add(Text(title));
        _content.Children.Add(row);
    }

    private void Save(Action change)
    {
        try { change(); }
        catch (Exception error) { UiDialogs.Show(this, error.Message, "设置"); }
    }
}
