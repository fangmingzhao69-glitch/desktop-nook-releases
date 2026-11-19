using System.IO;
using System.Runtime.InteropServices;

namespace DesktopNook.App;

internal static class ShortcutInstaller
{
    internal static readonly string[] LegacyNames = ["随手.lnk", "一处.lnk", "轻匣.lnk", "桌面小筑.lnk"];
    [StructLayout(LayoutKind.Sequential)]private struct PropertyKey {public Guid Format;public uint Id;}
    [StructLayout(LayoutKind.Explicit,Size=24)]private struct Variant {[FieldOffset(0)]public ushort Type;[FieldOffset(8)]public IntPtr Text;}
    [ComImport,Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        uint GetCount();void GetAt(uint index,out PropertyKey key);void GetValue(ref PropertyKey key,out Variant value);void SetValue(ref PropertyKey key,ref Variant value);void Commit();
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHGetPropertyStoreFromParsingName(string path,IntPtr bind,uint flags,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out IPropertyStore store);
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

    private static bool Owned(dynamic shell, string link, string program)
    {
        if (!File.Exists(link)) return false;
        dynamic entry = shell.CreateShortcut(link);
        try { return string.Equals((string)entry.TargetPath, program, StringComparison.OrdinalIgnoreCase); }
        finally { Marshal.FinalReleaseComObject(entry); }
    }
    internal static void SetIdentity(string link,string icon,string program,string arguments)
    {
        var iid=typeof(IPropertyStore).GUID;Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(link,IntPtr.Zero,2,ref iid,out var store));
        try
        {
            void Set(uint id,string text)
            {
                var key=new PropertyKey{Format=new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),Id=id};
                var value=new Variant{Type=31,Text=Marshal.StringToCoTaskMemUni(text)};
                try{store.SetValue(ref key,ref value);}finally{Marshal.FreeCoTaskMem(value.Text);}
            }
            Set(5,"DesktopNook.DesktopNook");Set(4,"折页");Set(3,icon);Set(2,"\""+program+"\""+(arguments.Length>0?" "+arguments:""));store.Commit();
        }
        finally{Marshal.FinalReleaseComObject(store);}
    }
}
