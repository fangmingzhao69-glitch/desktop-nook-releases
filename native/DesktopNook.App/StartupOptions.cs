using Microsoft.Win32;
using System.IO;
using System.Text.Json;

namespace DesktopNook.App;
internal sealed class StartupOptions(string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run",string settingsKey = @"Software\DesktopNook\Settings",string? preferencesPath=null)
{
    private sealed class Preferences {public bool? CloseEndsProcess {get;set;} public WindowSizePreference? WindowSize {get;set;}}
    private Preferences? ReadPreferences()
    {
        if(preferencesPath is null)return null;
        foreach(var path in new[]{preferencesPath,preferencesPath+".bak"})
            if(File.Exists(path))try{return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path));}catch(JsonException){}
        return null;
    }
    private void SavePreferences(Preferences value)
    {
        if(preferencesPath is null)return;
        Directory.CreateDirectory(Path.GetDirectoryName(preferencesPath)!);
        string temp=preferencesPath+".tmp-"+Guid.NewGuid().ToString("N");
        try
        {
            if(File.Exists(preferencesPath))File.Copy(preferencesPath,temp);
            using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){JsonSerializer.Serialize(stream,value);stream.Flush(true);}
            if(File.Exists(preferencesPath))File.Copy(preferencesPath,preferencesPath+".bak",true);
            File.Move(temp,preferencesPath,true);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    internal void PersistForUpdate()=>SavePreferences(new(){CloseEndsProcess=CloseEndsProcess,WindowSize=ReadWindowSize()});
    private const string ValueName="DesktopNook";
    internal WindowSizePreference ReadWindowSize()
    {
        if(ReadPreferences()?.WindowSize is WindowSizePreference saved)return saved.Validated();
        try{using var key=Registry.CurrentUser.OpenSubKey(settingsKey);return (System.Text.Json.JsonSerializer.Deserialize<WindowSizePreference>(key?.GetValue("WindowSize") as string??"{}")??new()).Validated();}catch{return new();}
    }
    internal void WriteWindowSize(WindowSizePreference value){var preferences=new Preferences{CloseEndsProcess=CloseEndsProcess,WindowSize=value.Validated()};SavePreferences(preferences);using var key=Registry.CurrentUser.CreateSubKey(settingsKey,true);key.SetValue("WindowSize",System.Text.Json.JsonSerializer.Serialize(value.Validated()),RegistryValueKind.String);key.Flush();}
    public bool Enabled {get{using var key=Registry.CurrentUser.OpenSubKey(runKey);return key?.GetValue(ValueName) is string value&&!string.IsNullOrWhiteSpace(value);}}
    public string? Command {get{using var key=Registry.CurrentUser.OpenSubKey(runKey);return key?.GetValue(ValueName) as string;}}
    public bool CloseEndsProcess {get{if(ReadPreferences()?.CloseEndsProcess is bool saved)return saved;using var key=Registry.CurrentUser.OpenSubKey(settingsKey);return key?.GetValue("CloseEndsProcess") is not int value || value!=0;}set{SavePreferences(new(){CloseEndsProcess=value,WindowSize=ReadWindowSize()});using var key=Registry.CurrentUser.CreateSubKey(settingsKey,true);key.SetValue("CloseEndsProcess",value?1:0,RegistryValueKind.DWord);key.Flush();}}
    public void SetEnabled(bool enabled,string? executable=null)
    {
        using var key=Registry.CurrentUser.CreateSubKey(runKey,true);
        if(enabled)
        {
            executable??=Environment.ProcessPath??throw new InvalidOperationException("无法确定程序位置。");
            if(executable.Contains('"'))throw new ArgumentException("程序路径无效。");
            var command="\""+executable+"\" --autostart";
            if(command.Length>260)throw new InvalidOperationException("程序路径过长，请将程序放在较短路径后启用自启。");
            key.SetValue(ValueName,command,RegistryValueKind.String);
        }
        else key.DeleteValue(ValueName,false);
        key.Flush();
    }
    public void RefreshEnabledPath(){if(Enabled)SetEnabled(true);}
}
