using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using DesktopNook.Core;

namespace DesktopNook.App;

internal sealed class TaskbarAppearanceWindow : Window
{
    private readonly TaskbarAppearanceSession _session;
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromSeconds(12) };
    private readonly CheckBox _enabled = new() { Content = "启用任务栏外观" };
    private readonly CheckBox _followScenes = new() { Content = "按场景自动切换外观", Margin = new Thickness(0, 7, 0, 0) };
    private readonly ComboBox _scene = new() { Margin = new Thickness(0, 4, 0, 8), DisplayMemberPath = "Name" };
    private readonly CheckBox _sceneEnabled = new() { Content = "为此场景启用单独的外观", Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBlock _sceneHint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
    private readonly ComboBox _style = new() { Margin = new Thickness(0, 4, 0, 12) };
    private readonly TextBox _color = new() { Margin = new Thickness(0, 4, 0, 12) };
    private readonly Slider _opacity = new() { Minimum = 0, Maximum = 255, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly TextBlock _opacityLabel = new();
    private readonly Slider _radius = new() { Minimum = 0, Maximum = 750, SmallChange = 1, LargeChange = 10 };
    private readonly StackPanel _radiusFields = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _radiusLabel = new();
    private readonly ComboBox _line = MakeVisibilityList();
    private readonly ComboBox _peek = MakeVisibilityList();
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 42, Margin = new Thickness(0, 10, 0, 8) };
    private readonly StackPanel _body = new() { Margin = new Thickness(20, 10, 20, 18) };
    private readonly StackPanel _footer = new() { Margin = new Thickness(20, 0, 20, 12) };
    private readonly Button _rules;
    private TaskbarAppearanceSettings _draft;
    private TaskbarAppearanceScene _activeScene = TaskbarAppearanceSettings.Scenes[0];
    private bool _loading;
    private bool _busy;
    private bool _closingReady;
    private bool _downloadApproved;

    internal TaskbarAppearanceWindow(Window owner, TaskbarAppearanceSession session)
    {
        _session = session;
        _draft = session.Saved;
        Owner = owner;
        Title = "任务栏外观";
        Width = 480;
        MaxHeight = Math.Max(340, Math.Min(700, SystemParameters.WorkArea.Height - 36));
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        _style.Items.Add(new ComboBoxItem { Content = "系统默认", Tag = "normal" });
        _style.Items.Add(new ComboBoxItem { Content = "透明", Tag = "clear" });
        _style.Items.Add(new ComboBoxItem { Content = "纯色", Tag = "opaque" });
        _style.Items.Add(new ComboBoxItem { Content = "模糊", Tag = "blur" });
        _style.Items.Add(new ComboBoxItem { Content = "亚克力", Tag = "acrylic" });

        _body.Children.Add(_enabled);
        _body.Children.Add(_followScenes);
        _body.Children.Add(new TextBlock { Text = "适用场景", Margin = new Thickness(0, 10, 0, 0) });
        _scene.ItemsSource = TaskbarAppearanceSettings.Scenes;
        _body.Children.Add(_scene);
        _body.Children.Add(_sceneEnabled);
        _body.Children.Add(_sceneHint);
        _body.Children.Add(new TextBlock { Text = "样式", Margin = new Thickness(0, 10, 0, 0) });
        _body.Children.Add(_style);
        _body.Children.Add(new TextBlock { Text = "颜色（#RRGGBB）" });
        var colorRow = new DockPanel();
        var pick = MakeButton("选颜色…", () =>
        {
            var value = TaskbarColorPicker.Choose(this, _color.Text.Trim());
            if (value is not null) _color.Text = value;
        });
        DockPanel.SetDock(pick, Dock.Right);
        pick.Margin = new Thickness(8, 4, 0, 12);
        colorRow.Children.Add(pick);
        colorRow.Children.Add(_color);
        _body.Children.Add(colorRow);
        _body.Children.Add(_opacityLabel);
        _body.Children.Add(_opacity);
        _radiusFields.Children.Add(_radiusLabel);
        _radiusFields.Children.Add(_radius);
        _body.Children.Add(_radiusFields);
        var options = new StackPanel { Margin = new Thickness(0, 8, 0, 4) };
        options.Children.Add(LabeledRow("任务栏顶部细线", _line));
        options.Children.Add(LabeledRow("Aero Peek（旧式任务栏）", _peek));
        var filters = new WrapPanel();
        _rules = MakeButton("窗口规则…", () => EditWindowConditions(isRules: true));
        filters.Children.Add(_rules);
        filters.Children.Add(MakeButton("忽略窗口…", () => EditWindowConditions(isRules: false)));
        options.Children.Add(filters);
        _body.Children.Add(new Expander { Header = "细线与窗口规则", Content = options, Margin = new Thickness(0, 10, 0, 0) });
        _footer.Children.Add(_message);
        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        tools.Children.Add(MakeButton("恢复系统外观", () => _ = RunAsync(RestoreSystemAsync)));
        _footer.Children.Add(tools);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = MakeButton("取消", Close);
        cancel.IsCancel = true;
        actions.Children.Add(cancel);
        actions.Children.Add(MakeButton("预览", () => _ = RunAsync(PreviewAsync)));
        var uniform = MakeButton("设为统一外观", () => _ = RunAsync(ApplyUniformAsync));
        uniform.ToolTip = "立即对所有场景使用当前样式。保存的动态场景仍可重新启用。";
        actions.Children.Add(uniform);
        actions.Children.Add(MakeButton("应用", () => _ = RunAsync(ApplyAsync), primary: true));
        _footer.Children.Add(actions);
        var layout = new DockPanel();
        DockPanel.SetDock(_footer, Dock.Bottom);
        layout.Children.Add(_footer);
        layout.Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = layout;
        UiDialogs.Chrome(this);
        AutomationProperties.SetAutomationId(_scene, "AppearanceScene");
        AutomationProperties.SetAutomationId(_sceneEnabled, "AppearanceSceneEnabled");
        AutomationProperties.SetAutomationId(_followScenes, "AppearanceFollowScenes");
        AutomationProperties.SetAutomationId(uniform, "AppearanceUniform");
        AutomationProperties.SetAutomationId(_style, "AppearanceStyle");
        AutomationProperties.SetAutomationId(_radius, "AppearanceBlurRadius");
        AutomationProperties.SetAutomationId(_line, "AppearanceTopLine");
        _scene.SelectionChanged += (_, _) => SwitchScene();
        _opacity.ValueChanged += (_, _) => UpdateOpacity();
        _radius.ValueChanged += (_, _) => UpdateOpacity();
        _style.SelectionChanged += (_, _) =>
        {
            if (!_loading && _activeScene.Key != TaskbarAppearanceSettings.DesktopScene && _style.SelectedItem is ComboBoxItem)
                _sceneEnabled.IsChecked = true;
            UpdateOpacity();
        };
        _followScenes.Checked += (_, _) => UpdateSceneHint();
        _followScenes.Unchecked += (_, _) => UpdateSceneHint();
        _enabled.Checked += (_, _) => UpdateSceneHint();
        _enabled.Unchecked += (_, _) => UpdateSceneHint();
        _sceneEnabled.Checked += (_, _) => UpdateSceneHint();
        _sceneEnabled.Unchecked += (_, _) => UpdateSceneHint();
        _previewTimer.Tick += (_, _) =>
        {
            if (_busy) return;
            _previewTimer.Stop();
            _ = RunAsync(async () =>
            {
                await _session.CancelPreviewAsync();
                _message.Text = "预览已结束，已经恢复到上次保存的设置。";
            });
        };
        Closing += OnClosing;
        Closed += (_, _) => _previewTimer.Stop();
        LoadFields(session.Saved);
        _message.Text = session.Restriction ?? (session.Running ? "外观组件已启动。" : "可以先预览，或应用后保存设置。");
    }

    private Button MakeButton(string label, Action click, bool primary = false)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 4) };
        if (primary) button.Style = (Style)FindResource("PrimaryButton");
        button.Click += (_, _) => click();
        return button;
    }

    private static ComboBox MakeVisibilityList()
    {
        var list = new ComboBox { MinWidth = 115 };
        list.Items.Add(new ComboBoxItem { Content = "沿用默认" });
        list.Items.Add(new ComboBoxItem { Content = "显示", Tag = true });
        list.Items.Add(new ComboBoxItem { Content = "隐藏", Tag = false });
        return list;
    }

    private static Grid LabeledRow(string text, Control field)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new());
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(field, 1);
        row.Children.Add(field);
        return row;
    }

    private void LoadFields(TaskbarAppearanceSettings settings)
    {
        _draft = settings;
        _enabled.IsChecked = settings.Enabled;
        _followScenes.IsChecked = settings.FollowScenes;
        LoadScene(TaskbarAppearanceSettings.Scenes[0]);
    }

    private void LoadScene(TaskbarAppearanceScene scene)
    {
        _loading = true;
        _activeScene = scene;
        var profile = _draft.ProfileFor(scene.Key);
        var defaults = TaskbarAppearanceSettings.SceneDefaultsFor(scene.Key);
        _scene.SelectedItem = scene;
        _sceneEnabled.Visibility = scene.Key == TaskbarAppearanceSettings.DesktopScene ? Visibility.Collapsed : Visibility.Visible;
        _sceneEnabled.IsChecked = profile.Enabled;
        _style.SelectedItem = _style.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, profile.Style));
        _color.Text = profile.Color;
        _opacity.Value = profile.Alpha;
        _radius.Value = profile.BlurRadius ?? defaults.BlurRadius;
        ((ComboBoxItem)_line.Items[0]).Content = defaults.ShowLine == true ? "场景默认（显示）" : "场景默认（隐藏）";
        ((ComboBoxItem)_peek.Items[0]).Content = defaults.ShowPeek == true ? "场景默认（显示）" : "场景默认（隐藏）";
        _line.SelectedItem = _line.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, profile.ShowLine));
        _peek.SelectedItem = _peek.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, profile.ShowPeek));
        _rules.IsEnabled = scene.AllowsRules;
        _loading = false;
        UpdateOpacity();
        UpdateSceneHint();
    }

    private void UpdateSceneHint()
    {
        if (_loading) return;
        if (_enabled.IsChecked != true)
        {
            _sceneHint.Text = "任务栏外观已停用。勾选启用后可预览，或直接使用「设为统一外观」。";
            return;
        }
        if (_followScenes.IsChecked != true)
        {
            _sceneHint.Text = "各场景统一使用桌面外观。重新开启按场景切换可恢复各场景的单独设置。";
            return;
        }
        if (_activeScene.Key != TaskbarAppearanceSettings.DesktopScene)
        {
            _sceneHint.Text = _sceneEnabled.IsChecked == true
                ? "此设置在该场景触发时显示。可用「预览」立即查看。"
                : "此场景未启用，应用只保存它的设置。";
            return;
        }
        var overrides = TaskbarAppearanceSettings.Scenes.Skip(1)
            .Where(scene => _draft.ProfileFor(scene.Key).Enabled).Select(scene => scene.Name).ToArray();
        _sceneHint.Text = overrides.Length == 0 ? ""
            : $"已启用「{string.Join("、", overrides)}」的独立外观，触发时会替代桌面外观。";
    }

    private void SwitchScene()
    {
        if (_loading || _scene.SelectedItem is not TaskbarAppearanceScene next || next == _activeScene) return;
        try
        {
            _draft = Fields();
            LoadScene(next);
        }
        catch (IOException ex)
        {
            _loading = true;
            _scene.SelectedItem = _activeScene;
            _loading = false;
            _message.Text = ex.Message;
        }
    }

    private void UpdateOpacity()
    {
        var mode = (_style.SelectedItem as ComboBoxItem)?.Tag as string;
        var custom = mode is not null and not "normal";
        _color.IsEnabled = custom;
        _opacity.IsEnabled = mode is "clear" or "blur" or "acrylic";
        _radiusFields.Visibility = mode == "blur" ? Visibility.Visible : Visibility.Collapsed;
        _opacityLabel.Text = $"颜色不透明度：{_opacity.Value * 100 / 255:0}%";
        _radiusLabel.Text = $"模糊半径：{_radius.Value:0.#}";
    }

    private TaskbarAppearanceSettings Fields()
    {
        var selected = _draft.ProfileFor(_activeScene.Key);
        string style = (_style.SelectedItem as ComboBoxItem)?.Tag as string ?? "normal";
        var profile = new TaskbarAppearanceProfile
        {
            Enabled = _sceneEnabled.IsChecked == true,
            Style = style,
            Color = _color.Text.Trim(),
            Alpha = (byte)Math.Round(_opacity.Value),
            BlurRadius = style == "blur" ? _radius.Value : selected.BlurRadius,
            ShowLine = (_line.SelectedItem as ComboBoxItem)?.Tag as bool?,
            ShowPeek = (_peek.SelectedItem as ComboBoxItem)?.Tag as bool?,
            Rules = selected.Rules
        };
        return (_draft.WithProfile(_activeScene.Key, profile) with
        {
            Enabled = _enabled.IsChecked == true,
            FollowScenes = _followScenes.IsChecked == true
        }).Validate();
    }

    private void EditWindowConditions(bool isRules)
    {
        if (isRules && !_activeScene.AllowsRules) return;
        try { _draft = Fields(); }
        catch (IOException ex) { _message.Text = ex.Message; return; }
        var activeProfile = _draft.ProfileFor(_activeScene.Key);
        var current = isRules ? activeProfile.Rules : _draft.IgnoredWindows;
        var window = new Window
        {
            Owner = this, Title = isRules ? "窗口规则" : "忽略窗口", Width = 480, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize
        };
        var editor = new TextBox
        {
            Text = current is { } value ? JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) : "{}",
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 190, MaxLength = 24000, Margin = new Thickness(0, 8, 0, 8)
        };
        var hint = isRules
            ? "可按进程名、窗口标题或窗口类设置特殊外观。示例：{\"process_name\":{\"notepad.exe\":{\"accent\":\"acrylic\"}}}"
            : "匹配的窗口不参与外观状态判断。示例：{\"process_name\":[\"notepad.exe\"]}。";
        var body = new StackPanel { Margin = new Thickness(18, 12, 18, 15) };
        body.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(editor);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, MinHeight = 18 };
        body.Children.Add(error);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = MakeButton("取消", window.Close);
        cancel.IsCancel = true;
        actions.Children.Add(cancel);
        JsonElement? selected = null;
        actions.Children.Add(MakeButton("确定", () =>
        {
            try
            {
                using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(editor.Text) ? "{}" : editor.Text,
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 14 });
                if (isRules) TaskbarAppearanceFilters.ValidateRules(parsed.RootElement);
                else TaskbarAppearanceFilters.ValidateIgnoredWindows(parsed.RootElement);
                selected = parsed.RootElement.EnumerateObject().Any() ? parsed.RootElement.Clone() : null;
                window.DialogResult = true;
            }
            catch (Exception ex) when (ex is IOException or JsonException) { error.Text = ex.Message; }
        }, primary: true));
        body.Children.Add(actions);
        window.Content = body;
        UiDialogs.Chrome(window);
        if (window.ShowDialog() != true) return;
        _draft = isRules ? _draft.WithProfile(_activeScene.Key, activeProfile with { Rules = selected })
            : _draft with { IgnoredWindows = selected };
        _message.Text = "窗口条件已编辑，选择「应用」保存。";
    }

    private bool CanApply(TaskbarAppearanceSettings settings)
    {
        if (!settings.UsesComponent) return true;
        if (_session.Restriction is { } restriction)
        {
            _message.Text = restriction;
            return false;
        }
        if (_session.NeedsDownload && !_downloadApproved)
        {
            string package = _session.UsesCompatibilityPackage ? "26340 兼容组件" : "TranslucentTB 2026.2 便携组件";
            _downloadApproved = UiDialogs.Show(this, $"首次启用需从 GitHub 下载并校验 {package}（约 1.8 MB）。", Title,
                MessageBoxButton.OKCancel) == MessageBoxResult.OK;
            return _downloadApproved;
        }
        return true;
    }

    private async Task PreviewAsync()
    {
        var appearance = Fields().ForPreview(_activeScene.Key);
        if (!CanApply(appearance)) return;
        if (!appearance.UsesComponent && !_session.Running)
        {
            _message.Text = "当前没有由轻匣应用的外观。";
            return;
        }
        await _session.PreviewAsync(appearance, _downloadApproved);
        _previewTimer.Stop();
        _previewTimer.Start();
        _message.Text = $"正在预览「{_activeScene.Name}」的外观。选择应用保存，或等待 12 秒自动恢复。";
    }

    private async Task ApplyAsync()
    {
        var appearance = Fields();
        if (!CanApply(appearance)) return;
        await _session.ApplyAsync(appearance, _downloadApproved);
        _draft = _session.Saved;
        _previewTimer.Stop();
        _message.Text = !appearance.Enabled ? "已保存，轻匣的任务栏外观已关闭。"
            : _activeScene.Key != TaskbarAppearanceSettings.DesktopScene &&
                (!appearance.FollowScenes || !appearance.ProfileFor(_activeScene.Key).Enabled)
                ? "已保存此场景的设置。启用场景切换与此场景后，触发时才会显示。"
                : appearance.UsesComponent ? "外观设置已应用，可在「预览」查看当前所选样式。" : "已恢复系统默认外观。";
        UpdateSceneHint();
    }

    private async Task ApplyUniformAsync()
    {
        var appearance = Fields().ForUniformAppearance(_activeScene.Key) with { Enabled = true };
        if (!CanApply(appearance)) return;
        await _session.ApplyAsync(appearance, _downloadApproved);
        _previewTimer.Stop();
        LoadFields(_session.Saved);
        _message.Text = "已将所选样式用于所有场景。原来的动态场景设置已保留。";
    }

    private async Task RestoreSystemAsync()
    {
        await _session.ApplyAsync(_session.Saved with { Enabled = false }, allowDownload: false);
        _previewTimer.Stop();
        LoadFields(_session.Saved);
        _message.Text = "轻匣的任务栏外观已关闭。";
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (_busy) return;
        _busy = true;
        _body.IsEnabled = false;
        _footer.IsEnabled = false;
        try { await operation(); }
        catch (Exception ex)
        {
            _message.Text = ex.Message;
            if (_session.HasPreview)
            {
                try { await _session.CancelPreviewAsync(); }
                catch (Exception rollback) { _message.Text += "\n恢复上次设置失败：" + rollback.Message; }
            }
        }
        finally
        {
            _busy = false;
            _body.IsEnabled = true;
            _footer.IsEnabled = true;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_closingReady) return;
        if (_busy)
        {
            args.Cancel = true;
            return;
        }
        if (!_session.HasPreview) return;
        args.Cancel = true;
        _previewTimer.Stop();
        Dispatcher.BeginInvoke(async () =>
        {
            await RunAsync(_session.CancelPreviewAsync);
            if (_session.HasPreview) return;
            _closingReady = true;
            Close();
        });
    }
}
