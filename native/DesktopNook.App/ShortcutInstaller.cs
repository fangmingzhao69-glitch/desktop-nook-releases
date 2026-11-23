using System.IO;
using System.Runtime.InteropServices;

namespace DesktopNook.App;

internal static class ShortcutInstaller
{
    internal const string ApplicationId = "DesktopNook.DesktopNook";
    internal const string DisplayName = "折页";
    internal static readonly string[] LegacyNames = ["随手.lnk", "一处.lnk", "轻匣.lnk", "桌面小筑.lnk"];
    private static readonly Guid AppUserModelFormat = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    [StructLayout(LayoutKind.Sequential)]private struct PropertyKey {public Guid Format;public uint Id;}
    [StructLayout(LayoutKind.Explicit,Size=24)]private struct Variant {[FieldOffset(0)]public ushort Type;[FieldOffset(8)]public IntPtr Text;}
    [ComImport,Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        uint GetCount();void GetAt(uint index,out PropertyKey key);void GetValue(ref PropertyKey key,out Variant value);void SetValue(ref PropertyKey key,ref Variant value);void Commit();
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHGetPropertyStoreFromParsingName(string path,IntPtr bind,uint flags,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out IPropertyStore store);
    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref Variant value);

    public static string Install(string? folderOverride=null)
    {
        var program=Environment.ProcessPath??throw new IOException("无法确定程序位置。");
        var folder=folderOverride??Environment.GetFolderPath(Environment.SpecialFolder.Programs);Directory.CreateDirectory(folder);
        var link=Path.Combine(folder,"折页.lnk");
        var type=Type.GetTypeFromProgID("WScript.Shell")??throw new IOException("无法创建 Windows 快捷方式。");
        dynamic shell=Activator.CreateInstance(type)!;
        try
        {
            if (File.Exists(link) && !Owned(shell, link, program)) throw new IOException("开始菜单中已有同名入口。");
            var legacy = LegacyNames.Select(name => Path.Combine(folder, name)).FirstOrDefault(path => Owned(shell, path, program));
            if (!File.Exists(link) && legacy is not null) File.Move(legacy, link);
            bool exists = File.Exists(link);
            dynamic shortcut = shell.CreateShortcut(link);
            try
            {
                if (!exists)
                {
                    shortcut.TargetPath = program;
                    shortcut.WorkingDirectory = Path.GetDirectoryName(program);
                }
                shortcut.IconLocation = program + ",0";
                shortcut.Description = "折页";
                var arguments = (string)shortcut.Arguments;
                shortcut.Save();
                SetIdentity(link, program + ",0", program, arguments);
            }
            finally { Marshal.FinalReleaseComObject(shortcut); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        return link;
    }

    internal static void RemoveOwned()
    {
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var program = Environment.ProcessPath!;
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("无法读取开始菜单入口。");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            foreach (var name in LegacyNames.Prepend("折页.lnk"))
            {
                var link = Path.Combine(folder, name);
                if (Owned(shell, link, program)) File.Delete(link);
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    internal static bool IsLegacyTaskbarCaption(string caption)
    {
        foreach (var link in LegacyNames)
        {
            string name = Path.GetFileNameWithoutExtension(link);
            if (!caption.StartsWith(name, StringComparison.Ordinal)) continue;
            if (caption.Length == name.Length || char.IsWhiteSpace(caption[name.Length]) ||
                caption[name.Length] is '-' or '–' or '—' or '(' or '（')
                return true;
        }
        return false;
    }

    private static bool Owned(dynamic shell, string link, string program)
    {
        if (!File.Exists(link)) return false;
        dynamic entry = shell.CreateShortcut(link);
        try { return string.Equals((string)entry.TargetPath, program, StringComparison.OrdinalIgnoreCase); }
        finally { Marshal.FinalReleaseComObject(entry); }
    }
    internal static bool SetIdentity(string link, string icon, string program, string arguments)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(link, IntPtr.Zero, 2, ref iid, out var store));
        try { return UpdateIdentity(store, icon, program, arguments); }
        finally { Marshal.FinalReleaseComObject(store); }
    }

    internal static string? WindowApplicationId(IntPtr window)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (window == 0 || SHGetPropertyStoreForWindow(window, ref iid, out var store) < 0) return null;
        try
        {
            var key = new PropertyKey { Format = AppUserModelFormat, Id = 5 };
            store.GetValue(ref key, out var value);
            try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Text) : null; }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.FinalReleaseComObject(store); }
    }

#if NOOK_UI_VERIFICATION
    internal static void SetProbeWindowId(nint window, string id)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, ref iid, out var store));
        var key = new PropertyKey { Format = AppUserModelFormat, Id = 5 };
        var value = new Variant { Type = 31, Text = Marshal.StringToCoTaskMemUni(id) };
        try { store.SetValue(ref key, ref value); store.Commit(); }
        finally { PropVariantClear(ref value); Marshal.FinalReleaseComObject(store); }
    }
#endif

    internal static void SetWindowIdentity(IntPtr window, string program, string? iconLocation = null, bool attachCleanup = true)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, ref iid, out var store));
        if (attachCleanup) System.Windows.Interop.HwndSource.FromHwnd(window)?.AddHook(ClearWindowIdentity);
        try { UpdateIdentity(store, iconLocation ?? program + ",0", program, ""); }
        finally { Marshal.FinalReleaseComObject(store); }
    }

    private static IntPtr ClearWindowIdentity(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int destroy = 2;
        if (message != destroy) return IntPtr.Zero;
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(window, ref iid, out var store) < 0) return IntPtr.Zero;
        try
        {
            for (uint id = 2; id <= 5; id++)
            {
                var key = new PropertyKey { Format = AppUserModelFormat, Id = id };
                Variant empty = default;
                store.SetValue(ref key, ref empty);
            }
        }
        catch (COMException) { }
        finally { Marshal.FinalReleaseComObject(store); }
        return IntPtr.Zero;
    }

    private static bool UpdateIdentity(IPropertyStore store, string icon, string program, string arguments)
    {
        string command = "\"" + program + "\"" + (arguments.Length > 0 ? " " + arguments : "");
        if (Matches(2, command) && Matches(3, icon) && Matches(4, DisplayName) && Matches(5, ApplicationId))
            return false;

        Set(2, command);
        Set(3, icon);
        Set(4, DisplayName);
        // The shell can refresh the entry when AppUserModelID is assigned.
        Set(5, ApplicationId);
        store.Commit();
        return true;

        bool Matches(uint id, string text)
        {
            var key = new PropertyKey { Format = AppUserModelFormat, Id = id };
            store.GetValue(ref key, out var value);
            try { return value.Type == 31 && Marshal.PtrToStringUni(value.Text) == text; }
            finally { PropVariantClear(ref value); }
        }

        void Set(uint id, string text)
        {
            // Reassigning an unchanged AppUserModelID asks Explorer to rebuild
            // the taskbar association even when only the icon has changed.
            if (Matches(id, text)) return;
            var key = new PropertyKey { Format = AppUserModelFormat, Id = id };
            var value = new Variant { Type = 31, Text = Marshal.StringToCoTaskMemUni(text) };
            try { store.SetValue(ref key, ref value); }
            finally { PropVariantClear(ref value); }
        }
    }
}
