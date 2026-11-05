using Microsoft.Win32;

namespace DesktopNook.App;
internal sealed class StartupOptions(string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run",string settingsKey = @"Software\DesktopNook\Settings")
{
    private const string ValueName="DesktopNook";
    internal WindowSizePreference ReadWindowSize()
    {
        try{using var key=Registry.CurrentUser.OpenSubKey(settingsKey);return (System.Text.Json.JsonSerializer.Deserialize<WindowSizePreference>(key?.GetValue("WindowSize") as string??"{}")??new()).Validated();}catch{return new();}
    }
    internal void WriteWindowSize(WindowSizePreference value){using var key=Registry.CurrentUser.CreateSubKey(settingsKey,true);key.SetValue("WindowSize",System.Text.Json.JsonSerializer.Serialize(value.Validated()),RegistryValueKind.String);key.Flush();}
    public bool Enabled {get{using var key=Registry.CurrentUser.OpenSubKey(runKey);return key?.GetValue(ValueName) is string value&&!string.IsNullOrWhiteSpace(value);}}
    public string? Command {get{using var key=Registry.CurrentUser.OpenSubKey(runKey);return key?.GetValue(ValueName) as string;}}
    public bool CloseEndsProcess {get{using var key=Registry.CurrentUser.OpenSubKey(settingsKey);return key?.GetValue("CloseEndsProcess") is not int value || value!=0;}set{using var key=Registry.CurrentUser.CreateSubKey(settingsKey,true);key.SetValue("CloseEndsProcess",value?1:0,RegistryValueKind.DWord);key.Flush();}}
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
