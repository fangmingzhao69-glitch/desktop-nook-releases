using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopNook.Core;

namespace DesktopNook.App;

internal sealed class TaskbarAppearancePage : UserControl
{
    private readonly Window _owner;
    private readonly TaskbarAppearanceSession _session;
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromSeconds(12) };
    private readonly CheckBox _enabled = new() { Content = "启用任务栏外观" };
    private readonly CheckBox _followScenes = new() { Content = "按场景自动切换外观" };
    private readonly ListBox _scene = new() { DisplayMemberPath = "Name", BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly StackPanel _sceneControls = new();
    private Button _pickColor = null!;
    private readonly CheckBox _sceneEnabled = new() { Content = "为此场景启用单独的外观" };
    private readonly TextBlock _sceneHint = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _layout = new();
    private readonly ComboBox _style = new();
    private readonly TextBox _color = new() { Width = 112, Height = 34, Padding = new Thickness(8, 4, 8, 4), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Border _colorSample = new() { Width = 22, Height = 22, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1) };
    private readonly Slider _opacity = new() { Minimum = 0, Maximum = 255, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly TextBlock _opacityLabel = new();
    private readonly Slider _radius = new() { Minimum = 0, Maximum = 750, SmallChange = 1, LargeChange = 10 };
    private readonly Grid _radiusRow;
    private readonly TextBlock _radiusLabel = new();
    private readonly TextBlock _message = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 7) };
    private readonly StackPanel _body = new() { Margin = new Thickness(22, 2, 22, 10) };
    private readonly StackPanel _footer = new();
    private readonly TextBlock _profileCaption = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 8) };
    private TaskbarAppearanceSettings _draft;
    private TaskbarAppearanceScene _activeScene = TaskbarAppearanceSettings.Scenes[0];
    private bool _loading;
    private bool _busy;
    private bool _downloadApproved;

    internal TaskbarAppearancePage(Window owner, TaskbarAppearanceSession session)
    {
        _owner = owner;
        _session = session;
        _draft = session.Saved;
        _layout.Items.Add("系统布局");
        _layout.Items.Add("胶囊形态");
        AutomationProperties.SetAutomationId(_layout, "NativeTaskbarLayout");
        _style.Items.Add(new ComboBoxItem { Content = "系统默认", Tag = "normal" });
        _style.Items.Add(new ComboBoxItem { Content = "透明", Tag = "clear" });
        _style.Items.Add(new ComboBoxItem { Content = "纯色", Tag = "opaque" });
        _style.Items.Add(new ComboBoxItem { Content = "模糊", Tag = "blur" });
        _style.Items.Add(new ComboBoxItem { Content = "亚克力", Tag = "acrylic" });
        _style.Items.Add(new ComboBoxItem { Content = "液态玻璃", Tag = "liquid", ToolTip = "轻模糊的背景在边缘产生折射，并沿上下沿显示渐变高光。" });
        _style.Items.Add(new ComboBoxItem { Content = "柔雾玻璃", Tag = "frosted", ToolTip = "提高背景模糊强度，减轻背景干扰。" });

        _enabled.Margin = new Thickness(0, 0, 0, 10);
        _body.Children.Add(_enabled);
        _body.Children.Add(FieldRow("任务栏形态", _layout));
        var appearanceCard = new StackPanel();
        _followScenes.Content = "按场景设置不同外观";
        _followScenes.Margin = new Thickness(0, 4, 0, 8);
        appearanceCard.Children.Add(_followScenes);
        ConfigureScenePicker();
        _scene.ItemsSource = TaskbarAppearanceSettings.Scenes;
        _sceneControls.Children.Add(_scene);
        appearanceCard.Children.Add(_sceneControls);
        _profileCaption.Foreground = ThemePalette.Brush("TextPrimaryBrush");
        _profileCaption.Margin = new Thickness(0, 10, 0, 6);
        appearanceCard.Children.Add(_profileCaption);
        _sceneEnabled.Content = "为此场景单独设置外观";
        _sceneEnabled.Margin = new Thickness(0, 0, 0, 4);
        appearanceCard.Children.Add(_sceneEnabled);
        _sceneHint.Foreground = ThemePalette.Brush("TextSecondaryBrush");
        _sceneHint.Margin = new Thickness(0, 0, 0, 8);
        appearanceCard.Children.Add(_sceneHint);
        _layout.ToolTip = "开始按钮、应用和托盘合在一起，宽度随图标数量自动调整。";
        var assistBall = MakeButton("临时定位信息球…", () => _ = RunAsync(async () =>
        {
            if (App.IsSandbox) throw new InvalidOperationException("隔离模式不请求管理员权限。");
            _message.Text = await NativeTaskbarInfoBall.RequestAssistanceAsync();
        }));
        assistBall.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(assistBall, Dock.Right);
        var informationBall = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var automaticBall = new CheckBox
        {
            Content = "自动连接信息球",
            Margin = new Thickness(0, 0, 0, 6),
            IsChecked = !App.IsSandbox && NativeInfoBallAutomatic.Enabled,
            IsEnabled = !App.IsSandbox,
            ToolTip = "首次启用需一次 Windows 授权。以后按需启动定位助手，折页主程序保持普通权限；关闭后移除授权任务。"
        };
        AutomationProperties.SetAutomationId(automaticBall, "Taskbar-InfoBall-Automatic");
        automaticBall.Click += (_, _) => _ = RunAsync(async () =>
        {
            try { _message.Text = await NativeInfoBallAutomatic.ConfigureAsync(automaticBall.IsChecked == true); }
            finally { automaticBall.IsChecked = NativeInfoBallAutomatic.Enabled; }
        });
        informationBall.Children.Add(automaticBall);
        informationBall.Children.Add(assistBall);
        appearanceCard.Children.Add(FieldRow("样式", _style));
        var colorControls = new StackPanel { Orientation = Orientation.Horizontal };
        _colorSample.BorderBrush = ThemePalette.Brush("OutlineBrush");
        _colorSample.Margin = new Thickness(0, 0, 8, 0);
        colorControls.Children.Add(_colorSample);
        _color.ToolTip = "颜色格式 #RRGGBB";
        AutomationProperties.SetName(_color, "颜色");
        colorControls.Children.Add(_color);
        var pick = _pickColor = MakeButton("选颜色…", () =>
        {
            var value = TaskbarColorPicker.Choose(_owner, _color.Text.Trim());
            if (value is not null) _color.Text = value;
        });
        pick.Margin = new Thickness(7, 0, 0, 0);
        pick.MinHeight = 34;
        pick.Background = ThemePalette.Brush("SurfaceBrush");
        colorControls.Children.Add(pick);
        appearanceCard.Children.Add(FieldRow("颜色", colorControls));
        appearanceCard.Children.Add(FieldRow("不透明度", SliderField(_opacity, _opacityLabel, "颜色不透明度")));
        _radiusRow = FieldRow("模糊半径", SliderField(_radius, _radiusLabel, "模糊半径"));
        appearanceCard.Children.Add(_radiusRow);
        _body.Children.Add(Card(appearanceCard));
        var advanced = new Expander { Header = "信息球与辅助选项", Content = informationBall,
            Padding = new Thickness(0, 8, 0, 4), Margin = new Thickness(0, 4, 0, 0), Foreground = ThemePalette.Brush("TextPrimaryBrush") };
        AutomationProperties.SetAutomationId(advanced, "AppearanceAdvanced");
        _body.Children.Add(advanced);

        _message.Foreground = ThemePalette.Brush("TextSecondaryBrush");
        _footer.Children.Add(_message);
        var actionBar = new DockPanel();
        var restore = MakeButton("恢复系统外观", () => _ = RunAsync(RestoreSystemAsync));
        restore.Foreground = ThemePalette.Brush("DangerBrush");
        restore.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(restore, Dock.Left);
        actionBar.Children.Add(restore);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(MakeButton("取消更改", () => _ = RunAsync(ResetDraftAsync)));
        actions.Children.Add(MakeButton("预览", () => _ = RunAsync(PreviewAsync)));
        var apply = MakeButton("应用", () => _ = RunAsync(ApplyAsync), primary: true);
        apply.MinWidth = 68;
        apply.Margin = new Thickness(4, 0, 0, 0);
        actions.Children.Add(apply);
        actionBar.Children.Add(actions);
        _footer.Children.Add(actionBar);
        var layout = new DockPanel();
        var heading = new TextBlock
        {
            Text = SettingsWindow.TaskbarPage, FontSize = 17, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(22, 8, 22, 12), Foreground = ThemePalette.Brush("TextPrimaryBrush")
        };
        DockPanel.SetDock(heading, Dock.Top);
        layout.Children.Add(heading);
        var footerFrame = new Border
        {
            BorderBrush = ThemePalette.Brush("LineBrush"), BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(22, 9, 22, 12), Child = _footer
        };
        DockPanel.SetDock(footerFrame, Dock.Bottom);
        layout.Children.Add(footerFrame);
        layout.Children.Add(new ScrollViewer
        {
            Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        Content = layout;
        _layout.SelectionChanged += (_, _) =>
        {
            if (_loading || _layout.SelectedIndex != 1) return;
            _enabled.IsChecked = true;
            if (_activeScene.Key == TaskbarAppearanceSettings.DesktopScene &&
                (_style.SelectedItem as ComboBoxItem)?.Tag as string == "clear")
            {
                _style.SelectedIndex = 0;
            }
        };
        AutomationProperties.SetAutomationId(_scene, "AppearanceScene");
        AutomationProperties.SetAutomationId(_sceneEnabled, "AppearanceSceneEnabled");
        AutomationProperties.SetAutomationId(_followScenes, "AppearanceFollowScenes");
        AutomationProperties.SetAutomationId(_style, "AppearanceStyle");
        AutomationProperties.SetAutomationId(_radius, "AppearanceBlurRadius");
        AutomationProperties.SetAutomationId(_opacity, "AppearanceOpacity");
        AutomationProperties.SetAutomationId(apply, "AppearanceApply");
        _scene.SelectionChanged += (_, _) => SwitchScene();
        _color.TextChanged += (_, _) => UpdateColorSample();
        _opacity.ValueChanged += (_, _) => UpdateOpacity();
        _radius.ValueChanged += (_, _) => UpdateOpacity();
        _style.SelectionChanged += (_, _) =>
        {
            if (!_loading && _style.SelectedItem is ComboBoxItem { Tag: string selected })
            {
                if (selected == "liquid") _opacity.Value = 38;
                if (selected == "frosted") _opacity.Value = 104;
            }
            UpdateOpacity();
        };
        _followScenes.Checked += (_, _) => ChangeSceneMode();
        _followScenes.Unchecked += (_, _) => ChangeSceneMode();
        _enabled.Checked += (_, _) => UpdateSceneHint();
        _enabled.Unchecked += (_, _) => UpdateSceneHint();
        _sceneEnabled.Checked += (_, _) => ChangeSceneOverride();
        _sceneEnabled.Unchecked += (_, _) => ChangeSceneOverride();
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
        LoadFields(session.Saved);
        _message.Text = session.Restriction ?? "预览 12 秒后自动还原；点击应用保存。";
    }

    private Button MakeButton(string label, Action click, bool primary = false)
    {
        var button = new Button { Content = label, MinHeight = 34, Margin = new Thickness(0, 0, 4, 0) };
        if (primary) button.Style = (Style)FindResource("PrimaryButton");
        button.Click += (_, _) => click();
        return button;
    }

    private void ConfigureScenePicker()
    {
        _scene.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        _scene.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        _scene.SetValue(ScrollViewer.CanContentScrollProperty, false);
        _scene.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "SceneChrome";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        border.SetValue(Border.PaddingProperty, new Thickness(9, 7, 9, 7));
        border.SetValue(Border.MarginProperty, new Thickness(0, 0, 5, 5));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Background)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new Binding(nameof(BorderBrush)) { RelativeSource = RelativeSource.TemplatedParent });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border };
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 12d));
        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 114d));
        style.Setters.Add(new Setter(Control.ForegroundProperty, ThemePalette.Brush("TextSecondaryBrush")));
        style.Setters.Add(new Setter(Control.BackgroundProperty, ThemePalette.Brush("SurfaceSubtleBrush")));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(AutomationProperties.AutomationIdProperty, new Binding("Key") { StringFormat = "TaskbarScene-{0}" }));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, ThemePalette.Brush("SelectionHoverBrush")));
        style.Triggers.Add(hover);
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, ThemePalette.Brush("PrimaryBrush")));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, ThemePalette.Brush("OnPrimaryBrush")));
        style.Triggers.Add(selected);
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        focus.Setters.Add(new Setter(Control.BorderBrushProperty, ThemePalette.Brush("AccentInkBrush")));
        style.Triggers.Add(focus);
        _scene.ItemContainerStyle = style;
    }

    private TaskbarAppearanceProfile ReadEditedProfile()
    {
        var selected = _draft.ProfileFor(_activeScene.Key);
        string style = (_style.SelectedItem as ComboBoxItem)?.Tag as string ?? "normal";
        return selected with { Enabled = _sceneEnabled.IsChecked == true, Style = style, Color = _color.Text.Trim(),
            Alpha = (byte)Math.Round(_opacity.Value), BlurRadius = style == "blur" ? _radius.Value : selected.BlurRadius };
    }

    private void ChangeSceneMode()
    {
        if (_loading) return;
        try { _draft = Fields(); LoadScene(TaskbarAppearanceSettings.Scenes[0]); CancelPreviewForEdit(); }
        catch (IOException error)
        {
            _loading = true; _followScenes.IsChecked = _draft.FollowScenes; _loading = false;
            _message.Text = error.Message;
        }
    }

    private void ChangeSceneOverride()
    {
        if (_loading || _activeScene.Key == TaskbarAppearanceSettings.DesktopScene) return;
        try
        {
            bool enabled = _sceneEnabled.IsChecked == true;
            var profile = _draft.ProfileFor(_activeScene.Key);
            if (!enabled) profile = ReadEditedProfile();
            else if (_draft.Profiles?.ContainsKey(_activeScene.Key) != true)
            {
                var baseProfile = _draft.ProfileFor(TaskbarAppearanceSettings.DesktopScene);
                profile = new TaskbarAppearanceProfile { Style = baseProfile.Style, Color = baseProfile.Color,
                    Alpha = baseProfile.Alpha, BlurRadius = baseProfile.BlurRadius };
            }
            _draft = _draft.WithProfile(_activeScene.Key, profile with { Enabled = enabled });
            LoadScene(_activeScene);
            CancelPreviewForEdit();
        }
        catch (IOException error)
        {
            _loading = true; _sceneEnabled.IsChecked = _draft.ProfileFor(_activeScene.Key).Enabled; _loading = false;
            _message.Text = error.Message;
        }
    }

    private static Border Card(UIElement content) => new()
    {
        Child = content, Background = ThemePalette.Brush("CanvasSubtleBrush"),
        BorderBrush = ThemePalette.Brush("LineBrush"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 8),
        Padding = new Thickness(12, 8, 12, 8)
    };

    private static Grid FieldRow(string text, FrameworkElement field)
    {
        var row = new Grid { MinHeight = 30, Margin = new Thickness(0, 2, 0, 3) };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(88) });
        row.ColumnDefinitions.Add(new());
        var caption = new TextBlock
        {
            Text = text, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemePalette.Brush("TextSecondaryBrush")
        };
        if (field is Control control) AutomationProperties.SetLabeledBy(control, caption);
        row.Children.Add(caption);
        Grid.SetColumn(field, 1);
        row.Children.Add(field);
        return row;
    }

    private DockPanel SliderField(Slider slider, TextBlock value, string name)
    {
        var row = new DockPanel();
        value.Foreground = ThemePalette.Brush("AccentInkBrush");
        value.FontSize = 12;
        value.HorizontalAlignment = HorizontalAlignment.Center;
        value.VerticalAlignment = VerticalAlignment.Center;
        var badge = new Border
        {
            MinWidth = 50, Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(5),
            Background = ThemePalette.Brush("SelectionBrush"), Child = value
        };
        DockPanel.SetDock(badge, Dock.Right);
        row.Children.Add(badge);
        slider.Style = (Style)FindResource("TaskbarAppearanceSlider");
        slider.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(slider, name);
        row.Children.Add(slider);
        return row;
    }

    private void UpdateColorSample()
    {
        string text = _color.Text.Trim();
        if (text.Length == 7 && text[0] == '#' &&
            uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            brush.Freeze();
            _colorSample.Background = brush;
        }
        else _colorSample.Background = Brushes.Transparent;
    }

    private void LoadFields(TaskbarAppearanceSettings settings)
    {
        _draft = settings;
        _loading = true;
        _layout.SelectedIndex = settings.FloatingLayout ? 1 : 0;
        _enabled.IsChecked = settings.Enabled;
        _followScenes.IsChecked = settings.FollowScenes;
        LoadScene(TaskbarAppearanceSettings.Scenes[0]);
    }

    private void LoadScene(TaskbarAppearanceScene scene)
    {
        _loading = true;
        _activeScene = scene;
        bool inherited = scene.Key != TaskbarAppearanceSettings.DesktopScene && !_draft.ProfileFor(scene.Key).Enabled;
        _profileCaption.Text = _followScenes.IsChecked != true ? "所有场景 · 统一外观" : scene.Key == TaskbarAppearanceSettings.DesktopScene
            ? "桌面 · 默认外观" : scene.Name + (inherited ? " · 继承桌面" : " · 单独外观");
        _sceneControls.Visibility = _followScenes.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        var stored = _draft.ProfileFor(scene.Key);
        var profile = inherited ? _draft.ProfileFor(TaskbarAppearanceSettings.DesktopScene) : stored;
        var defaults = TaskbarAppearanceSettings.SceneDefaultsFor(scene.Key);
        _scene.SelectedItem = scene;
        _sceneEnabled.Visibility = scene.Key == TaskbarAppearanceSettings.DesktopScene ? Visibility.Collapsed : Visibility.Visible;
        _sceneEnabled.IsChecked = stored.Enabled;
        _style.SelectedItem = _style.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, profile.Style));
        _color.Text = profile.Color;
        _opacity.Value = profile.Alpha;
        _radius.Value = profile.BlurRadius ?? defaults.BlurRadius;
        _loading = false;
        UpdateOpacity();
        UpdateSceneHint();
    }

    private void UpdateSceneHint()
    {
        if (_loading) return;
        if (_enabled.IsChecked != true)
        {
            ShowSceneHint("启用任务栏外观后，可预览当前设置，再点击应用保存。");
            return;
        }
        if (_followScenes.IsChecked != true)
        {
            ShowSceneHint("当前样式用于所有场景。开启场景设置后可分别调整，原有设置会保留。");
            return;
        }
        if (_activeScene.Key != TaskbarAppearanceSettings.DesktopScene)
        {
            ShowSceneHint(_sceneEnabled.IsChecked == true
                ? "已单独设置；点击应用后，在该场景触发时生效。"
                : "当前继承桌面外观。打开上方开关后，可单独调整此场景。");
            return;
        }
        var overrides = TaskbarAppearanceSettings.Scenes.Skip(1)
            .Where(scene => _draft.ProfileFor(scene.Key).Enabled).Select(scene => scene.Name).ToArray();
        ShowSceneHint(overrides.Length switch
        {
            0 => "",
            1 => $"「{overrides[0]}」的独立外观会在触发时替代桌面外观。",
            _ => $"{overrides.Length} 个场景的独立外观会在触发时替代桌面外观。"
        }, overrides.Length > 1 ? string.Join("、", overrides) : null);
    }

    private void ShowSceneHint(string text, string? detail = null)
    {
        _sceneHint.Text = text;
        _sceneHint.ToolTip = detail;
        AutomationProperties.SetHelpText(_sceneHint, detail ?? "");
        _sceneHint.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SwitchScene()
    {
        if (_loading || _scene.SelectedItem is not TaskbarAppearanceScene next || next == _activeScene) return;
        try
        {
            _draft = Fields();
            LoadScene(next);
            CancelPreviewForEdit();
        }
        catch (IOException ex)
        {
            _loading = true;
            _scene.SelectedItem = _activeScene;
            _loading = false;
            _message.Text = ex.Message;
        }
    }

    private void CancelPreviewForEdit()
    {
        if (!_session.HasPreview) return;
        _previewTimer.Stop();
        _ = RunAsync(async () =>
        {
            await _session.CancelPreviewAsync();
            _message.Text = "已切换设置，上一次预览已结束。可重新预览，再点击应用保存。";
        });
    }

    private void UpdateOpacity()
    {
        var mode = (_style.SelectedItem as ComboBoxItem)?.Tag as string;
        var custom = mode is not null and not "normal";
        bool editable = _activeScene.Key == TaskbarAppearanceSettings.DesktopScene || _sceneEnabled.IsChecked == true;
        _style.IsEnabled = editable;
        _color.IsEnabled = editable && custom;
        _pickColor.IsEnabled = editable && custom;
        _opacity.IsEnabled = editable && mode is "clear" or "blur" or "acrylic" or "liquid" or "frosted";
        _radius.IsEnabled = editable;
        _radiusRow.Visibility = mode == "blur" ? Visibility.Visible : Visibility.Collapsed;
        _opacityLabel.Text = $"{_opacity.Value * 100 / 255:0}%";
        _radiusLabel.Text = $"{_radius.Value:0.#}";
    }

    private TaskbarAppearanceSettings Fields()
    {
        var selected = _draft.ProfileFor(_activeScene.Key);
        var profile = _activeScene.Key != TaskbarAppearanceSettings.DesktopScene && _sceneEnabled.IsChecked != true
            ? selected with { Enabled = false } : ReadEditedProfile();
        return (_draft.WithProfile(_activeScene.Key, profile) with
        {
            Enabled = _enabled.IsChecked == true,
            FloatingLayout = _layout.SelectedIndex == 1,
            FollowScenes = _followScenes.IsChecked == true
        }).Validate();
    }

    private bool CanApply(TaskbarAppearanceSettings settings)
    {
        if (!settings.UsesComponent) return true;
        if (_session.Restriction is { } restriction)
        {
            _message.Text = restriction;
            return false;
        }
        if (settings.UsesComponent && _session.NeedsDownload && !_downloadApproved)
        {
            _downloadApproved = UiDialogs.Show(_owner, "首次启用需从 GitHub 下载并校验任务栏组件（约 0.8 MB）。", SettingsWindow.TaskbarPage,
                MessageBoxButton.OKCancel) == MessageBoxResult.OK;
            return _downloadApproved;
        }
        return true;
    }

    private async Task PreviewAsync()
    {
        string scene = _activeScene.Key != TaskbarAppearanceSettings.DesktopScene && _sceneEnabled.IsChecked != true
            ? TaskbarAppearanceSettings.DesktopScene : _activeScene.Key;
        var appearance = Fields().ForPreview(scene);
        if (!CanApply(appearance)) return;
        if (!appearance.UsesComponent && !_session.Running)
        {
            _message.Text = "当前没有由折页应用的外观。";
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
        _message.Text = !appearance.Enabled ? "已保存，折页的任务栏外观已关闭。"
            : _activeScene.Key != TaskbarAppearanceSettings.DesktopScene &&
                (!appearance.FollowScenes || !appearance.ProfileFor(_activeScene.Key).Enabled)
                ? "已保存此场景的设置。启用场景切换与此场景后，触发时才会显示。"
                : appearance.UsesComponent ? "外观设置已应用，可在「预览」查看当前所选样式。" : "已恢复系统默认外观。";
        UpdateSceneHint();
    }

    private async Task RestoreSystemAsync()
    {
        await _session.ApplyAsync(_session.Saved with { Enabled = false }, allowDownload: false);
        _previewTimer.Stop();
        LoadFields(_session.Saved);
        _message.Text = "折页的任务栏外观已关闭。";
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

    internal bool NeedsExitCheck => _busy || _session.HasPreview;

    internal void StopPreviewTimer() => _previewTimer.Stop();

    internal async Task<bool> TryLeaveAsync()
    {
        if (_busy) return false;
        if (!_session.HasPreview) return true;
        StopPreviewTimer();
        await RunAsync(async () =>
        {
            await _session.CancelPreviewAsync();
            _message.Text = "预览已结束，已经恢复到上次保存的设置。";
        });
        return !_session.HasPreview;
    }

    private async Task ResetDraftAsync()
    {
        StopPreviewTimer();
        await _session.CancelPreviewAsync();
        LoadFields(_session.Saved);
        _message.Text = "已经恢复到上次保存的设置。";
    }
}
