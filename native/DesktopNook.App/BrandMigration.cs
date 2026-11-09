using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace DesktopNook.App;

internal static class BrandMigration
{
    internal static void Refresh(string dataRoot,string? startFolder=null,string? pinnedFolder=null,string registryKey=@"Software\Microsoft\Windows\CurrentVersion\Uninstall\DesktopNook",string? iconFolder=null)
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
            Directory.CreateDirectory(folder);var file=Path.Combine(folder,"Qingxia-"+version+".ico");
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
        pinnedFolder??=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),@"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
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
            var legacy=Path.Combine(startFolder,"桌面小筑.lnk");var current=Path.Combine(startFolder,"轻匣.lnk");
            if((Owned(legacy)||Owned(current))&&(!File.Exists(current)||Owned(current)))
            {
                if(Owned(legacy)){ShortcutInstaller.Install(startFolder);changed++;}
                RefreshLink(current);
            }
            if(Directory.Exists(pinnedFolder))foreach(var link in Directory.EnumerateFiles(pinnedFolder,"*.lnk"))if(Owned(link))RefreshLink(link);
            void RefreshLink(string link)
            {
                dynamic entry=shell.CreateShortcut(link);
                try
                {
                    if(string.Equals((string)entry.IconLocation,iconLocation,StringComparison.OrdinalIgnoreCase)&&(string)entry.Description=="轻匣")return;
                    var arguments=(string)entry.Arguments;
                    // Preserve target, arguments, working directory, file name and pin order.
                    entry.IconLocation=iconLocation;entry.Description="轻匣";entry.Save();
                    ShortcutInstaller.SetIdentity(link,iconLocation,program,arguments);changed++;
                }
                finally{Marshal.FinalReleaseComObject(entry);}
            }
        }
        finally{Marshal.FinalReleaseComObject(shell);}
        using(var key=Registry.CurrentUser.OpenSubKey(registryKey,true))
        {
            if(string.Equals(key?.GetValue("InstallLocation") as string,Path.GetDirectoryName(program),StringComparison.OrdinalIgnoreCase))
            {
                if(key!.GetValue("DisplayName") as string!="轻匣"||key.GetValue("DisplayIcon") as string!=iconLocation)changed++;
                key.SetValue("DisplayName","轻匣");key.SetValue("DisplayIcon",iconLocation);key.SetValue("DisplayVersion",version);
            }
        }
        if(changed>0)ShellIntegration.RefreshDesktop();
    }
}
