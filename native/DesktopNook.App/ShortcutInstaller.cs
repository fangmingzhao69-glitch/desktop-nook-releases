using System.IO;
using System.Runtime.InteropServices;

namespace DesktopNook.App;

internal static class ShortcutInstaller
{
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
        var link=Path.Combine(folder,"轻匣.lnk");
        var type=Type.GetTypeFromProgID("WScript.Shell")??throw new IOException("无法创建 Windows 快捷方式。");
        dynamic shell=Activator.CreateInstance(type)!;dynamic shortcut=shell.CreateShortcut(link);
        try{shortcut.TargetPath=program;shortcut.WorkingDirectory=Path.GetDirectoryName(program);shortcut.IconLocation=program+",0";shortcut.Description="轻匣 — 给桌面图标，一个小家";shortcut.Save();}
        finally{Marshal.FinalReleaseComObject(shortcut);Marshal.FinalReleaseComObject(shell);}
        var iid=typeof(IPropertyStore).GUID;Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(link,IntPtr.Zero,2,ref iid,out var store));
        var key=new PropertyKey{Format=new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),Id=5};
        var value=new Variant{Type=31,Text=Marshal.StringToCoTaskMemUni("DesktopNook.DesktopNook")};
        try{store.SetValue(ref key,ref value);store.Commit();}finally{Marshal.FreeCoTaskMem(value.Text);Marshal.FinalReleaseComObject(store);}
        var legacy=Path.Combine(folder,"桌面小筑.lnk");
        if(File.Exists(legacy))
        {
            dynamic oldShell=Activator.CreateInstance(type)!;dynamic oldLink=oldShell.CreateShortcut(legacy);
            try { if(string.Equals((string)oldLink.TargetPath,program,StringComparison.OrdinalIgnoreCase))File.Delete(legacy); }
            finally { Marshal.FinalReleaseComObject(oldLink);Marshal.FinalReleaseComObject(oldShell); }
        }
        return link;
    }
}
