using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DesktopNook.Core;
using Microsoft.Win32;

namespace DesktopNook.App;
internal static class StorageVerification
{
    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);var checks=new List<string>();string failure="";
        string profile=Path.Combine(root,"Profile"),desktop=Path.Combine(profile,"Desktop"),old=Path.Combine(profile,"Data"),install=Path.Combine(root,"Install 中文"),target=Path.Combine(install,"Data");
        Directory.CreateDirectory(desktop);Directory.CreateDirectory(install);
        string exe=Path.Combine(install,"DesktopNook.exe");File.Copy(Environment.ProcessPath!,exe,false);
        string testKey=@"Software\DesktopNook\StorageVerification\"+Guid.NewGuid().ToString("N");
        void Check(bool condition,string text){if(!condition)throw new IOException(text);checks.Add(text);}
        Process Start(string file,params string[] args){var info=new ProcessStartInfo(file){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};foreach(var arg in args)info.ArgumentList.Add(arg);return Process.Start(info)!;}
        void Stop(){using var p=Start(exe,"--sandbox",profile,"--maintenance=stop");p.WaitForExit(15000);}
        try
        {
            var store=new NookStore(old,[desktop],new SimulatedRecycleBin());
            for(int n=0;n<3;n++){var path=Path.Combine(desktop,n+".url");File.WriteAllText(path,"[InternetShortcut]\r\nURL=https://example.invalid/\r\n");store.Collect(path,"office");}
            store.ReorderItems("office",store.OrderedItems("office").Reverse().Select(i=>i.Id).ToArray());store.DeleteCategory("uncategorized");
            var preferences=Path.Combine(old,"preferences.json");var options=new StartupOptions(testKey+@"\Run",testKey+@"\Settings",preferences,fileOnly:true);
            using(var key=Registry.CurrentUser.CreateSubKey(testKey+@"\Settings")){key.SetValue("CloseEndsProcess",1);key.SetValue("MotionMode","system");}
            options.CloseEndsProcess=false;options.DefaultCollectView="details";options.MotionMode="off";options.WriteWindowSize(new(){Width=800,Height=500,Free=true});
            using(var key=Registry.CurrentUser.OpenSubKey(testKey+@"\Settings"))Check((int)key!.GetValue("CloseEndsProcess")! ==1&&(string)key.GetValue("MotionMode")! =="system"&&key.GetValue("CollectView") is null,"Preference changes persist to files without updating legacy registry settings");
            var before=VerifiedContent.Snapshot(old);StorageRelocation.Migrate(old,target,[desktop],new SimulatedRecycleBin());
            Check(VerifiedContent.Equal(before,VerifiedContent.Snapshot(old)),"Original directory remains an unchanged backup");
            string ready=Path.Combine(root,"ready.json");
            using(var child=Start(exe,"--sandbox",profile,"--data-root",target,"--autostart","--update-ready",ready))
            {
                for(int n=0;n<150&&!File.Exists(ready)&&!child.HasExited;n++)Thread.Sleep(100);
                using var value=JsonDocument.Parse(File.ReadAllText(ready));
                Check(value.RootElement.GetProperty("dataRoot").GetString()==target,"Application reads explicitly relocated data while retaining sandbox desktop identity");
                Check(!value.RootElement.GetProperty("closeEndsProcess").GetBoolean(),"Relocated close preference remains unchecked");
                Stop();child.WaitForExit(10000);Check(child.HasExited,"Maintenance stops the relocated instance through the same instance identity");
            }
            var reloaded=new NookStore(target,[desktop],new SimulatedRecycleBin(),storeAllPayloads:true);
            Check(reloaded.OrderedItems("office").Select(i=>i.Id).SequenceEqual(store.OrderedItems("office").Select(i=>i.Id)),"Application startup preserves category order in relocated storage");
            var staged=LocalUpdater.Stage(Environment.ProcessPath!,exe,target,profile,0,new StartupOptions(preferencesPath:Path.Combine(target,"preferences.json")),reloaded.State);
            using(var helper=Start(staged.Helper,"--apply-update",staged.Plan))
            {
                Check(helper.WaitForExit(30000)&&helper.ExitCode==0,"Updater preserves a relocated data-root override");
            }
            using(var result=JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(staged.Plan)!,"ready.json"))))Check(result.RootElement.GetProperty("dataRoot").GetString()==target,"Updated process confirms the relocated root instead of reopening the old backup");
            Stop();Check(VerifiedContent.Equal(before,VerifiedContent.Snapshot(old)),"Update does not write back to the original backup");
        }
        catch(Exception ex){failure=ex.ToString();}
        finally{try{Stop();}catch{}Registry.CurrentUser.DeleteSubKeyTree(testKey,false);File.WriteAllText(Path.Combine(root,"storage-verification.json"),JsonSerializer.Serialize(new{passed=checks.Count,failure,checks},new JsonSerializerOptions{WriteIndented=true}));}
        return failure.Length==0?0:1;
    }
}
