using System.Runtime.InteropServices;

namespace DesktopNook.App;

internal static class DockScreens
{
    internal sealed record Screen(string Id, string Name);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Device
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref Device info, uint flags);
    internal static IReadOnlyList<Screen> List()
    {
        var list = new List<Screen>();
        for (uint i = 0; ; i++)
        {
            var adapter = new Device { Size = Marshal.SizeOf<Device>() };
            if (!EnumDisplayDevices(null, i, ref adapter, 0)) break;
            if ((adapter.Flags & 1) == 0) continue;
            var screen = new Device { Size = Marshal.SizeOf<Device>() };
            string id = adapter.Name, name = adapter.Name;
            for (uint n = 0; EnumDisplayDevices(adapter.Name, n, ref screen, 1); n++)
            {
                if (n == 0 || (screen.Flags & 1) != 0) { if (!string.IsNullOrEmpty(screen.Id)) id = screen.Id; name = screen.Description; }
                if ((screen.Flags & 1) != 0) break;
                screen = new() { Size = Marshal.SizeOf<Device>() };
            }
            list.Add(new(id, $"{list.Count + 1} · {name}{((adapter.Flags & 4) != 0 ? "（主屏）" : "")}"));
        }
        return list;
    }
}
