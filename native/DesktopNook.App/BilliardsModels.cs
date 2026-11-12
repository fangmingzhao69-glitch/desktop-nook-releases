using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using DesktopNook.Core;

namespace DesktopNook.App;
internal sealed record GameIcon(string Id,string Name,string Path,bool Recycle=false);
internal sealed class GameSettings
{
    public string Source {get;set;}="current";
    public int Count {get;set;}=15;
    public bool Guide {get;set;}=true;
}
internal sealed class GameMessage
{
    public string Kind {get;set;}="";
    public string Nonce {get;set;}="";
    public int Pid {get;set;}
    public long StartTicks {get;set;}
    public List<GameIcon> Icons {get;set;}=[];
    public int Count {get;set;}
    public bool Guide {get;set;}
    public int Round {get;set;}
    public int Shots {get;set;}
    public double Milliseconds {get;set;}
    public double Left {get;set;}
    public double Top {get;set;}
    public double Width {get;set;}
    public double Height {get;set;}
    public bool TestMode {get;set;}
    public string Action {get;set;}="";
    public int Request {get;set;}
    public bool Paused {get;set;}
    public bool FrameSubscribed {get;set;}
    public long PrivateWorkingSet {get;set;}
    public long Commit {get;set;}
    public double CpuMs {get;set;}
    public double ScreenX {get;set;}
    public double ScreenY {get;set;}
    public string Image {get;set;}="";
    public long WindowHandle {get;set;}
    public double FrameP95 {get;set;}
    public double FrameMean {get;set;}
    public int FrameCount {get;set;}
}
internal sealed record GameScore(DateTime CompletedUtc,string Source,int Count,int Shots,double Milliseconds)
{
    public double AverageSeconds=>Milliseconds/1000/Count;
}
internal sealed class GameStorage(string dataRoot)
{
    private sealed class History
    {
        public List<GameScore> Recent {get;set;}=[];
        public GameScore? Best {get;set;}
    }
    private readonly object _gate=new();
    internal string Root=>Path.Combine(dataRoot,"Games","IconBilliards");
    internal GameSettings LoadSettings()
    {
        try{var settings=JsonSerializer.Deserialize<GameSettings>(File.ReadAllText(Path.Combine(Root,"settings.json")))??new();settings.Count=Math.Clamp(settings.Count,15,45);if(settings.Source is not ("current" or "all" or "desktop"))settings.Source="current";return settings;}catch(Exception ex)when(ex is IOException or JsonException){return new();}
    }
    internal void SaveSettings(GameSettings value){lock(_gate){Directory.CreateDirectory(Root);StorageRelocation.WriteJson(Path.Combine(Root,"settings.json"),value);}}
    private static bool Valid(GameScore? score)=>score is {Count:>=15 and <=45,Shots:>0}&&double.IsFinite(score.Milliseconds)&&score.Milliseconds>0;
    private History LoadHistory()
    {
        try
        {
            var json=File.ReadAllText(Path.Combine(Root,"scores.json"));
            var history=json.TrimStart().StartsWith('[')?new History{Recent=JsonSerializer.Deserialize<List<GameScore>>(json)??[]}:JsonSerializer.Deserialize<History>(json)??new();
            history.Recent=history.Recent.Where(Valid).TakeLast(100).ToList();
            history.Best=history.Recent.Concat(Valid(history.Best)?[history.Best!]:[]).OrderBy(s=>s.AverageSeconds).FirstOrDefault();return history;
        }
        catch(Exception ex)when(ex is IOException or JsonException){return new();}
    }
    internal List<GameScore> Scores(){lock(_gate)return LoadHistory().Recent;}
    internal GameScore? Best {get{lock(_gate)return LoadHistory().Best;}}
    internal void SaveScore(GameScore value)
    {
        if(!Valid(value))throw new IOException("成绩无效。");
        lock(_gate){var history=LoadHistory();history.Recent.Add(value);history.Recent=history.Recent.TakeLast(100).ToList();if(history.Best is null||value.AverageSeconds<history.Best.AverageSeconds)history.Best=value;Directory.CreateDirectory(Root);StorageRelocation.WriteJson(Path.Combine(Root,"scores.json"),history);}
    }
}
internal static class GameIconCatalog
{
    internal static List<GameIcon> Collected(NookStore store,string? category)=>store.OrderedItems(category).Select(i=>new GameIcon(i.Id,i.Name,i.Kind=="recycle-bin"?"":store.ManagedPath(i),i.Kind=="recycle-bin")).ToList();
    internal static Task<List<GameIcon>> Desktop(NookStore store,bool sandbox)
    {
        var ready=new TaskCompletionSource<List<GameIcon>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var icons=new List<GameIcon>();object? shell=null,folder=null,items=null;
            try
            {
                if(!sandbox)
                {
                    shell=Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);folder=((dynamic)shell!).NameSpace(0);items=((dynamic)folder!).Items();
                    for(int n=0;n<Math.Min(4096,(int)((dynamic)items!).Count);n++)
                    {
                        object item=((dynamic)items).Item(n);
                        try
                        {
                            string path=Convert.ToString(((dynamic)item).Path)??"",name=Convert.ToString(((dynamic)item).Name)??"";
                            if(string.IsNullOrWhiteSpace(path)||string.IsNullOrWhiteSpace(name))continue;
                            if(File.Exists(path)||Directory.Exists(path)){if((File.GetAttributes(path)&FileAttributes.Hidden)!=0)continue;}
                            else if(path.StartsWith("::",StringComparison.Ordinal))
                            {
                                bool hidden=false;
                                foreach(var hive in new[]{Microsoft.Win32.Registry.CurrentUser,Microsoft.Win32.Registry.LocalMachine})using(var key=hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel")){if(key?.GetValue(path[2..]) is int flag){hidden=flag!=0;break;}}
                                if(hidden)continue;
                            }
                            icons.Add(new(path,name,path));
                        }
                        finally{Marshal.FinalReleaseComObject(item);}
                    }
                }
                else foreach(var root in store.DesktopRoots.Where(Directory.Exists))foreach(var path in Directory.EnumerateFileSystemEntries(root))if((File.GetAttributes(path)&FileAttributes.Hidden)==0)icons.Add(new(path,Path.GetFileNameWithoutExtension(path),path));
                ready.TrySetResult(icons.DistinctBy(i=>i.Id,StringComparer.OrdinalIgnoreCase).Take(4096).ToList());
            }
            catch(Exception ex){ready.TrySetException(ex);}
            finally{foreach(var com in new[]{items,folder,shell})if(com is not null&&Marshal.IsComObject(com))Marshal.FinalReleaseComObject(com);}
        }){IsBackground=true,Name="Billiards desktop catalogue"};thread.SetApartmentState(ApartmentState.STA);thread.Start();return ready.Task;
    }
}
