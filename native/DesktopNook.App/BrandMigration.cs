using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace DesktopNook.App;

internal static class BrandMigration
{
    internal static void RefreshAfterUpgrade(string dataRoot, string? pinnedIconLocation = null) =>
        Refresh(dataRoot, pinnedFolder: PinnedFolder(), pinnedIconLocation: pinnedIconLocation);

    private static string? PinnedFolder()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrEmpty(roaming) ? null :
            Path.Combine(roaming, "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");
    }

    internal static void RefreshPinnedAppearance(string iconLocation, string? folder = null)
    {
        folder ??= PinnedFolder();
        if (folder is null || !Directory.Exists(folder)) return;
        var program = Environment.ProcessPath!;
        object shell = CreateShell();
        try { RefreshPins(shell, folder, program, iconLocation); }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    internal static void Refresh(string dataRoot,string? startFolder=null,string? pinnedFolder=null,string registryKey=@"Software\Microsoft\Windows\CurrentVersion\Uninstall\DesktopNook",string? iconFolder=null,bool refreshShortcuts=true,string? pinnedIconLocation=null)
    {
        var program=Environment.ProcessPath??throw new IOException("无法定位程序。");
        var version=System.Reflection.Assembly.GetExecutingAssembly().GetName().Version!.ToString(3);
        byte[] bytes;
        using(var stream=Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AppIcon.ico")).Stream)
        using(var memory=new MemoryStream())
        {
            stream.CopyTo(memory);bytes=memory.ToArray();
        }
        string icon=ThemeShellIcon.SavePublicIcon(bytes,iconFolder);
        var iconLocation=icon+",0";int changed=0;
        startFolder??=Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        // Never enumerate or rewrite the user's pins implicitly. Explicit folders
        // are used only by an intentional migration/isolated verification.
        object shell=CreateShell();
        try
        {
            var current=Path.Combine(startFolder,"折页.lnk");
            bool legacy = ShortcutInstaller.LegacyNames.Any(name => OwnsShortcut(shell, Path.Combine(startFolder, name), program));
            bool owned = OwnsShortcut(shell,current,program);
            if(refreshShortcuts&&(legacy||owned)&&(!File.Exists(current)||owned))
            {
                if(!File.Exists(current) && legacy){ShortcutInstaller.Install(startFolder);changed++;}
                if (RefreshLink(shell,current,program,iconLocation,refreshName:true)) changed++;
            }
            if (refreshShortcuts && pinnedFolder is not null && Directory.Exists(pinnedFolder))
                changed += RefreshPins(shell,pinnedFolder,program,pinnedIconLocation ?? iconLocation,refreshName:true);
        }
        finally{Marshal.FinalReleaseComObject(shell);}
        using(var key=Registry.CurrentUser.OpenSubKey(registryKey,true))
        {
            if(string.Equals(key?.GetValue("InstallLocation") as string,Path.GetDirectoryName(program),StringComparison.OrdinalIgnoreCase))
            {
                if(key!.GetValue("DisplayName") as string!="折页"||key.GetValue("DisplayIcon") as string!=iconLocation)changed++;
                key.SetValue("DisplayName","折页");key.SetValue("DisplayIcon",iconLocation);key.SetValue("DisplayVersion",version);
            }
        }
        if(changed>0)ShellIntegration.RefreshDesktop();
    }

    private static object CreateShell() => Activator.CreateInstance(
        Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("无法读取程序入口。"))!;

    private static bool OwnsShortcut(dynamic shell, string path, string program)
    {
        if (!File.Exists(path)) return false;
        dynamic entry = shell.CreateShortcut(path);
        try { return string.Equals((string)entry.TargetPath,program,StringComparison.OrdinalIgnoreCase); }
        finally { Marshal.FinalReleaseComObject(entry); }
    }

    private static int RefreshPins(object shell, string folder, string program, string iconLocation, bool refreshName = false)
    {
        int changed = 0;
        foreach (var link in Directory.EnumerateFiles(folder,"*.lnk"))
            if (RefreshLink(shell,link,program,iconLocation,refreshName)) changed++;
        return changed;
    }

    private static bool RefreshLink(dynamic shell, string link, string program, string iconLocation, bool refreshName)
    {
        dynamic entry = shell.CreateShortcut(link);
        try
        {
            if (!string.Equals((string)entry.TargetPath,program,StringComparison.OrdinalIgnoreCase)) return false;
            var arguments = (string)entry.Arguments;
            bool saved = !string.Equals((string)entry.IconLocation,iconLocation,StringComparison.OrdinalIgnoreCase) ||
                (refreshName && (string)entry.Description != ShortcutInstaller.DisplayName);
            if (saved)
            {
                entry.IconLocation = iconLocation;
                if (refreshName) entry.Description = ShortcutInstaller.DisplayName;
                entry.Save();
            }
            bool updatedIdentity = ShortcutInstaller.SetIdentity(link,iconLocation,program,arguments);
            if (!saved && !updatedIdentity) return false;
            ShellIntegration.RefreshShortcut(link);
            return true;
        }
        finally { Marshal.FinalReleaseComObject(entry); }
    }
}
