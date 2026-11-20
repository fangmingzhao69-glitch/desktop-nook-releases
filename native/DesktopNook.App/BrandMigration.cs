using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace DesktopNook.App;

internal static class BrandMigration
{
    internal static void RefreshAfterUpgrade(string dataRoot)
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string? pinnedFolder = string.IsNullOrEmpty(roaming) ? null :
            Path.Combine(roaming, "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");
        Refresh(dataRoot, pinnedFolder: pinnedFolder);
    }

    internal static void Refresh(string dataRoot,string? startFolder=null,string? pinnedFolder=null,string registryKey=@"Software\Microsoft\Windows\CurrentVersion\Uninstall\DesktopNook",string? iconFolder=null,bool refreshShortcuts=true)
    {
        var program=Environment.ProcessPath??throw new IOException("无法定位程序。");
        var version=System.Reflection.Assembly.GetExecutingAssembly().GetName().Version!.ToString(3);
        byte[] bytes;
        using(var stream=Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AppIcon.ico")).Stream)
        using(var memory=new MemoryStream())
        {
            stream.CopyTo(memory);bytes=memory.ToArray();
        }
        string EnsureIcon(string folder)
        {
            // Icon identity changes with artwork, not every application release.
            var artwork=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
            Directory.CreateDirectory(folder);var file=Path.Combine(folder,"Zheye-"+artwork+".ico");
            if(!File.Exists(file)||!File.ReadAllBytes(file).SequenceEqual(bytes))File.WriteAllBytes(file,bytes);
            // Shell cache brokers need an unencrypted public icon. Do not decrypt or
            // relax protection on any folder/file; choose a different cache instead.
            if((File.GetAttributes(file)&FileAttributes.Encrypted)!=0)throw new IOException("图标缓存位置被加密。");
            return file;
        }
        string icon;
        try{icon=EnsureIcon(iconFolder??Path.Combine(Path.GetDirectoryName(program)!,"Branding"));}
        catch(Exception ex)when(iconFolder is null&&(ex is IOException or UnauthorizedAccessException))
        {
            var shared=Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
            if(string.IsNullOrEmpty(shared))throw new IOException("无法定位公开图标缓存位置。",ex);
            var sid=System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            icon=EnsureIcon(Path.Combine(shared,"QingxiaIconCache",sid));
        }
        var iconLocation=icon+",0";int changed=0;
        startFolder??=Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        // Never enumerate or rewrite the user's pins implicitly. Explicit folders
        // are used only by an intentional migration/isolated verification.
        var type=Type.GetTypeFromProgID("WScript.Shell")??throw new IOException("无法读取程序入口。");
        dynamic shell=Activator.CreateInstance(type)!;
        try
        {
            bool Owned(string link)
            {
                if(!File.Exists(link))return false;
                dynamic entry=shell.CreateShortcut(link);
                try{return string.Equals((string)entry.TargetPath,program,StringComparison.OrdinalIgnoreCase);}
                finally{Marshal.FinalReleaseComObject(entry);}
            }
            var current=Path.Combine(startFolder,"折页.lnk");
            bool legacy = ShortcutInstaller.LegacyNames.Any(name => Owned(Path.Combine(startFolder, name)));
            if(refreshShortcuts&&(legacy||Owned(current))&&(!File.Exists(current)||Owned(current)))
            {
                if(!File.Exists(current) && legacy){ShortcutInstaller.Install(startFolder);changed++;}
                RefreshLink(current);
            }
            if(refreshShortcuts&&pinnedFolder is not null&&Directory.Exists(pinnedFolder))foreach(var link in Directory.EnumerateFiles(pinnedFolder,"*.lnk"))if(Owned(link))RefreshLink(link);
            void RefreshLink(string link)
            {
                dynamic entry=shell.CreateShortcut(link);
                try
                {
                    var arguments = (string)entry.Arguments;
                    bool saved = !string.Equals((string)entry.IconLocation, iconLocation, StringComparison.OrdinalIgnoreCase) ||
                        (string)entry.Description != ShortcutInstaller.DisplayName;
                    // Preserve target, arguments, working directory, file name and pin order.
                    if (saved)
                    {
                        entry.IconLocation = iconLocation;
                        entry.Description = ShortcutInstaller.DisplayName;
                        entry.Save();
                    }
                    bool updatedIdentity = ShortcutInstaller.SetIdentity(link, iconLocation, program, arguments);
                    if (!saved && !updatedIdentity) return;
                    ShellIntegration.RefreshShortcut(link);
                    changed++;
                }
                finally{Marshal.FinalReleaseComObject(entry);}
            }
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
}
