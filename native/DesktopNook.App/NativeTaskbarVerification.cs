using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using DesktopNook.Core;

namespace DesktopNook.App;

internal static class NativeTaskbarVerification
{
    internal static async Task<int> Run(string root, string? archive)
    {
        var checks = new List<string>();
        string? failure = null;
        try
        {
            Directory.CreateDirectory(root);
            var before = IndependentProcesses();
            string storage = Path.Combine(root, "Appearance");
            var store = new TaskbarAppearanceStore(storage);
            CacheFixture(store, archive);
            var backend = new NativeTaskbarBackend(store.DirectoryPath, sandbox: true, dryRun: true);
            var productionGuard = new NativeTaskbarBackend(storage, sandbox: true);
            Require(productionGuard.Restriction is not null, "Regular isolated sessions reject live rendering.");
            var session = new TaskbarAppearanceSession(store, backend);
            var clear = new TaskbarAppearanceSettings { Enabled = true, Style = "clear" };
            for (int cycle = 1; cycle <= 3; cycle++)
            {
                await session.PreviewAsync(clear, allowDownload: false);
                Require(session.Running, $"Worker reached ready state in cycle {cycle}.");
                using var process = Process.GetCurrentProcess();
                Require(process.Modules.Cast<ProcessModule>().Any(module =>
                    Path.GetFileName(module.FileName) == "QingxiaTaskbarCore.dll"), $"Module belongs to the WPF host in cycle {cycle}.");
                Require(before.SequenceEqual(IndependentProcesses()), $"No additional TranslucentTB process in cycle {cycle}.");
                await session.CancelPreviewAsync();
                Require(!session.Running && !session.HasPreview, $"Worker stopped after cancelling preview in cycle {cycle}.");
            }

            await session.ApplyAsync(clear, allowDownload: false);
            Require(session.Running && store.Load().UsesComponent, "Applying persists settings and starts the worker.");
            var conflictingStore = new TaskbarAppearanceStore(Path.Combine(root, "ConflictingAppearance"));
            CacheFixture(conflictingStore, archive);
            var conflicting = new NativeTaskbarBackend(conflictingStore.DirectoryPath, sandbox: true, dryRun: true);
            try
            {
                await conflicting.ApplyAsync(clear, allowDownload: false);
                throw new InvalidOperationException("Two workers were allowed to manage the same session.");
            }
            catch (IOException error)
            {
                Require(error.Message.Contains("已有折页", StringComparison.Ordinal) &&
                    !conflicting.Running && session.Running, "A second backend cannot take over an active session.");
            }
            Require(session.Stop() && !session.Running, "Session shutdown stops the worker.");
            await session.StartSavedAsync();
            Require(session.Running, "Saved appearance can start another worker.");
            await session.PreviewAsync(clear with { Style = "opaque" }, allowDownload: false);
            await Task.Delay(450);
            string configuration = Path.Combine(store.DirectoryPath, "Native", "RoamingState", "settings.json");
            using (var generated = JsonDocument.Parse(File.ReadAllBytes(configuration)))
                Require(session.Running && generated.RootElement.GetProperty(TaskbarAppearanceSettings.DesktopScene)
                    .GetProperty("accent").GetString() == "opaque", "Changing style rewrites the native settings while the worker stays active.");
            await session.ApplyAsync(clear with { Enabled = false }, allowDownload: false);
            Require(!session.Running && !store.Load().UsesComponent, "Disabling stops the worker and persists settings.");
            Require(before.SequenceEqual(IndependentProcesses()), "Existing taskbar processes are unchanged.");
        }
        catch (Exception error) { failure = error.ToString(); }

        File.WriteAllText(Path.Combine(root, "native-taskbar-verification.json"),
            JsonSerializer.Serialize(new { hostPid = Environment.ProcessId, dryRun = true, passed = checks.Count, failure, checks },
                new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;

        void Require(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            checks.Add(description);
        }
    }

    internal static async Task<int> RunLive(string root, string? archive)
    {
        Directory.CreateDirectory(root);
        var checks = new List<string>();
        var colors = new Dictionary<string, string[]>();
        string? failure = null;
        var store = new TaskbarAppearanceStore(root);
        var backend = new NativeTaskbarBackend(store.DirectoryPath, sandbox: false);
        var session = new TaskbarAppearanceSession(store, backend);
        try
        {
            CacheFixture(store, archive);
            if (IndependentProcesses().Length != 0) throw new InvalidOperationException("A separate renderer is still active.");
            var before = TakeSamples();
            colors.Add("system", before);
            var opaque = new TaskbarAppearanceSettings { Enabled = true, Style = "opaque", Color = "#D020E0", Alpha = 255, FollowScenes = false };
            await session.ApplyAsync(opaque, allowDownload: false);
            using var host = Process.GetCurrentProcess();
            Check(session.Running && host.Modules.Cast<ProcessModule>().Any(module =>
                Path.GetFileName(module.FileName) == "QingxiaTaskbarCore.dll"), "The actual renderer is loaded in the DesktopNook host.");
            Check(IndependentProcesses().Length == 0, "The renderer starts without a TranslucentTB process.");
            colors.Add("purple", await WaitForColor("#D020E0"));
            Check(true, "The taskbar displays the first selected solid color.");

            await session.PreviewAsync(opaque with { FloatingLayout = true }, allowDownload: false);
            await Task.Delay(1250);
            colors.Add("capsuleEdges", TakeCapsuleEdgeSamples());
            Check(colors["capsuleEdges"].Take(4).All(color => color != "#D020E0") &&
                colors["capsuleEdges"].Skip(4).Take(3).Contains("#D020E0"), "The solid fill stays inside the native capsule.");
            Check(colors["capsuleEdges"][7] == "#D020E0" && colors["capsuleEdges"][8] == "#D020E0",
                "Solid pixels reach the first and last taskbar rows.");
            await session.CancelPreviewAsync();
            await WaitForColor("#D020E0");

            var glass = opaque with { Color = "#B0DBE6", Alpha = 38, BlurRadius = 6 };
            await session.PreviewAsync(glass with { Style = "blur" }, allowDownload: false);
            await Task.Delay(700);
            colors["plainRim"] = TakeSamples(topEdge: true);
            await session.PreviewAsync(glass with { Style = "liquid" }, allowDownload: false);
            await Task.Delay(900);
            colors.Add("liquid", TakeSamples());
            colors["liquidRim"] = TakeSamples(topEdge: true);
            static int Light(string color) => Convert.ToInt32(color[1..3],16) + Convert.ToInt32(color[3..5],16) + Convert.ToInt32(color[5..7],16);
            Check(colors["liquidRim"].Zip(colors["plainRim"]).Count(pair => Light(pair.First) > Light(pair.Second) + 30) >= 5,
                "Liquid glass highlights the rim compared with identical blur and tint.");
            await session.PreviewAsync(opaque with { Style = "frosted", Color = "#B0DBE6", Alpha = 104 }, allowDownload: false);
            await Task.Delay(900);
            colors.Add("frosted", TakeSamples());
            Check(!colors["liquid"].SequenceEqual(colors["frosted"]) &&
                !colors["liquid"].SequenceEqual(colors["purple"]), "The two glass presets change the actual native taskbar.");

            await session.PreviewAsync(opaque with { Color = "#1679D1" }, allowDownload: false);
            colors.Add("blue", await WaitForColor("#1679D1"));
            Check(true, "Changing the style preview updates the actual taskbar.");
            await session.PreviewAsync(opaque with { Style = "clear", Alpha = 0 }, allowDownload: false);
            await Task.Delay(650);
            colors.Add("clear", TakeSamples());
            var fillIndexes = Enumerable.Range(0, before.Length).Where(index => colors["blue"][index] == "#1679D1").ToArray();
            Check(fillIndexes.All(index => colors["clear"][index] != "#1679D1" && colors["clear"][index] != "#D020E0"), "Clear removes the solid taskbar fill.");
            await session.CancelPreviewAsync();
            colors.Add("afterCancel", await WaitForColor("#D020E0"));
            Check(true, "Cancelling the preview reapplies the saved style.");
            await session.ApplyAsync(opaque with { Enabled = false }, allowDownload: false);
            await Task.Delay(650);
            colors.Add("afterStop", TakeSamples());
            Check(!session.Running, "The taskbar worker shut down.");
            Check(fillIndexes.Count(index => colors["afterStop"][index] == before[index]) >= fillIndexes.Length / 2,
                "Disabling restores the system appearance.");
            await session.ApplyAsync(opaque, allowDownload: false);
            colors.Add("afterRestart", await WaitForColor("#D020E0"));
            Check(session.Stop() && !session.Running, "The in-process worker restarts and shuts down again.");
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            if (!session.Stop()) failure ??= "The taskbar worker did not stop.";
            File.WriteAllText(Path.Combine(root, "native-taskbar-live.json"), JsonSerializer.Serialize(
                new { hostPid = Environment.ProcessId, passed = checks.Count, failure, checks, colors }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return failure is null ? 0 : 1;

        void Check(bool success, string name)
        {
            if (!success) throw new InvalidOperationException(name);
            checks.Add(name);
        }

        async Task<string[]> WaitForColor(string expected)
        {
            var samples = Array.Empty<string>();
            for (int attempt = 0; attempt < 18; attempt++)
            {
                await Task.Delay(175);
                samples = TakeSamples();
                if (samples.Count(color => color == expected) > 3) return samples;
            }
            colors["unmatched"] = samples;
            throw new InvalidOperationException($"Expected taskbar color {expected}, found {string.Join(", ", samples)}.");
        }
    }

    private static string[] TakeSamples(bool topEdge = false)
    {
        var window = FindWindowW("Shell_TrayWnd", null);
        if (window == IntPtr.Zero || !GetWindowRect(window, out var bounds)) throw new IOException("No taskbar in the interactive session.");
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) throw new IOException("Couldn't read the desktop display.");
        try
        {
            var result = new string[10];
            for (int index = 0; index < result.Length; index++)
            {
                int x = bounds.Left + (int)((bounds.Right - bounds.Left) * (0.08 + 0.025 * index));
                int y = topEdge ? bounds.Top + 1 : bounds.Top + (bounds.Bottom - bounds.Top) / 2;
                uint bgr = GetPixel(dc, x, y);
                if (bgr == uint.MaxValue) throw new IOException("Couldn't sample the taskbar.");
                result[index] = $"#{bgr & 255:X2}{(bgr >> 8) & 255:X2}{(bgr >> 16) & 255:X2}";
            }
            return result;
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    private static string[] TakeCapsuleEdgeSamples()
    {
        nint shell = FindWindowW("Shell_TrayWnd", null);
        nint bridge = FindWindowEx(shell, 0, "Windows.UI.Composition.DesktopWindowContentBridge", null);
        if (bridge == 0 || !GetWindowRect(bridge, out var child))
            throw new IOException("Could not locate the original Explorer composition child.");
        int right = child.Right - (int)GetProp(bridge, NativeTaskbarShape.TrimAppliedProperty);
        nint dc = GetDC(0);
        if (dc == 0) throw new IOException("Could not sample the capsule edge.");
        try
        {
            int middle = (child.Top + child.Bottom) / 2;
            return new[] { (child.Left - 1, middle), (right, middle), (child.Left + 2, child.Top + 5),
                (right - 3, child.Bottom - 6), (child.Left + 5, middle), (right - 6, middle), ((child.Left + right) / 2, middle),
                ((child.Left + right) / 2, child.Top), ((child.Left + right) / 2, child.Bottom - 1) }
                .Select(point =>
                {
                    uint bgr = GetPixel(dc, point.Item1, point.Item2);
                    return $"#{bgr & 255:X2}{(bgr >> 8) & 255:X2}{(bgr >> 16) & 255:X2}";
                }).ToArray();
        }
        finally { ReleaseDC(0, dc); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint window, string name);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr dc, int x, int y);

    private static void CacheFixture(TaskbarAppearanceStore store, string? archive)
    {
        if (archive is null) throw new IOException("验收需要指定任务栏组件压缩包。");
        var cache = new TaskbarAppearancePackage(store.DirectoryPath).ArchivePath;
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.Copy(archive, cache, overwrite: false);
    }

    private static int[] IndependentProcesses()
    {
        var processes = Process.GetProcessesByName("TranslucentTB");
        try { return processes.Select(process => process.Id).Order().ToArray(); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
