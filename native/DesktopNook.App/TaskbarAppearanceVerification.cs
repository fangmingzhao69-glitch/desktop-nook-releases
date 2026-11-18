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
                Check(properties.GetProperty("desktop_appearance").GetProperty("color").GetString() == "#123456FF" &&
                    !properties.GetProperty("maximized_window_appearance").GetProperty("enabled").GetBoolean() &&
                    properties.GetProperty("disable_saving").GetBoolean() && properties.GetProperty("hide_tray").GetBoolean() &&
                    !properties.GetProperty("copy_dlls").GetBoolean(),
                    "纯色编码与上游格式一致，使用已校验的独立组件目录");
            }
            var original = Path.Combine(root, "import.json");
            const string import = "{ /* comment */ \"desktop_appearance\":{\"accent\":\"clear\",\"color\":\"#1234\",},}";
            File.WriteAllText(original, import);
            var imported = TaskbarAppearanceSettings.Import(original);
            Check(imported.Color == "#112233" && imported.Alpha == 0x44 && File.ReadAllText(original) == import,
                "只读导入上游简写 RGBA、注释及末尾逗号");

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
            Check(MainWindow.CurrentVersionLabel == "v0.17.0", "正式候选在更新菜单显示完整版本");

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
