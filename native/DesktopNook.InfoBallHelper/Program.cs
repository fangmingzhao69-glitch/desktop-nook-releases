using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using DesktopNook.App;

namespace DesktopNook.InfoBallHelper;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is not ["--run"]) return 64;
        try
        {
            var directory = Path.GetDirectoryName(Environment.ProcessPath!)!;
            using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "owner.json")));
            if (config.RootElement.GetProperty("Sid").GetString() != WindowsIdentity.GetCurrent().User!.Value) return 65;
            string program = config.RootElement.GetProperty("Program").GetString()!;
            if (!Path.IsPathFullyQualified(program) || !Path.GetFileName(program).Equals("DesktopNook.exe", StringComparison.OrdinalIgnoreCase)) return 65;
            // The task is started on demand by the ordinary app, never at logon.
            // Wait briefly for Explorer to finish applying its capsule geometry.
            for (int attempt = 0; attempt < 20; attempt++)
            {
                nint taskbar = FindWindowEx(0, 0, "Shell_TrayWnd", null);
                nint bridge = FindWindowEx(taskbar, 0, "Windows.UI.Composition.DesktopWindowContentBridge", null);
                int owner = (int)GetProp(bridge, "DesktopNook.NativeTaskbarShape.Owner");
                if (owner > 0)
                {
                    using var process = Process.GetProcessById(owner);
                    if (process.SessionId == Process.GetCurrentProcess().SessionId &&
                        string.Equals(process.MainModule?.FileName, program, StringComparison.OrdinalIgnoreCase))
                        return NativeTaskbarInfoBall.RunAssistant(owner, program);
                }
                Thread.Sleep(250);
            }
            return 0;
        }
        catch (Exception) { return 1; }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string name, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint window, string name);
}
