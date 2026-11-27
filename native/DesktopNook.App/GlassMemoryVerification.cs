using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace DesktopNook.App;

internal static class GlassMemoryVerification
{
    internal static async Task RunPanel(MainWindow main, string root, string theme, int cycles)
    {
        var samples = new List<object>(); string? failure = null; Window? board = null;
        try
        {
            if (!App.IsSandbox || theme is not ("glass" or "grape") || cycles is < 40 or > 200)
                throw new ArgumentException("Invalid panel memory probe");
            main.KeepOpenForVerification = true; UiMotion.Configure("on");
            for (int i = 0; i < 58; i++)
            {
                var path = Path.Combine(App.Store.DesktopRoots[0], "合成入口" + i + ".url");
                File.WriteAllText(path, "[InternetShortcut]\r\nURL=https://example.invalid/\r\n");
                App.Store.Collect(path, "office");
            }
            main.Width = 1080; main.Height = 620; main.Render();
            string stateHash = LocalUpdater.Hash(Path.Combine(App.Store.DataDirectory, "state.json"));
            board = new Window { Left = main.Left - 80, Top = main.Top - 80, Width = main.Width + 160, Height = main.Height + 160,
                WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Topmost = true, Background = Brushes.CadetBlue };
            board.Show(); GroupPreviewVerification.PlaceBehind(board, main); main.Activate();
            ThemePalette.Apply("light", theme); await Task.Delay(2000); Sample("first-open");
            for (int i = 1; i <= cycles; i++)
            {
                main.TogglePanel(); await Task.Delay(650);
                main.ShowPanel(); await Task.Delay(650);
                if (i % 10 == 0) Sample("cycle-" + i);
                if (i is 10 or 40 || i == cycles)
                {
                    main.TogglePanel();
                    using var process = Process.GetCurrentProcess();
                    var cpu = process.TotalProcessorTime;
                    await Task.Delay(15000); process.Refresh();
                    Sample("idle-15s-after-" + i, (process.TotalProcessorTime - cpu).TotalMilliseconds);
                    if (main.GlassMaterial is { Running: true } || main.GlassMaterial is { BufferBytes: > 0 } ||
                        main.GlassMaterial is { CaptureSuspended: false } || main.GlassBackdropImage.Source is not null)
                        throw new Exception("Hidden panel retained an active capture or pixel buffer");
                    if (i < cycles) { main.ShowPanel(); await Task.Delay(650); }
                }
            }
            if (theme == "glass" && main.GlassMaterial?.CaptureInstances != 1)
                throw new Exception("Panel did not reuse exactly one capture instance");
            if (LocalUpdater.Hash(Path.Combine(App.Store.DataDirectory, "state.json")) != stateHash)
                throw new Exception("Synthetic collection metadata changed");
            // Allow startup caches to warm, then bound additional idle growth
            // over the remaining 60 cycles. Never force GC or trim the process.
            if (cycles >= 100)
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(samples));
                long warm = doc.RootElement.EnumerateArray().Single(row => row.GetProperty("stage").GetString() == "idle-15s-after-40").GetProperty("privateBytes").GetInt64();
                long end = doc.RootElement.EnumerateArray().Last().GetProperty("privateBytes").GetInt64();
                if (end - warm > 32L * 1024 * 1024) throw new Exception("Private commit kept growing after the warm-up checkpoint");
            }
        }
        catch (Exception error) { failure = error.ToString(); }
        finally { Write(); board?.Close(); Application.Current.Shutdown(failure is null ? 0 : 1); }
        void Sample(string stage, double idleCpuMs = 0)
        {
            using var process = Process.GetCurrentProcess(); process.Refresh();
            samples.Add(new { stage, privateBytes = process.PrivateMemorySize64, workingSet = process.WorkingSet64,
                managedBytes = GC.GetTotalMemory(false), handles = process.HandleCount,
                captureInstances = main.GlassMaterial?.CaptureInstances ?? 0, captureBufferBytes = main.GlassMaterial?.BufferBytes ?? 0,
                captureRunning = main.GlassMaterial?.Running ?? false, captureSuspended = main.GlassMaterial?.CaptureSuspended ?? true,
                animationBufferBytes = main.PanelMotion.BufferedBytes, iconCacheBytes = ShellIntegration.CachedIconBytes,
                main.ContentParked, idleCpuMs });
            Write();
        }
        void Write() => File.WriteAllText(Path.Combine(root, "glass-panel-memory-verification.json"),
            JsonSerializer.Serialize(new { success = failure is null, theme, cycles, samples, failure }, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static async Task RunCapture(string root, string mode, int cycles)
    {
        Directory.CreateDirectory(root);
        var samples = new List<object>(); string? failure = null; int frames = 0;
        var board = new Window { Width = 1000, Height = 680, Left = 60, Top = 60, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, Topmost = true, Background = Brushes.CadetBlue };
        GlassBackdropCapture? reusable = null;
        try
        {
            if (mode is not ("churn" or "reuse" or "suspend") || cycles is < 1 or > 500) throw new ArgumentException("Invalid capture memory probe");
            board.Show(); await Task.Delay(400); Sample("before");
            if (mode != "churn") reusable = new GlassBackdropCapture();
            for (int i = 0; i < cycles; i++)
            {
                var capture = reusable ?? new GlassBackdropCapture();
                try
                {
                    board.Background = i % 2 == 0 ? Brushes.CadetBlue : Brushes.SandyBrown;
                    await Task.Delay(30);
                    if (capture.Capture(new() { Left = 80, Top = 80, Right = 1000, Bottom = 660 }, []) is null)
                        throw new Exception(capture.Status);
                    frames++;
                    if (mode == "suspend")
                    {
                        capture.Suspend();
                        if (capture.BufferBytes != 0 || !capture.Suspended) throw new Exception("Capture did not suspend or release its pixel ownership");
                    }
                }
                finally { if (reusable is null) capture.Dispose(); }
                await Task.Delay(30);
                if ((i + 1) % 10 == 0) Sample("cycle-" + (i + 1));
            }
            reusable?.Dispose(); reusable = null;
            await Task.Delay(15000); Sample("idle-15s");
            // Diagnostics only: distinguish native retention from collectible
            // managed objects. Production never collects or trims working sets.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            await Task.Delay(500); Sample("diagnostic-full-gc");
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            reusable?.Dispose();
            Write(); board.Close(); Application.Current.Shutdown(failure is null ? 0 : 1);
        }
        void Sample(string stage)
        {
            using var process = Process.GetCurrentProcess(); process.Refresh();
            samples.Add(new { stage, privateBytes = process.PrivateMemorySize64, workingSet = process.WorkingSet64,
                managedBytes = GC.GetTotalMemory(false), managedCommittedBytes = GC.GetGCMemoryInfo().TotalCommittedBytes,
                handles = process.HandleCount, userObjects = GetGuiResources(process.Handle, 1), gdiObjects = GetGuiResources(process.Handle, 0),
                captureBufferBytes = reusable?.BufferBytes ?? 0 });
            Write();
        }
        void Write() => File.WriteAllText(Path.Combine(root, "glass-memory-verification.json"), JsonSerializer.Serialize(new
            { success = failure is null, mode, cycles, frames, samples, failure }, new JsonSerializerOptions { WriteIndented = true }));
    }
    [DllImport("user32.dll")] private static extern uint GetGuiResources(nint process, uint flags);
}
