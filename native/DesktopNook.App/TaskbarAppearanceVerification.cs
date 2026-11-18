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
        public bool UsesCompatibilityPackage => false;
        public string? RejectStyle { get; set; }
        public Task ImportCompatibilityPackageAsync(string source) => throw new NotSupportedException();
        public Task ApplyAsync(TaskbarAppearanceSettings settings, bool allowDownload)
        {
            if (settings.Style == RejectStyle) throw new IOException("模拟组件无法重读上次的样式。");
            LastAppearance = settings;
            return Task.CompletedTask;
        }
        public bool Stop() { LastAppearance = null; return true; }
    }

    internal static async Task Run(MainWindow window, string root, string? archivePath, string? compatibilityArchive)
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
            var original = Path.Combine(root, "import.json");
            const string import = "{ /* comment */ \"desktop_appearance\":{\"accent\":\"clear\",\"color\":\"#1234\",},}";
            File.WriteAllText(original, import);
            var imported = TaskbarAppearanceSettings.Import(original);
            Check(imported.Color == "#112233" && imported.Alpha == 0x44 && File.ReadAllText(original) == import,
                "只读导入上游简写 RGBA、注释及末尾逗号");
            Check(imported.ProfileFor("task_view_opened_appearance") is { Enabled: true, Style: "normal", ShowLine: true } &&
                imported.ProfileFor("maximized_window_appearance") is { Enabled: false, Style: "acrylic", ShowLine: true } &&
                imported.ShowLine == false && imported.ShowPeek == false,
                "导入缺少的场景时采用原组件的 Windows 11 默认值");

            const string partial = """
                {"desktop_appearance":{"color":" #abcd "},
                 "maximized_window_appearance":{"enabled":true,"accent":"normal","show_line":false,"color":"#00000000"},
                 "visible_window_appearance":{"blur_radius":760}}
                """;
            File.WriteAllText(original, partial);
            var matched = TaskbarAppearanceSettings.Import(original);
            TaskbarAppearanceStore.WriteComponentSettings(launchSettings, matched);
            using (var parsed = JsonDocument.Parse(File.ReadAllText(launchSettings)))
            {
                var data = parsed.RootElement;
                var desktop = data.GetProperty("desktop_appearance");
                var maximum = data.GetProperty("maximized_window_appearance");
                Check(desktop.GetProperty("accent").GetString() == "clear" &&
                    desktop.GetProperty("color").GetString() == "#AABBCCDD" &&
                    maximum.GetProperty("enabled").GetBoolean() && !maximum.GetProperty("show_line").GetBoolean() &&
                    data.GetProperty("task_view_opened_appearance").GetProperty("enabled").GetBoolean() &&
                    data.GetProperty("visible_window_appearance").GetProperty("blur_radius").GetDouble() == 750,
                    "原配置含缺省字段、显式隐藏细线及超上限模糊半径时按原组件规则转换");
            }

            const string fullImport = """
                {
                  "desktop_appearance":{"accent":"normal","show_line":true},
                  "visible_window_appearance":{"enabled":true,"accent":"acrylic","color":"#884433AA","show_line":false,
                    "rules":{"process_name":{"notepad.exe":{"accent":"blur","blur_radius":18.5,"inactive":{"accent":"clear","color":"#2018"}}}}},
                  "maximized_window_appearance":{"enabled":true,"accent":"blur","blur_radius":22.5,"show_peek":false},
                  "start_opened_appearance":{"enabled":true,"accent":"clear"},
                  "search_opened_appearance":{"enabled":true,"accent":"opaque"},
                  "task_view_opened_appearance":{"enabled":true,"accent":"normal"},
                  "battery_saver_appearance":{"enabled":true,"accent":"acrylic"},
                  "ignored_windows":{"window_title":["不参与检测"],"window_class":["ExampleWindow"]}
                }
                """;
            File.WriteAllText(original, fullImport);
            var full = TaskbarAppearanceSettings.Import(original) with { Enabled = true };
            var visible = full.ProfileFor("visible_window_appearance");
            var maximized = full.ProfileFor("maximized_window_appearance");
            Check(full.UsesComponent && full.Profiles?.Count == 6 && full.Profiles.Values.All(profile => profile.Enabled) &&
                full.Style == "normal" && full.ShowLine == true && visible.Style == "acrylic" && visible.Alpha == 0xAA &&
                maximized.Style == "blur" && maximized.BlurRadius == 22.5 && maximized.ShowPeek == false,
                "导入并保留桌面及六种动态场景、模糊与亚克力、细线和旧式开关");
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
                    emitted.GetProperty("maximized_window_appearance").GetProperty("blur_radius").GetDouble() == 22.5 &&
                    File.ReadAllText(original) == fullImport,
                    "规则、非活动外观和忽略列表可重读并传给原渲染组件，原配置保持原样");
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
                    "统一模糊在组件侧关闭场景覆盖，最大化透明仍保存在轻匣设置");
            }
            var maximizedPreview = reproduction.ForPreview("maximized_window_appearance");
            var maximizedUniform = reproduction.ForUniformAppearance("maximized_window_appearance");
            Check(maximizedPreview.ShowLine == true && maximizedPreview.ShowPeek == true && maximizedPreview.BlurRadius == 9 &&
                maximizedUniform.ShowLine == true && maximizedUniform.ShowPeek == true &&
                matched.ForPreview("maximized_window_appearance").ShowLine == false,
                "预览和设为统一外观均保留来源场景的细线、Peek 与模糊默认值，显式隐藏不被改写");
            Check(clear.ProfileFor("task_view_opened_appearance").Enabled == false,
                "现有轻匣配置在升级后不会自动开启未设置的场景");
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

            var sandboxBackend = new TranslucentTaskbarBackend(new TaskbarAppearancePackage(store.DirectoryPath), sandbox: true);
            try { await sandboxBackend.ApplyAsync(clear, true); throw new InvalidOperationException("沙箱未拦截实际组件"); }
            catch (IOException) { checks.Add("沙箱禁止下载和运行会话外观组件"); }

            if (archivePath is not null)
            {
                var extracted = TaskbarAppearancePackage.VerifyAndExtract(archivePath, Path.Combine(root, "package"));
                Check(File.Exists(Path.Combine(extracted, "TranslucentTB.exe")), "核验并解压真实官方 ZIP");
                TaskbarAppearanceStore.WriteComponentSettings(Path.Combine(extracted, "settings.json"), clear);
                TaskbarAppearancePackage.VerifyAndExtract(archivePath, extracted);
                File.AppendAllText(Path.Combine(extracted, "TranslucentTB.exe"), "tamper");
                try { TaskbarAppearancePackage.VerifyAndExtract(archivePath, extracted); throw new InvalidOperationException("组件篡改未拒绝"); }
                catch (IOException) { checks.Add("已解压组件有变化时拒绝运行"); }
                var wrong = Path.Combine(root, "invalid.zip");
                File.WriteAllText(wrong, "not an official package");
                try { TaskbarAppearancePackage.VerifyAndExtract(wrong, Path.Combine(root, "invalid")); throw new InvalidOperationException("错误 ZIP 未拒绝"); }
                catch (IOException) { checks.Add("不匹配发行摘要的 ZIP 在解压前被拒绝"); }
            }

            if (compatibilityArchive is not null)
            {
                var compatibility = new TaskbarAppearancePackage(store.DirectoryPath, useCompatibilityPackage: true);
                compatibility.ImportCompatibilityPackage(compatibilityArchive);
                string directory = await compatibility.PrepareAsync(allowDownload: false);
                Check(File.Exists(Path.Combine(directory, "ExplorerTAP.dll")), "导入并核验 26340 兼容组件，未启动进程");
                if (archivePath is not null)
                {
                    try { compatibility.ImportCompatibilityPackage(archivePath); throw new InvalidOperationException("误接受未修复 ZIP"); }
                    catch (IOException) { checks.Add("兼容组件入口拒绝将原版 ZIP 误作修复版"); }
                    await compatibility.PrepareAsync(allowDownload: false);
                }

                const string expectedPath = "/fangmingzhao69-glitch/desktop-nook-releases/releases/download/v0.17.0/TranslucentTB-2026.2-26340.zip";
                var download = new TaskbarAppearancePackage(Path.Combine(root, "compatibility-download"), useCompatibilityPackage: true);
                using var transport = new HttpClient(new ArchiveHandler(request =>
                {
                    Check(request.RequestUri?.Host == "github.com" && request.RequestUri.AbsolutePath == expectedPath,
                        "兼容组件从固定的 0.17.0 发行地址请求");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(compatibilityArchive)) };
                }));
                try { await download.PrepareAsync(allowDownload: false); throw new InvalidOperationException("启动阶段自动下载安装组件"); }
                catch (IOException) { checks.Add("启动阶段保留显式下载授权"); }
                await download.DownloadAsync(transport);
                await download.PrepareAsync(allowDownload: false);
                Check(SHA256.HashData(File.ReadAllBytes(download.ArchivePath)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(compatibilityArchive))),
                    "兼容组件下载按固定版本校验并可离线复用");
                var damagedPackage = File.ReadAllBytes(compatibilityArchive);
                damagedPackage[^1] ^= 1;
                var damaged = new TaskbarAppearancePackage(Path.Combine(root, "compatibility-damaged"), useCompatibilityPackage: true);
                using var wrongBytes = new HttpClient(new ArchiveHandler(_ =>
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(damagedPackage) }));
                try { await damaged.DownloadAsync(wrongBytes); throw new InvalidOperationException("损坏下载未拒绝"); }
                catch (IOException)
                {
                    Check(!damaged.HasArchive && !Directory.EnumerateFiles(Path.GetDirectoryName(damaged.ArchivePath)!).Any(),
                        "下载文件长度相同但摘要错误时拒绝并移除半成品");
                }
            }

            var minimum = new Version(8, 2501, 31001, 0);
            Check(TaskbarAppearanceDependencies.MeetsMinimum("Microsoft.UI.Xaml.2.8_8.2501.31001.0_x64__8wekyb3d8bbwe", minimum) &&
                !TaskbarAppearanceDependencies.MeetsMinimum("Microsoft.UI.Xaml.2.8_8.2501.31001.0_x86__8wekyb3d8bbwe", minimum) &&
                !TaskbarAppearanceDependencies.MeetsMinimum("Microsoft.UI.Xaml.2.8_8.2401.31001.0_x64__8wekyb3d8bbwe", minimum),
                "依赖检查接受满足版本的 x64 包，并拒绝 x86 和过旧版本");
            Check(MainWindow.CurrentVersionLabel == "v0.17.3", "正式候选在更新菜单显示完整版本");

            var originalExe = Environment.ProcessPath!;
            var target = Path.Combine(root, "update-target.exe");
            File.Copy(originalExe, target);
            var update = LocalUpdater.Stage(originalExe, target, App.Store.DataDirectory, root, 0, App.Options, App.Store.State);
            var backup = Path.Combine(Path.GetDirectoryName(update.Plan)!, "metadata-backup", "TaskbarAppearance", "settings.json");
            Check(File.ReadAllBytes(backup).SequenceEqual(File.ReadAllBytes(store.SettingsPath)), "更新备份保留任务栏外观配置");

            UiMotion.Configure("off");
            var ownerButton = Find<Button>(window.NotesHost).Single(b => AutomationProperties.GetName(b) == "更多操作");
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
