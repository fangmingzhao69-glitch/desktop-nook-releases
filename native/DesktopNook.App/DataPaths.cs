using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using DesktopNook.Core;
using Microsoft.Win32;

namespace DesktopNook.App;
internal static class DataPaths
{
    internal static string LegacyRoot=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DesktopNook");
    internal static string InstallationRoot
    {
        get
        {
            using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\DesktopNook");
            var registered=key?.GetValue("InstallLocation") as string;
            var directory=registered is not null&&File.Exists(Path.Combine(registered,"DesktopNook.exe"))?registered:Path.GetDirectoryName(Environment.ProcessPath!)!;
            return Path.Combine(directory,"Data");
        }
    }
    internal static string Signature(string root)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())))[..20];
    private static bool Same(string a,string b)=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),StringComparison.OrdinalIgnoreCase);
    private static bool HasData(string path)=>File.Exists(Path.Combine(path,"state.json"))||File.Exists(Path.Combine(path,"state.json.bak"))||Directory.Exists(Path.Combine(path,"vault"));
    private static string? PreviousLocation()
    {
        var path=Path.Combine(LegacyRoot,"storage-location.json");
        if(!File.Exists(path))return null;
        var value=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(path));
        var target=value?.GetValueOrDefault("DataDirectory");
        if(target is null||!HasData(target))throw new IOException("已迁移的数据目录不可用，未使用旧备份替代当前数据。");
        return Path.GetFullPath(target);
    }
    internal static bool IsInstalledData(string root)=>Same(root,InstallationRoot)||File.Exists(Path.Combine(root,"storage.json"));
    internal static string Select(string[] args,bool maintenance)
    {
        int explicitRoot=Array.IndexOf(args,"--data-root");
        if(explicitRoot>=0)
        {
            var requested=Path.GetFullPath(args[explicitRoot+1]);
            // An old updater needs its existing root until it has acknowledged ready.
            if(args.Contains("--resume-update")&&Same(requested,LegacyRoot))return requested;
            if(!Same(requested,LegacyRoot))return requested;
        }
        if(!maintenance)return InstallationRoot;
        if(HasData(InstallationRoot))return InstallationRoot;
        var previous=PreviousLocation();if(previous is not null)return previous;
        using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\DesktopNook");
        if(key?.GetValue("InstallLocation") is string installed&&HasData(Path.Combine(installed,"Data")))return Path.Combine(installed,"Data");
        return LegacyRoot;
    }
    internal static void Prepare(string root,IReadOnlyList<string> desktops,IRecycleBin recycle)
    {
        if(HasData(root))return;
        if(File.Exists(Path.Combine(root,"storage.json"))&&JsonSerializer.Deserialize<StorageReceipt>(File.ReadAllText(Path.Combine(root,"storage.json")))?.Layout=="installation-v1")return;
        if(Directory.Exists(root)&&Directory.EnumerateFileSystemEntries(root).Any())throw new IOException("安装目录下的 Data 已有其他内容，未覆盖。");
        if(Directory.Exists(root))Directory.Delete(root,false);
        var source=PreviousLocation()??LegacyRoot;
        if(HasData(source))Migrate(source,root,desktops,recycle);
        else{Directory.CreateDirectory(root);StorageSecurity.Protect(root);StorageRelocation.WriteJson(Path.Combine(root,"storage.json"),new StorageReceipt());}
    }
    internal static StorageReceipt Migrate(string source,string target,IReadOnlyList<string> desktops,IRecycleBin recycle)
    {
        var receipt=StorageRelocation.Migrate(source,target,desktops,recycle);
        // Only a small compatibility locator remains beside the untouched old data.
        try{Directory.CreateDirectory(LegacyRoot);StorageRelocation.WriteJson(Path.Combine(LegacyRoot,"storage-location.json"),new Dictionary<string,string>{{"DataDirectory",Path.GetFullPath(target)}});}catch(IOException){}
        return receipt;
    }
    internal static int MigrateCommand(string source,string target,string report)
    {
        using var mutex=new Mutex(false,"Local\\DesktopNook-"+Signature(LegacyRoot));bool owned=false;
        try
        {
            try{owned=mutex.WaitOne(0);}catch(AbandonedMutexException){owned=true;}
            if(!owned)throw new IOException("请先正常退出轻匣，未在运行期间迁移数据。");
            var desktops=new[]{Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)};
            var receipt=Migrate(source,target,desktops,new WindowsRecycleBin());
            File.WriteAllText(report,JsonSerializer.Serialize(new{success=true,receipt},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception ex){File.WriteAllText(report,JsonSerializer.Serialize(new{success=false,error=ex.ToString()}));return 1;}
        finally{if(owned)mutex.ReleaseMutex();}
    }
}
