using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using DesktopNook.Core;

namespace DesktopNook.App;

public partial class App : Application
{
    private Mutex? _mutex; private EventWaitHandle? _activate; private EventWaitHandle? _stop; private bool _ownsMutex;
    public static NookStore Store { get; private set; } = null!;
    public static IRecycleBin Recycle { get; private set; } = null!;
    public static bool IsSandbox { get; private set; }
    internal static string? SandboxRoot {get;private set;}
    internal static StartupOptions Options {get;private set;}=new();
    public static void ReloadStore() => Store = new NookStore(Store.DataDirectory,Store.DesktopRoots,Recycle,storeAllPayloads:Store.StoreAllPayloads);
    private void OnStartup(object sender, StartupEventArgs e)
    {
        try
        {
            var game=Array.IndexOf(e.Args,"--icon-billiards");if(game>=0){_=BilliardsHost.Run(e.Args,game);return;}
            if(e.Args.Any(a=>a is "--billiards-self-test" or "--billiards-parent-probe" or "--billiards-perf-test" or "--billiards-protocol-self-test" or "--billiards-mouse-self-test")&&!e.Args.Contains("--sandbox"))throw new IOException("台球检查必须使用沙箱。");
            var storageTest=Array.IndexOf(e.Args,"--storage-self-test");if(storageTest>=0){Shutdown(StorageVerification.Run(Path.GetFullPath(e.Args[storageTest+1])));return;}
            var relocate=Array.IndexOf(e.Args,"--relocate-storage");if(relocate>=0){Shutdown(DataPaths.MigrateCommand(e.Args[relocate+1],e.Args[relocate+2],e.Args[relocate+3]));return;}
            var onlineTest=Array.IndexOf(e.Args,"--verify-online-update");if(onlineTest>=0){Shutdown(Task.Run(()=>OnlineUpdateVerification.Run(e.Args[onlineTest+1],e.Args[onlineTest+2],e.Args.Contains("--live"),e.Args.Contains("--live-check"))).GetAwaiter().GetResult());return;}
            var iconCheck=Array.IndexOf(e.Args,"--verify-icons");if(iconCheck>=0){Shutdown(IconVerification.Run(e.Args[iconCheck+1],e.Args.Skip(iconCheck+2)));return;}
            var inventory=Array.IndexOf(e.Args,"--collection-inventory");if(inventory>=0){Shutdown(CollectionRecovery.Run(e.Args[inventory+1],e.Args.Contains("--apply")));return;}
            var updateTest=Array.IndexOf(e.Args,"--verify-update");if(updateTest>=0){Shutdown(UpdateVerification.Run(e.Args[updateTest+1],e.Args[updateTest+2],e.Args[updateTest+3]));return;}
            var applyIndex=Array.IndexOf(e.Args,"--apply-update");if(applyIndex>=0){Shutdown(LocalUpdater.Apply(e.Args[applyIndex+1]));return;}
            if(e.Args.Contains("--verification-marker") && Array.IndexOf(e.Args,"--sandbox") is var markerIndex && markerIndex>=0)
            {
                File.WriteAllText(Path.Combine(Path.GetFullPath(e.Args[markerIndex+1]),"shortcut-launched.txt"),"DesktopNook shortcut launch verified");Shutdown();return;
            }
            ShellIntegration.SetAppId("DesktopNook.DesktopNook");
            var payloadIndex=Array.IndexOf(e.Args,"--verify-payloads");if(payloadIndex>=0){Shutdown(PayloadVerification.Run(Path.GetFullPath(e.Args[payloadIndex+1])));return;}
            var liveIndex=Array.IndexOf(e.Args,"--live-verify");
            if(liveIndex>=0){var result=LiveVerification.Run(Path.GetFullPath(e.Args[liveIndex+1]));Shutdown(result);return;}
            var sandboxIndex = Array.IndexOf(e.Args, "--sandbox");
            IsSandbox = sandboxIndex >= 0;
            if(IsSandbox&&e.Args.Contains("--software-rendering"))System.Windows.Media.RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
            var dataRootIndex=Array.IndexOf(e.Args,"--data-root");
            bool maintenance=e.Args.Any(a=>a.StartsWith("--maintenance="));
            var root = IsSandbox ? Path.GetFullPath(e.Args[sandboxIndex + 1]) : DataPaths.Select(e.Args,maintenance);
            SandboxRoot=IsSandbox?root:null;
            var dataDirectory=IsSandbox?(dataRootIndex>=0?Path.GetFullPath(e.Args[dataRootIndex+1]):Path.Combine(root,"Data")):root;
            bool standard=!IsSandbox&&(dataRootIndex<0||DataPaths.IsInstalledData(root)||Path.GetFullPath(e.Args[dataRootIndex+1]).Equals(DataPaths.LegacyRoot,StringComparison.OrdinalIgnoreCase));
            var signature = DataPaths.Signature(standard?DataPaths.LegacyRoot:root);
            if(IsSandbox)Options=new StartupOptions(@"Software\DesktopNook\Sandbox\"+signature+@"\Run",@"Software\DesktopNook\Sandbox\"+signature+@"\Settings",Path.Combine(dataDirectory,"preferences.json"));
            else Options=new StartupOptions(preferencesPath:Path.Combine(root,"preferences.json"),fileOnly:true);
            if(maintenance){Shutdown(Maintenance.Run(e.Args,root,signature,IsSandbox));return;}
            _mutex = new Mutex(true, "Local\\DesktopNook-" + signature, out _ownsMutex);
            _activate = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\DesktopNook-Activate-" + signature);
            if (!_ownsMutex) { if(!e.Args.Contains("--autostart"))_activate.Set(); Shutdown(); return; }
            var desktops = IsSandbox ? new[] { Path.Combine(root, "Desktop") } : new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) };
            if(IsSandbox)Directory.CreateDirectory(desktops[0]);
            Recycle=IsSandbox?new SandboxRecycle(root):new WindowsRecycleBin();
            bool legacyHandoff=!IsSandbox&&e.Args.Contains("--resume-update")&&root.Equals(DataPaths.LegacyRoot,StringComparison.OrdinalIgnoreCase);
            if(!IsSandbox&&!legacyHandoff&&standard)DataPaths.Prepare(root,desktops,Recycle);
            var resumeIndex=Array.IndexOf(e.Args,"--resume-update");
            if(resumeIndex>=0)
            {
                var plan=System.Text.Json.JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(e.Args[resumeIndex+1]))??throw new IOException("更新确认记录为空。");
                var directory=dataDirectory;
                if(!string.Equals(Path.GetFullPath(plan.DataRoot),Path.GetFullPath(directory),StringComparison.OrdinalIgnoreCase))throw new IOException("更新后的数据目录与原目录不一致，已停止启动。");
                if(!string.IsNullOrEmpty(plan.StateFingerprint))
                {
                    var saved=System.Text.Json.JsonSerializer.Deserialize<NookState>(File.ReadAllText(Path.Combine(directory,"state.json")))??throw new IOException("原分类配置为空。");
                    if(LocalUpdater.Fingerprint(saved)!=plan.StateFingerprint)throw new IOException("更新前后分类配置不一致，已停止启动并保留记录。");
                }
                if(plan.CloseEndsProcess is bool closing&&Options.CloseEndsProcess!=closing)throw new IOException("更新前后关闭偏好不一致，已停止启动并保留记录。");
            }
            Options.PersistForUpdate();
            Store = new NookStore(dataDirectory, desktops, Recycle,storeAllPayloads:!IsSandbox||File.Exists(Path.Combine(dataDirectory,"storage.json")));
            UiMotion.Configure(Options.MotionMode);
            var window = new MainWindow(); MainWindow = window;
            if(!IsSandbox)Dispatcher.BeginInvoke(()=>{try{BrandMigration.Refresh(Store.DataDirectory);}catch(Exception){window.SetStatus("入口图标刷新未完成，可稍后重新打开轻匣重试。");}},System.Windows.Threading.DispatcherPriority.ContextIdle);
            window.KeepOpenForVerification=IsSandbox&&e.Args.Contains("--keep-open-verification");
            if(e.Args.Contains("--autostart")){window.ShowActivated=false;window.WindowState=WindowState.Minimized;}
            window.Show();
            File.WriteAllLines(Path.Combine(root,"instance.pid"),[Environment.ProcessId.ToString(),Environment.ProcessPath??""]);
            _stop=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\DesktopNook-Stop-"+signature);
            ThreadPool.RegisterWaitForSingleObject(_stop,(_,_)=>Dispatcher.BeginInvoke(()=>window.QuitForMaintenance()),null,Timeout.Infinite,false);
            if(!e.Args.Contains("--autostart"))_activate.WaitOne(0);
            ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => Dispatcher.BeginInvoke(() => window.TogglePanel()), null, Timeout.Infinite, false);
            var readyIndex=Array.IndexOf(e.Args,"--update-ready");if(readyIndex>=0)Dispatcher.BeginInvoke(()=>WriteUpdateReady(e.Args[readyIndex+1]),System.Windows.Threading.DispatcherPriority.Loaded);
            if(legacyHandoff)Dispatcher.BeginInvoke(async()=>await RelocateAfterHandoff(window,e.Args[Array.IndexOf(e.Args,"--resume-update")+1]),System.Windows.Threading.DispatcherPriority.ContextIdle);
            DispatcherUnhandledException += (_, args) => { args.Handled = true; window.ShowFailure(args.Exception); };
            if (e.Args.Contains("--ui-self-test")) Dispatcher.BeginInvoke(async () => await NativeVerification.Run(window, root), System.Windows.Threading.DispatcherPriority.Loaded);
            if (e.Args.Contains("--billiards-self-test")) Dispatcher.BeginInvoke(async () => await BilliardsVerification.Run(window, root), System.Windows.Threading.DispatcherPriority.Loaded);
            if (e.Args.Contains("--billiards-mouse-self-test")) Dispatcher.BeginInvoke(async () => await BilliardsVerification.Run(window, root), System.Windows.Threading.DispatcherPriority.Loaded);
            if (IsSandbox&&e.Args.Contains("--billiards-protocol-self-test")) Dispatcher.BeginInvoke(async () => await BilliardsVerification.Run(window, root,false), System.Windows.Threading.DispatcherPriority.Loaded);
            if (e.Args.Contains("--billiards-parent-probe")) Dispatcher.BeginInvoke(async () => await BilliardsVerification.ParentProbe(window, root), System.Windows.Threading.DispatcherPriority.Loaded);
            if (e.Args.Contains("--billiards-perf-test")) Dispatcher.BeginInvoke(async () => await BilliardsVerification.Performance(window, root), System.Windows.Threading.DispatcherPriority.Loaded);
            if (e.Args.Contains("--motion-visibility-self-test")) Dispatcher.BeginInvoke(async () => await MotionVisibilityVerification.Run(window, root), System.Windows.Threading.DispatcherPriority.Loaded);
            if (e.Args.Contains("--memory-self-test")) Dispatcher.BeginInvoke(async () => await ResidencyVerification.Run(window, root), System.Windows.Threading.DispatcherPriority.Loaded);
            if (e.Args.Contains("--size-self-test")) Dispatcher.BeginInvoke(async () => await SizingVerification.Run(window, root), System.Windows.Threading.DispatcherPriority.Loaded);
            if (e.Args.Contains("--order-self-test")) Dispatcher.BeginInvoke(async () => await OrderingVerification.Run(window, root), System.Windows.Threading.DispatcherPriority.Loaded);
            if (e.Args.Contains("--category-self-test")) Dispatcher.BeginInvoke(async () => await CategoryDeletionVerification.Run(window, root), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch (Exception ex) { MessageBox.Show("轻匣暂时无法打开。原文件和数据均保留。\n\n" + ex.Message, "轻匣", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }
    private static async Task RelocateAfterHandoff(MainWindow window,string plan)
    {
        var helper=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(plan))!,"DesktopNook.Updater.exe");
        while(!window.Dispatcher.HasShutdownStarted)
        {
            bool running=ProcessHelperRunning(helper);
            if(!running&&window.TryBeginStorageMove())break;
            await Task.Delay(250);
        }
        if(window.Dispatcher.HasShutdownStarted)return;
        try
        {
            window.SetStatus("正在将数据迁移到安装目录，原位置保留备份…");
            string target=DataPaths.InstallationRoot;var desktops=Store.DesktopRoots.ToArray();
            await Task.Run(()=>DataPaths.Prepare(target,desktops,Recycle));
            Store=new NookStore(target,desktops,Recycle,storeAllPayloads:true);
            Options=new StartupOptions(preferencesPath:Path.Combine(target,"preferences.json"),fileOnly:true);
            File.WriteAllLines(Path.Combine(target,"instance.pid"),[Environment.ProcessId.ToString(),Environment.ProcessPath!]);
            window.EndStorageMove();window.SetStatus("数据已迁移到安装目录，原位置保留备份");
        }
        catch(Exception ex){UiDialogs.Show(window,"数据迁移未完成，原目录和副本已保留。请退出后重开重试。\n"+ex.Message,"数据迁移");Application.Current.Shutdown(1);}
    }
    private static bool ProcessHelperRunning(string path)
    {
        foreach(var process in System.Diagnostics.Process.GetProcessesByName("DesktopNook.Updater"))using(process){try{if(string.Equals(process.MainModule?.FileName,path,StringComparison.OrdinalIgnoreCase))return true;}catch{}}
        return false;
    }
    private static void WriteUpdateReady(string path)
    {
        var temp=path+".tmp-"+Environment.ProcessId;
        File.WriteAllText(temp,System.Text.Json.JsonSerializer.Serialize(new{version=System.Reflection.Assembly.GetExecutingAssembly().GetName().Version!.ToString(),pid=Environment.ProcessId,stateFingerprint=LocalUpdater.Fingerprint(Store.State),closeEndsProcess=Options.CloseEndsProcess,categoryIds=Store.State.Categories.Select(c=>c.Id).ToArray(),dataRoot=Store.DataDirectory}));
        // Old updaters open this path immediately after File.Exists. Publishing a hard
        // link to the closed, complete file avoids the transient rename/delete handle
        // that can deny that first read on encrypted NTFS profiles. Keep the source
        // link in the update staging folder until normal update cleanup.
        if(!CreateHardLink(path,temp,IntPtr.Zero))File.Move(temp,path,true);
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll",EntryPoint="CreateHardLinkW",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true)]
    [return:System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName,string existingFileName,IntPtr securityAttributes);
    protected override void OnExit(ExitEventArgs e) { BilliardsController.Current?.Dispose();_stop?.Dispose();_activate?.Dispose(); if (_ownsMutex) _mutex?.ReleaseMutex(); _mutex?.Dispose(); base.OnExit(e); }
}

internal sealed class SandboxRecycle(string root) : IRecycleBin
{
    private string PathName => Path.Combine(root, "recycle-hidden.test");
    public bool IsVisible => !File.Exists(PathName);
    public Dictionary<string,int?> Capture() => new() { ["NewStartPanel"] = IsVisible ? 0 : 1, ["ClassicStartMenu"] = null };
    public void Hide() => File.WriteAllText(PathName,"test-only");
    public void Restore(Dictionary<string,int?> snapshot) { if(snapshot["NewStartPanel"] == 1) Hide(); else File.Delete(PathName); }
}
