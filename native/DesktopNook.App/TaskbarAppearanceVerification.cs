using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopNook.Core;

namespace DesktopNook.App;

internal static class TaskbarAppearanceVerification
{
    private sealed class ArchiveHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(response(request));
    }

    private sealed class ProbeBackend : ITaskbarAppearanceBackend
    {
        public TaskbarAppearanceSettings? LastAppearance { get; private set; }
        public string? Restriction => null;
        public bool NeedsDownload => false;
        public bool Running => LastAppearance?.UsesComponent == true;

        public string? RejectStyle { get; set; }
        public Task ApplyAsync(TaskbarAppearanceSettings settings, bool allowDownload)
        {
            if (settings.Style == RejectStyle) throw new IOException("模拟组件无法重读上次的样式。");
            LastAppearance = settings;
            return Task.CompletedTask;
        }
        public bool Stop() { LastAppearance = null; return true; }
    }

    internal static async Task Run(MainWindow window, string root, string? archivePath)
    {
        var checks = new List<string>();
        string failure = "";
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks.Add(name);
        }

        try
        {
            if (!App.IsSandbox) throw new InvalidOperationException("任务栏外观验证必须使用沙箱。");
            window.KeepOpenForVerification = true;
            window.FinishPanelMotion();
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
            var store = new TaskbarAppearanceStore(App.Store.DataDirectory);
            var probe = new ProbeBackend();
            var session = new TaskbarAppearanceSession(store, probe);
            var clear = new TaskbarAppearanceSettings { Enabled = true, Style = "clear", Color = "#123456", Alpha = 110 };
            var opaque = clear with { Style = "opaque" };
            Check(!session.Saved.Enabled && !File.Exists(store.SettingsPath), "首次使用默认关闭且没有外观配置写入");
            await session.PreviewAsync(clear, true);
            Check(probe.LastAppearance == clear && !File.Exists(store.SettingsPath), "预览调用组件而不保存设置");
            await session.CancelPreviewAsync();
            Check(!probe.Running && !session.HasPreview, "取消首次预览恢复默认");
            await session.ApplyAsync(clear, true);
            Check(store.Load() == clear, "应用后重读得到相同外观");
            await session.PreviewAsync(opaque, false);
            await session.CancelPreviewAsync();
            Check(probe.LastAppearance == clear && store.Load() == clear, "取消预览恢复已经应用的颜色与样式");
            await session.PreviewAsync(opaque, false);
            probe.RejectStyle = "clear";
            bool couldNotRestore = false;
            try { await session.CancelPreviewAsync(); } catch (IOException) { couldNotRestore = true; }
            Check(couldNotRestore && !session.HasPreview && !probe.Running && store.Load() == clear,
                "上次样式恢复失败时退出自有组件并保留配置");
            probe.RejectStyle = null;
            var launchSettings = Path.Combine(root, "test-backend", "settings.json");
            TaskbarAppearanceStore.WriteComponentSettings(launchSettings, opaque);
            using (var json = JsonDocument.Parse(File.ReadAllText(launchSettings)))
            {
                var properties = json.RootElement;
                Check(properties.GetProperty("desktop_appearance").GetProperty("color").GetString() == "#1234566E" &&
                    !properties.GetProperty("maximized_window_appearance").GetProperty("enabled").GetBoolean() &&
                    properties.GetProperty("disable_saving").GetBoolean() && properties.GetProperty("hide_tray").GetBoolean() &&
                    !properties.GetProperty("copy_dlls").GetBoolean(),
                    "纯色配置保留原 RGBA，完全不透明由组件渲染时处理");
            }
            using var rules = JsonDocument.Parse("""
                {"process_name":{"notepad.exe":{"accent":"blur","blur_radius":18.5,"inactive":{"accent":"clear","color":"#2018"}}}}
                """);
            using var ignored = JsonDocument.Parse("""{"window_title":["不参与检测"],"window_class":["ExampleWindow"]}""");
            var full = new TaskbarAppearanceSettings { Enabled = true, Style = "normal", ShowLine = true, IgnoredWindows = ignored.RootElement.Clone() }
                .WithProfile("visible_window_appearance", new TaskbarAppearanceProfile
                {
                    Enabled = true, Style = "acrylic", Color = "#884433", Alpha = 0xAA, ShowLine = false, Rules = rules.RootElement.Clone()
                })
                .WithProfile("maximized_window_appearance", new TaskbarAppearanceProfile
                {
                    Enabled = true, Style = "blur", BlurRadius = 22.5, ShowPeek = false
                })
                .WithProfile("start_opened_appearance", new TaskbarAppearanceProfile { Enabled = true, Style = "clear" })
                .WithProfile("search_opened_appearance", new TaskbarAppearanceProfile { Enabled = true, Style = "opaque" })
                .WithProfile("task_view_opened_appearance", new TaskbarAppearanceProfile { Enabled = true, Style = "normal" })
                .WithProfile("battery_saver_appearance", new TaskbarAppearanceProfile { Enabled = true, Style = "acrylic" });
            var visible = full.ProfileFor("visible_window_appearance");
            var maximized = full.ProfileFor("maximized_window_appearance");
            Check(full.UsesComponent && full.Profiles?.Count == 6 && full.Profiles.Values.All(profile => profile.Enabled) &&
                full.Style == "normal" && full.ShowLine == true && visible.Style == "acrylic" && visible.Alpha == 0xAA &&
                maximized.Style == "blur" && maximized.BlurRadius == 22.5 && maximized.ShowPeek == false,
                "手动设置桌面及六种动态场景，保留模糊与亚克力、细线和旧式开关");
            store.Save(full);
            var reloaded = store.Load();
            TaskbarAppearanceStore.WriteComponentSettings(launchSettings, reloaded);
            using (var json = JsonDocument.Parse(File.ReadAllText(launchSettings)))
            {
                var emitted = json.RootElement;
                var profile = emitted.GetProperty("visible_window_appearance");
                Check(profile.GetProperty("accent").GetString() == "acrylic" &&
                    !profile.GetProperty("show_line").GetBoolean() &&
                    profile.GetProperty("rules").GetProperty("process_name").GetProperty("notepad.exe")
                        .GetProperty("inactive").GetProperty("accent").GetString() == "clear" &&
                    emitted.GetProperty("ignored_windows").GetProperty("window_class")[0].GetString() == "ExampleWindow" &&
                    emitted.GetProperty("maximized_window_appearance").GetProperty("blur_radius").GetDouble() == 22.5,
                    "手动编辑的规则、非活动外观和忽略列表可重读并传给渲染组件");
            }
            Check((full with { Style = "normal", ShowLine = null }).UsesComponent &&
                !(full with { Enabled = false }).UsesComponent, "桌面为系统默认时可仅启用动态外观，全局关闭保留设置");
            var selectedPreview = full.ForPreview("maximized_window_appearance");
            Check(selectedPreview.Style == "blur" && selectedPreview.BlurRadius == 22.5 && selectedPreview.Profiles is null &&
                full.ProfileFor("visible_window_appearance").Rules is not null,
                "所选场景可单独预览，已配置的动态规则保持");
            var reproduction = (clear with { Style = "blur", BlurRadius = 9 })
                .WithProfile("maximized_window_appearance", new TaskbarAppearanceProfile { Enabled = true, Style = "clear" });
            var uniform = reproduction.ForUniformAppearance(TaskbarAppearanceSettings.DesktopScene);
            TaskbarAppearanceStore.WriteComponentSettings(launchSettings, uniform);
            using (var document = JsonDocument.Parse(File.ReadAllText(launchSettings)))
            {
                Check(document.RootElement.GetProperty("desktop_appearance").GetProperty("accent").GetString() == "blur" &&
                    TaskbarAppearanceSettings.Scenes.Skip(1).All(scene =>
                        !document.RootElement.GetProperty(scene.Key).GetProperty("enabled").GetBoolean()) &&
                    uniform.ProfileFor("maximized_window_appearance").Enabled,
                    "统一模糊在组件侧关闭场景覆盖，最大化透明仍保存在一处设置");
            }
            var maximizedPreview = reproduction.ForPreview("maximized_window_appearance");
            var maximizedUniform = reproduction.ForUniformAppearance("maximized_window_appearance");
            Check(maximizedPreview.ShowLine == true && maximizedPreview.ShowPeek == true && maximizedPreview.BlurRadius == 9 &&
                maximizedUniform.ShowLine == true && maximizedUniform.ShowPeek == true &&
                full.WithProfile("maximized_window_appearance", maximized with { ShowLine = false })
                    .ForPreview("maximized_window_appearance").ShowLine == false,
                "预览和设为统一外观均保留来源场景的细线、Peek 与模糊默认值，显式隐藏不被改写");
            Check(clear.ProfileFor("task_view_opened_appearance").Enabled == false,
                "现有一处配置在升级后不会自动开启未设置的场景");
            TaskbarAppearanceStore.WriteComponentSettings(launchSettings, uniform with { FollowScenes = true });
            using (var document = JsonDocument.Parse(File.ReadAllText(launchSettings)))
            {
                var max = document.RootElement.GetProperty("maximized_window_appearance");
                Check(max.GetProperty("enabled").GetBoolean() && max.GetProperty("accent").GetString() == "clear",
                    "重新启用场景切换后恢复原最大化透明规则");
            }
            try { _ = full.WithProfile("maximized_window_appearance", maximized with { BlurRadius = 751 });
                throw new InvalidOperationException("超限模糊数值被接受"); }
            catch (IOException) { checks.Add("超出引擎范围的模糊数值被拒绝"); }
            using (var badRule = JsonDocument.Parse("""{"process_name":{"a.exe":{"inactive":{"blur_radius":-1}}}}"""))
            {
                try { TaskbarAppearanceFilters.ValidateRules(badRule.RootElement); throw new InvalidOperationException("错误窗口规则被接受"); }
                catch (IOException) { checks.Add("单条窗口规则和非活动子规则均验证范围"); }
            }
            store.Save(clear);

            var sandboxBackend = new NativeTaskbarBackend(store.DirectoryPath, sandbox: true);
            try { await sandboxBackend.ApplyAsync(clear, true); throw new InvalidOperationException("沙箱未拦截实际组件"); }
            catch (IOException) { checks.Add("沙箱禁止下载和运行会话外观组件"); }

            if (archivePath is not null)
            {
                var extracted = TaskbarAppearancePackage.VerifyAndExtract(archivePath, Path.Combine(root, "package"));
                var module = Path.Combine(extracted, "QingxiaTaskbarCore.dll");
                Check(File.Exists(module) && !File.Exists(Path.Combine(extracted, "TranslucentTB.exe")),
                    "发行组件只包含进程内使用的动态库");
                TaskbarAppearancePackage.VerifyAndExtract(archivePath, extracted);
                File.AppendAllText(module, "tamper");
                try { TaskbarAppearancePackage.VerifyAndExtract(archivePath, extracted); throw new InvalidOperationException("组件篡改未拒绝"); }
                catch (IOException) { checks.Add("已解压组件有变化时拒绝运行"); }

                var download = new TaskbarAppearancePackage(Path.Combine(root, "cache-download"));
                using var transport = new HttpClient(new ArchiveHandler(request =>
                {
                    var expectedPath = $"/{OnlineUpdater.Repository}/releases/download/{TaskbarAppearancePackage.ReleaseTag}/{TaskbarAppearancePackage.PackageFile}";
                    Check(request.RequestUri?.Host == "github.com" && request.RequestUri.AbsolutePath == expectedPath,
                        "从固定版本发行页下载任务栏组件");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(archivePath)) };
                }));
                try { await download.PrepareAsync(allowDownload: false); throw new InvalidOperationException("启动阶段自动下载安装组件"); }
                catch (IOException) { checks.Add("启动阶段保留显式下载授权"); }
                await download.DownloadAsync(transport);
                await download.PrepareAsync(allowDownload: false);
                Check(SHA256.HashData(File.ReadAllBytes(download.ArchivePath)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(archivePath))),
                    "通过摘要校验的组件可以离线复用");
                var damagedBytes = File.ReadAllBytes(archivePath);
                damagedBytes[^1] ^= 1;
                var damaged = new TaskbarAppearancePackage(Path.Combine(root, "damaged-download"));
                using var wrongBytes = new HttpClient(new ArchiveHandler(_ =>
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(damagedBytes) }));
                try { await damaged.DownloadAsync(wrongBytes); throw new InvalidOperationException("损坏下载未拒绝"); }
                catch (IOException)
                {
                    Check(!damaged.HasArchive && !Directory.EnumerateFiles(Path.GetDirectoryName(damaged.ArchivePath)!).Any(),
                        "下载文件长度相同但摘要错误时拒绝并移除半成品");
                }
            }

            Check(MainWindow.CurrentVersionLabel == "v0.19.0", "正式候选在更新菜单显示完整版本");

            var originalExe = Environment.ProcessPath!;
            var target = Path.Combine(root, "update-target.exe");
            File.Copy(originalExe, target);
            var update = LocalUpdater.Stage(originalExe, target, App.Store.DataDirectory, root, 0, App.Options, App.Store.State);
            var backup = Path.Combine(Path.GetDirectoryName(update.Plan)!, "metadata-backup", "TaskbarAppearance", "settings.json");
            Check(File.ReadAllBytes(backup).SequenceEqual(File.ReadAllBytes(store.SettingsPath)), "更新备份保留任务栏外观配置");

            UiMotion.Configure("off");
            var ownerButton = Find<Button>(window).Single(b => AutomationProperties.GetName(b) == "更多操作");
            ownerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var menu = window.LastMenu ?? throw new InvalidOperationException("无法打开更多菜单。");
            var entry = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "任务栏外观…"));
            menu.IsOpen = false;
            Exception? dialogFailure = null;
            _ = window.Dispatcher.BeginInvoke(() =>
            {
                var dialog = Application.Current.Windows.OfType<TaskbarAppearanceWindow>().SingleOrDefault();
                try
                {
                    if (dialog is null) throw new InvalidOperationException("未打开任务栏外观设置。");
                    dialog.UpdateLayout();
                    Check(Find<TextBlock>(dialog).Any(text => text.Text.Contains("隔离模式", StringComparison.Ordinal)),
                        "从更多菜单打开外观界面，沙箱显示运行限制");
                    var buttons = Find<Button>(dialog).Select(button => button.Content as string).OfType<string>().ToList();
                    Check(!buttons.Any(text => text.Contains("导入", StringComparison.Ordinal)) &&
                        buttons.Contains("选颜色…") && buttons.Contains("恢复系统外观"),
                        "任务栏外观没有导入入口，保留手动选色和恢复");

                    var scenes = Find<ComboBox>(dialog).Single(item => AutomationProperties.GetAutomationId(item) == "AppearanceScene");
                    var styles = Find<ComboBox>(dialog).Single(item => AutomationProperties.GetAutomationId(item) == "AppearanceStyle");
                    var blur = Find<Slider>(dialog).Single(item => AutomationProperties.GetAutomationId(item) == "AppearanceBlurRadius");
                    var enabled = Find<CheckBox>(dialog).Single(item => AutomationProperties.GetAutomationId(item) == "AppearanceSceneEnabled");
                    Check(scenes.Items.Count == 7 && styles.Items.Count == 5, "设置窗口列出七种场景与五种外观");
                    scenes.SelectedIndex = 2;
                    styles.SelectedIndex = 3;
                    blur.Value = 34;
                    Check(enabled.IsChecked == true, "直接选择未启用场景的外观会启用该场景");
                    scenes.SelectedIndex = 0;
                    Check(styles.SelectedIndex == 1, "修改最大化场景后，桌面样式独立保持");
                    scenes.SelectedIndex = 2;
                    Check(blur.Value == 34 && styles.SelectedIndex == 3 && enabled.IsChecked == true,
                        "切换场景保留未应用的模糊半径、样式及启用开关");
                    dialog.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(dialog);
                    var screenshot = new PngBitmapEncoder();
                    screenshot.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(root, "taskbar-appearance-preview.png"));
                    screenshot.Save(file);
                }
                catch (Exception ex) { dialogFailure = ex; }
                finally { dialog?.Close(); }
            }, DispatcherPriority.ContextIdle);
            entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if (dialogFailure is not null) throw dialogFailure;

            var uniformStore = new TaskbarAppearanceStore(Path.Combine(root, "uniform-ui"));
            uniformStore.Save(reproduction);
            var uniformProbe = new ProbeBackend();
            var uniformSession = new TaskbarAppearanceSession(uniformStore, uniformProbe);
            var uniformDialog = new TaskbarAppearanceWindow(window, uniformSession);
            _ = window.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    uniformDialog.UpdateLayout();
                    Check(Find<TextBlock>(uniformDialog).Any(text => text.Text.Contains("窗口最大化") &&
                        text.Text.Contains("替代桌面外观")), "桌面页面明确提示可能覆盖桌面的场景");
                    var button = Find<Button>(uniformDialog).Single(item => AutomationProperties.GetAutomationId(item) == "AppearanceUniform");
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var saved = uniformStore.Load();
                    Check(!saved.FollowScenes && saved.Style == "blur" && saved.ProfileFor("maximized_window_appearance").Enabled &&
                        uniformProbe.LastAppearance?.FollowScenes == false,
                        "界面一键应用统一模糊并保存，原最大化场景保持可恢复");
                }
                catch (Exception ex) { dialogFailure = ex; }
                finally { uniformDialog.Close(); }
            }, DispatcherPriority.ContextIdle);
            uniformDialog.ShowDialog();
            if (dialogFailure is not null) throw dialogFailure;
        }
        catch (Exception ex) { failure = ex.ToString(); }
        File.WriteAllText(Path.Combine(root, "taskbar-appearance-verification.json"),
            JsonSerializer.Serialize(new { passed = checks.Count, failure, checks }, new JsonSerializerOptions { WriteIndented = true }));
        Application.Current.Shutdown(failure.Length == 0 ? 0 : 1);
    }

    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var descendant in Find<T>(child)) yield return descendant;
        }
    }
}
