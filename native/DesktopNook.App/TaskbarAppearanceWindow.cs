using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DesktopNook.Core;
using Microsoft.Win32;

namespace DesktopNook.App;

internal sealed class TaskbarAppearanceWindow : Window
{
    private readonly TaskbarAppearanceSession _session;
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromSeconds(12) };
    private readonly CheckBox _enabled = new() { Content = "启用任务栏外观" };
    private readonly ComboBox _style = new() { Margin = new Thickness(0, 4, 0, 12) };
    private readonly TextBox _color = new() { Margin = new Thickness(0, 4, 0, 12) };
    private readonly Slider _opacity = new() { Minimum = 0, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly TextBlock _opacityLabel = new();
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 42, Margin = new Thickness(0, 10, 0, 8) };
    private readonly StackPanel _body = new() { Margin = new Thickness(20, 10, 20, 18) };
    private bool _busy;
    private bool _closingReady;
    private bool _downloadApproved;

    internal TaskbarAppearanceWindow(Window owner, TaskbarAppearanceSession session)
    {
        _session = session;
        Owner = owner;
        Title = "任务栏外观";
        Width = 440;
        MaxHeight = Math.Max(340, Math.Min(580, SystemParameters.WorkArea.Height - 36));
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        _style.Items.Add(new ComboBoxItem { Content = "系统默认", Tag = "normal" });
        _style.Items.Add(new ComboBoxItem { Content = "透明", Tag = "clear" });
        _style.Items.Add(new ComboBoxItem { Content = "纯色", Tag = "opaque" });

        _body.Children.Add(_enabled);
        _body.Children.Add(new TextBlock { Text = "样式", Margin = new Thickness(0, 10, 0, 0) });
        _body.Children.Add(_style);
        _body.Children.Add(new TextBlock { Text = "颜色（#RRGGBB）" });
        _body.Children.Add(_color);
        _body.Children.Add(_opacityLabel);
        _body.Children.Add(_opacity);
        _body.Children.Add(_message);
        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        tools.Children.Add(MakeButton("导入独立版设置…", Import));
        if (session.UsesCompatibilityPackage)
            tools.Children.Add(MakeButton("导入 26340 兼容组件…", ImportCompatibilityPackage));
        tools.Children.Add(MakeButton("恢复系统外观", () => _ = RunAsync(RestoreSystemAsync)));
        _body.Children.Add(tools);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = MakeButton("取消", Close);
        cancel.IsCancel = true;
        actions.Children.Add(cancel);
        actions.Children.Add(MakeButton("预览", () => _ = RunAsync(PreviewAsync)));
        actions.Children.Add(MakeButton("应用", () => _ = RunAsync(ApplyAsync), primary: true));
        _body.Children.Add(actions);
        Content = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        UiDialogs.Chrome(this);
        _opacity.ValueChanged += (_, _) => UpdateOpacity();
        _style.SelectionChanged += (_, _) => UpdateOpacity();
        _enabled.Checked += (_, _) => UpdateOpacity();
        _enabled.Unchecked += (_, _) => UpdateOpacity();
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

    private void LoadFields(TaskbarAppearanceSettings settings)
    {
        _enabled.IsChecked = settings.Enabled;
        _style.SelectedItem = _style.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, settings.Style));
        _color.Text = settings.Color;
        _opacity.Value = Math.Round(settings.Alpha * 100d / 255);
        UpdateOpacity();
    }

    private void UpdateOpacity()
    {
        var custom = _enabled.IsChecked == true && !Equals((_style.SelectedItem as ComboBoxItem)?.Tag, "normal");
        _color.IsEnabled = custom;
        _opacity.IsEnabled = custom && Equals((_style.SelectedItem as ComboBoxItem)?.Tag, "clear");
        _opacityLabel.Text = $"透明模式的颜色不透明度：{_opacity.Value:0}%";
    }

    private TaskbarAppearanceSettings Fields()
    {
        var settings = _session.Saved with
        {
            Enabled = _enabled.IsChecked == true,
            Style = (_style.SelectedItem as ComboBoxItem)?.Tag as string ?? "normal",
            Color = _color.Text.Trim(),
            Alpha = (byte)Math.Round(_opacity.Value * 255 / 100)
        };
        return settings.Validate();
    }

    private void Import()
    {
        var picker = new OpenFileDialog { Title = "导入 TranslucentTB 的 settings.json", Filter = "JSON 文件 (*.json)|*.json", FileName = "settings.json" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var imported = TaskbarAppearanceSettings.Import(picker.FileName);
            LoadFields(imported with { Enabled = _enabled.IsChecked == true });
            _message.Text = "已读入独立版的桌面样式和颜色。选择应用后保存到轻匣。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _message.Text = ex.Message;
        }
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
            _downloadApproved = UiDialogs.Show(this, "首次启用需从 GitHub 下载并校验 TranslucentTB 2026.2 便携组件，约 1.8 MB。", Title,
                MessageBoxButton.OKCancel) == MessageBoxResult.OK;
            return _downloadApproved;
        }
        return true;
    }

    private void ImportCompatibilityPackage()
    {
        var picker = new OpenFileDialog
        {
            Title = "导入预览版随附的兼容组件",
            Filter = "组件压缩包 (*.zip)|*.zip",
            FileName = "TranslucentTB-2026.2-26340-preview.zip"
        };
        if (picker.ShowDialog(this) != true) return;
        _ = RunAsync(async () =>
        {
            await _session.ImportCompatibilityPackageAsync(picker.FileName);
            _message.Text = _session.Restriction ?? "兼容组件已核验。选择预览或应用后启用。";
        });
    }

    private async Task PreviewAsync()
    {
        var appearance = Fields();
        if (!CanApply(appearance)) return;
        if (!appearance.UsesComponent && !_session.Running)
        {
            _message.Text = "当前没有由轻匣应用的外观。";
            return;
        }
        await _session.PreviewAsync(appearance, _downloadApproved);
        _previewTimer.Stop();
        _previewTimer.Start();
        _message.Text = "预览中。选择应用保存，或等待 12 秒自动恢复。";
    }

    private async Task ApplyAsync()
    {
        var appearance = Fields();
        if (!CanApply(appearance)) return;
        await _session.ApplyAsync(appearance, _downloadApproved);
        _previewTimer.Stop();
        _message.Text = appearance.UsesComponent ? "外观设置已保存。" : "已保存，轻匣的任务栏外观已关闭。";
    }

    private async Task RestoreSystemAsync()
    {
        await _session.ApplyAsync(_session.Saved with { Enabled = false, Style = "normal" }, allowDownload: false);
        _previewTimer.Stop();
        LoadFields(_session.Saved);
        _message.Text = "轻匣的任务栏外观已关闭。";
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (_busy) return;
        _busy = true;
        _body.IsEnabled = false;
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
