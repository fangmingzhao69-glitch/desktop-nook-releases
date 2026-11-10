using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNook.App;
internal sealed class StartupOptions(string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run",string settingsKey = @"Software\DesktopNook\Settings",string? preferencesPath=null)
{
    private sealed class Preferences {public bool? CloseEndsProcess {get;set;} public WindowSizePreference? WindowSize {get;set;} [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public string? CollectView {get;set;}}
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
    private string? SavedCollectView {get{var saved=ReadPreferences()?.CollectView;if(saved is "icons" or "details")return saved;using var key=Registry.CurrentUser.OpenSubKey(settingsKey);return key?.GetValue("CollectView") as string;}}
    private Preferences Snapshot()=>new(){CloseEndsProcess=CloseEndsProcess,WindowSize=ReadWindowSize(),CollectView=SavedCollectView};
    internal string DefaultCollectView
    {
        get=>SavedCollectView=="details"?"details":"icons";
        set{if(value is not ("icons" or "details"))throw new ArgumentException("无效的添加视图。");var preferences=Snapshot();preferences.CollectView=value;SavePreferences(preferences);using var key=Registry.CurrentUser.CreateSubKey(settingsKey,true);key.SetValue("CollectView",value);key.Flush();}
    }
    internal void PersistForUpdate()=>SavePreferences(Snapshot());
    private const string ValueName="DesktopNook";
    internal WindowSizePreference ReadWindowSize()
    {
        if(ReadPreferences()?.WindowSize is WindowSizePreference saved)return saved.Validated();
        try{using var key=Registry.CurrentUser.OpenSubKey(settingsKey);return (System.Text.Json.JsonSerializer.Deserialize<WindowSizePreference>(key?.GetValue("WindowSize") as string??"{}")??new()).Validated();}catch{return new();}
    }
    internal void WriteWindowSize(WindowSizePreference value){var preferences=Snapshot();preferences.WindowSize=value.Validated();SavePreferences(preferences);using var key=Registry.CurrentUser.CreateSubKey(settingsKey,true);key.SetValue("WindowSize",System.Text.Json.JsonSerializer.Serialize(value.Validated()),RegistryValueKind.String);key.Flush();}
    public bool Enabled {get{using var key=Registry.CurrentUser.OpenSubKey(runKey);return key?.GetValue(ValueName) is string value&&!string.IsNullOrWhiteSpace(value);}}
    public string? Command {get{using var key=Registry.CurrentUser.OpenSubKey(runKey);return key?.GetValue(ValueName) as string;}}
    public bool CloseEndsProcess {get{if(ReadPreferences()?.CloseEndsProcess is bool saved)return saved;using var key=Registry.CurrentUser.OpenSubKey(settingsKey);return key?.GetValue("CloseEndsProcess") is not int value || value!=0;}set{var preferences=Snapshot();preferences.CloseEndsProcess=value;SavePreferences(preferences);using var key=Registry.CurrentUser.CreateSubKey(settingsKey,true);key.SetValue("CloseEndsProcess",value?1:0,RegistryValueKind.DWord);key.Flush();}}
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
