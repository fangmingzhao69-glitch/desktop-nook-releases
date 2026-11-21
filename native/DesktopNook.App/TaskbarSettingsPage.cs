using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopNook.Core;

namespace DesktopNook.App;

internal sealed class TaskbarSettingsPage : UserControl
{
    private readonly Window _owner;
    private readonly FloatingTaskbarSession _session;
    private readonly TaskbarAppearancePage _system;
    private readonly StackPanel _fields = new();
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 8) };
    private readonly FloatingTaskbarPreview _preview = new() { Height = 142, Margin = new(0, 8, 0, 14) };
    private readonly ComboBox _mode = new() { MinWidth = 180 };
    private readonly Button _try = new() { Content = "试用 12 秒", MinHeight = 34 };
    private readonly Button _save = new() { Content = "保存设置", MinHeight = 34, Margin = new(8, 0, 0, 0) };
    private readonly Button _cancel = new() { Content = "撤销", MinHeight = 34, Margin = new(8, 0, 0, 0) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private FloatingTaskbarSettings _draft;
    private FloatingTaskbarSettings? _trialSettings;
    private bool _busy, _loading, _trial;
    private int _seconds;
    internal bool NeedsExitCheck => _busy || _session.HasPreview || _system.NeedsExitCheck || _draft != _session.Saved;
    internal FloatingTaskbarSettings Draft => _draft;

    internal TaskbarSettingsPage(Window owner, MainWindow main, TaskbarAppearanceSession systemSession)
    {
        _owner = owner; _session = main.FloatingTaskbar; _draft = _session.Saved;
        _system = new TaskbarAppearancePage(owner, systemSession);
        var root = new DockPanel { Margin = new(22, 6, 22, 12) };
        var footer = new StackPanel();
        _message.Foreground = ThemePalette.Brush("TextSecondaryBrush");
        footer.Children.Add(_message);
        var actions = new WrapPanel();
        actions.Children.Add(_try); actions.Children.Add(_save); actions.Children.Add(_cancel);
        _save.Style = (Style)FindResource("PrimaryButton");
        footer.Children.Add(actions); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = SettingsWindow.TaskbarPage, FontSize = 17, FontWeight = FontWeights.SemiBold });
        _mode.Items.Add(new ComboBoxItem { Content = "Windows 系统任务栏", Tag = "system" });
        _mode.Items.Add(new ComboBoxItem { Content = "悬浮任务栏（替换 Windows 任务栏）", Tag = "floating" });
        _mode.SelectedIndex = _draft.Mode == "floating" ? 1 : 0;
        AutomationProperties.SetAutomationId(_mode, "Settings-TaskbarMode");
        body.Children.Add(Row("使用方式", _mode));
        body.Children.Add(_preview);
        body.Children.Add(_fields);
        body.Children.Add(_system);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = root;
        _mode.SelectionChanged += async (_, _) =>
        {
            if (_loading || _busy) return;
            string next = (_mode.SelectedItem as ComboBoxItem)?.Tag as string ?? "system";
            if (next == _draft.Mode) return;
            if (_system.NeedsExitCheck && !await _system.TryLeaveAsync())
            {
                _loading = true; _mode.SelectedIndex = _draft.Mode == "floating" ? 1 : 0; _loading = false; return;
            }
            if (_trial) await RunAsync(async () => { await _session.CancelPreviewAsync(); EndTrial(); });
            _draft = _draft with { Mode = next };
            RenderFields();
        };
        _try.Click += async (_, _) => await RunAsync(PreviewAsync);
        _save.Click += async (_, _) => await RunAsync(async () =>
        {
            bool requiresTrial = _draft.Mode != _session.Saved.Mode;
            if (requiresTrial && (!_trial || _trialSettings != _draft)) { await PreviewAsync(); return; }
            await _session.ApplyAsync(_draft, save: true);
            EndTrial(); RenderFields(); _message.Text = "任务栏设置已保存。";
        });
        _cancel.Click += async (_, _) => await RunAsync(DiscardAsync);
        _timer.Tick += async (_, _) =>
        {
            if (_busy) return;
            if (--_seconds > 0) { _save.Content = $"保留设置（{_seconds} 秒）"; return; }
            _timer.Stop();
            await RunAsync(async () =>
            {
                await _session.CancelPreviewAsync(); EndTrial(); RenderFields();
                _message.Text = "试用已结束，已恢复上次保存的任务栏模式。";
            });
        };
        RenderFields();
    }
    private async Task PreviewAsync()
    {
        await _session.ApplyAsync(_draft, save: false);
        _trialSettings = _draft;
        _trial = true; _seconds = 12; _timer.Start();
        _save.Content = "保留设置（12 秒）";
        _message.Text = "正在试用，点击「保留设置」保存；超时自动恢复。";
        _system.IsEnabled = _draft.Mode == "system";
    }
    private void EndTrial() { _timer.Stop(); _trial = false; _trialSettings = null; _save.Content = "保存设置"; }
    private async Task RunAsync(Func<Task> operation)
    {
        if (_busy) return;
        _busy = true; _mode.IsEnabled = _fields.IsEnabled = _try.IsEnabled = _save.IsEnabled = _cancel.IsEnabled = false;
        _message.Text = "正在应用任务栏设置…";
        try { await operation(); }
        catch (Exception ex) { _message.Text = ex.Message; }
        finally { _busy = false; _mode.IsEnabled = _fields.IsEnabled = _try.IsEnabled = _save.IsEnabled = _cancel.IsEnabled = true; }
    }
    private void RenderFields()
    {
        _fields.Children.Clear();
        bool floating = _draft.Mode == "floating";
        _system.Visibility = floating ? Visibility.Collapsed : Visibility.Visible;
        _system.IsEnabled = !floating && _session.Current.Mode == "system";
        _preview.Settings = _draft; _preview.InvalidateVisual();
        if (!floating)
        {
            _message.Text = _session.Current.Mode == "floating" ? "点击「试用 12 秒」切回系统任务栏，再调整其外观。" : "系统任务栏的外观与悬浮任务栏分别保存。";
            return;
        }
        _message.Text = App.IsSandbox ? "隔离预览保留系统任务栏。" : "试用期间，屏幕底部只显示悬浮任务栏。退出折页后恢复 Windows 任务栏。";
        Pick("材质", "Dock-Material", _draft.Material, [("liquid", "液态玻璃"), ("frosted", "柔雾玻璃"), ("solid", "纯色")], value =>
        { _draft = _draft with { Material = value }; RenderFields(); });
        Section("形状与布局");
        Slider("任务栏高度", "Dock-Height", 48, 88, _draft.Height, "DIP", value => _draft = _draft with { Height = value, IconSize = Math.Min(_draft.IconSize, value - 8) });
        Slider("图标大小", "Dock-IconSize", 24, Math.Min(56, _draft.Height - 8), _draft.IconSize, "DIP", value => _draft = _draft with { IconSize = value });
        Slider("底边距离", "Dock-BottomGap", 0, 48, _draft.BottomGap, "DIP", value => _draft = _draft with { BottomGap = value });
        Toggle("宽度随内容调整", "Dock-AutoWidth", _draft.AutoWidth, value => _draft = _draft with { AutoWidth = value });
        Slider("最大宽度", "Dock-Width", 320, 4000, _draft.MaximumWidth, "DIP", value => _draft = _draft with { MaximumWidth = value });
        Section("玻璃与动效");
        if (_draft.Material != "solid")
        {
            Slider("透明度", "Dock-Transparency", 0, 100, _draft.Transparency * 100, "%", value => _draft = _draft with { Transparency = value / 100 });
            Slider("模糊强度", "Dock-Blur", 0, 100, _draft.Blur * 100, "%", value => _draft = _draft with { Blur = value / 100 });
            if (_draft.Material == "liquid") Slider("折射强度", "Dock-Refraction", 0, 60, _draft.Refraction, "", value => _draft = _draft with { Refraction = value });
            Slider("边缘高光", "Dock-Highlight", 0, 100, _draft.Highlight * 100, "%", value => _draft = _draft with { Highlight = value / 100 });
        }
        var tint = new TextBox { Text = _draft.Tint, Width = 110, HorizontalAlignment = HorizontalAlignment.Left, Padding = new(7) };
        AutomationProperties.SetAutomationId(tint, "Dock-Tint");
        tint.TextChanged += (_, _) => { _draft = _draft with { Tint = tint.Text.Trim() }; RefreshPreview(); };
        var tintRow = new StackPanel { Orientation = Orientation.Horizontal }; tintRow.Children.Add(tint);
        var themeTint = new Button { Content = "使用主题色", Margin = new(8, 0, 0, 0) };
        themeTint.Click += (_, _) => { var c = ThemePalette.Brush("CanvasBrush").Color; tint.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}"; };
        tintRow.Children.Add(themeTint); _fields.Children.Add(Row("材质染色", tintRow));
        Slider("悬停放大", "Dock-Magnify", 100, 200, _draft.Magnification * 100, "%", value => _draft = _draft with { Magnification = value / 100 });
        Toggle("玻璃随悬停轻微隆起", "Dock-HoverRise", _draft.HoverRise, value => _draft = _draft with { HoverRise = value });
        Section("显示与状态");
        Pick("显示方式", "Dock-AutoHide", _draft.AutoHide, [("never", "常驻"), ("fullscreen", "最大化与全屏时避让"), ("always", "自动隐藏")], value => _draft = _draft with { AutoHide = value });
        Pick("显示器", "Dock-Displays", _draft.DisplayMode, [("primary", "主显示器"), ("follow", "随指针跨屏唤出"), ("all", "所有显示器"), ("fixed", "指定显示器")], value => { _draft = _draft with { DisplayMode = value }; RenderFields(); });
        if (_draft.DisplayMode == "fixed")
        {
            var list = new ComboBox { DisplayMemberPath = "Name", SelectedValuePath = "Id" };
            foreach (var screen in DockScreens.List()) list.Items.Add(screen);
            list.SelectedValue = _draft.Display;
            list.SelectionChanged += (_, _) => { if (list.SelectedValue is string id) _draft = _draft with { Display = id }; RefreshPreview(); };
            _fields.Children.Add(Row("目标显示器", list));
        }
        Toggle("显示系统托盘", "Dock-Tray", _draft.ShowTray, value => _draft = _draft with { ShowTray = value });
        Toggle("显示网络音量电量", "Dock-Status", _draft.ShowStatus, value => _draft = _draft with { ShowStatus = value });
        Toggle("显示时间与日期", "Dock-Clock", _draft.ShowClock, value => _draft = _draft with { ShowClock = value });
        Toggle("显示通知入口", "Dock-Notifications", _draft.ShowNotifications, value => _draft = _draft with { ShowNotifications = value });
        Toggle("远程控制兼容（映出壁纸）", "Dock-Remote", _draft.RemoteCompatible, value => _draft = _draft with { RemoteCompatible = value });
        var reset = new Button { Content = "恢复悬浮任务栏默认参数", HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 6, 0, 8) };
        reset.Click += (_, _) => { _draft = new() { Mode = "floating" }; RenderFields(); };
        _fields.Children.Add(reset);
    }
    private void RefreshPreview() { _preview.Settings = _draft; _preview.InvalidateVisual(); }
    private void Section(string name) => _fields.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new(0, 18, 0, 7) });
    private static Grid Row(string label, UIElement field)
    {
        var row = new Grid { Margin = new(0, 5, 0, 5), MinHeight = 32 };
        row.ColumnDefinitions.Add(new() { Width = new(115) }); row.ColumnDefinitions.Add(new());
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemePalette.Brush("TextSecondaryBrush") };
        row.Children.Add(text); Grid.SetColumn(field, 1); row.Children.Add(field);
        if (field is DependencyObject obj) AutomationProperties.SetLabeledBy(obj, text);
        return row;
    }
    private void Toggle(string label, string id, bool value, Action<bool> changed)
    {
        var check = new CheckBox { Content = label, IsChecked = value, Margin = new(0, 6, 0, 6) };
        AutomationProperties.SetAutomationId(check, id);
        check.Click += (_, _) => { changed(check.IsChecked == true); RefreshPreview(); };
        _fields.Children.Add(check);
    }
    private void Pick(string label, string id, string selected, (string Id, string Name)[] choices, Action<string> changed)
    {
        var list = new ComboBox();
        foreach (var choice in choices) list.Items.Add(new ComboBoxItem { Content = choice.Name, Tag = choice.Id });
        list.SelectedItem = list.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, selected));
        AutomationProperties.SetAutomationId(list, id);
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is ComboBoxItem { Tag: string value }) { changed(value); RefreshPreview(); } };
        _fields.Children.Add(Row(label, list));
    }
    private void Slider(string label, string id, double min, double max, double value, string unit, Action<double> changed)
    {
        var row = new DockPanel();
        var number = new TextBlock { Text = $"{value:0}{unit}", MinWidth = 55, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0) };
        DockPanel.SetDock(number, Dock.Right); row.Children.Add(number);
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)FindResource("TaskbarAppearanceSlider") };
        AutomationProperties.SetAutomationId(slider, id);
        slider.ValueChanged += (_, _) =>
        {
            changed(slider.Value); number.Text = $"{slider.Value:0}{unit}";
            if (id == "Dock-Height")
                foreach (var icon in _fields.Children.OfType<Grid>().SelectMany(grid => grid.Children.OfType<DockPanel>())
                    .SelectMany(panel => panel.Children.OfType<Slider>()).Where(control => AutomationProperties.GetAutomationId(control) == "Dock-IconSize"))
                    icon.Maximum = Math.Min(56, _draft.Height - 8);
            RefreshPreview();
        };
        row.Children.Add(slider); _fields.Children.Add(Row(label, row));
    }
    internal async Task<bool> TryLeaveAsync()
    {
        if (_busy) return false;
        if (!await _system.TryLeaveAsync()) return false;
        try { await _session.CancelPreviewAsync(); EndTrial(); }
        catch (Exception ex) { _message.Text = ex.Message; return false; }
        if (_draft == _session.Saved) return true;
        if (UiDialogs.Show(_owner, "任务栏参数尚未保存。放弃本次修改？", "任务栏设置", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return false;
        await DiscardAsync();
        return true;
    }
    internal void StopPreviewTimer() { _timer.Stop(); _system.StopPreviewTimer(); }
    internal async Task DiscardAsync()
    {
        await _session.CancelPreviewAsync(); EndTrial(); _draft = _session.Saved;
        _loading = true; _mode.SelectedIndex = _draft.Mode == "floating" ? 1 : 0; _loading = false;
        RenderFields(); _message.Text = "已恢复上次保存的设置。";
    }
}
