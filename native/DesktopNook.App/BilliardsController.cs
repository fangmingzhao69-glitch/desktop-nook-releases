using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32.SafeHandles;

namespace DesktopNook.App;
internal static class GameWire
{
    [DllImport("kernel32.dll",SetLastError=true)]internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint pid);
    [DllImport("kernel32.dll",SetLastError=true)]internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint pid);
    internal static async Task<GameMessage?> Read(StreamReader reader,CancellationToken token)
    {
        var line=await reader.ReadLineAsync(token);if(line is null)return null;
        if(line.Length>2*1024*1024)throw new IOException("游戏消息超过上限。");
        return JsonSerializer.Deserialize<GameMessage>(line)??throw new IOException("游戏消息为空。");
    }
}
internal sealed class BilliardsController : IDisposable
{
    private sealed class Session(Process process,NamedPipeServerStream pipe,string nonce,GameSettings settings)
    {
        internal readonly Process Process=process;
        internal readonly long StartTicks=process.StartTime.ToUniversalTime().Ticks;
        internal readonly NamedPipeServerStream Pipe=pipe;
        internal readonly string Nonce=nonce;
        internal readonly GameSettings Settings=settings;
        internal readonly CancellationTokenSource Cancel=new();
        internal readonly SemaphoreSlim Writes=new(1,1);
        internal readonly TaskCompletionSource Ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal StreamWriter? Writer;
        internal Task? ReadTask;
        internal bool Cleaned;
        internal readonly HashSet<int> Recorded=[];
        internal readonly System.Collections.Concurrent.ConcurrentDictionary<int,TaskCompletionSource<GameMessage>> Probes=new();
        internal int Request;
    }
    private Session? _session;
    private readonly GameStorage _storage;
    internal static BilliardsController? Current {get;private set;}
    internal bool Running{get{var s=_session;if(s is null)return false;try{return !s.Process.HasExited;}catch(InvalidOperationException){return false;}}}
    internal int? ChildPid{get{var s=_session;try{return s is not null&&!s.Process.HasExited?s.Process.Id:null;}catch(InvalidOperationException){return null;}}}
    internal string? LastError {get;private set;}
    internal BilliardsController(string dataRoot){_storage=new(dataRoot);Current=this;}
    internal async Task Launch(List<GameIcon> icons,GameSettings settings,Rect work,bool test=false)
    {
        if(Running){Activate();return;}
        Stop();
        if(icons.Count is <1 or >45||settings.Count is <15 or >45)throw new IOException("没有可用图标，或数量不符合范围。");
        string name="DesktopNook.Billiards."+Guid.NewGuid().ToString("N"),nonce=Guid.NewGuid().ToString("N")+Guid.NewGuid().ToString("N");
        var pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        using var parent=Process.GetCurrentProcess();
        var info=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Normal};
        foreach(var arg in new[]{"--icon-billiards",name,nonce,parent.Id.ToString(),parent.StartTime.ToUniversalTime().Ticks.ToString()})info.ArgumentList.Add(arg);
        Session? session=null;
        try
        {
            var process=Process.Start(info)??throw new IOException("无法启动台球。");session=new(process,pipe,nonce,settings);_session=session;LastError=null;
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(session.Cancel.Token);timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await pipe.WaitForConnectionAsync(timeout.Token);
            if(!GameWire.GetNamedPipeClientProcessId(pipe.SafePipeHandle,out var pid)||pid!=process.Id)throw new IOException("游戏进程身份不一致。");
            var reader=new StreamReader(pipe,System.Text.Encoding.UTF8,false,4096,true);session.Writer=new StreamWriter(pipe,new System.Text.UTF8Encoding(false),4096,true){AutoFlush=true};
            var hello=await GameWire.Read(reader,timeout.Token);
            if(hello?.Kind!="Hello"||hello.Nonce!=nonce||hello.Pid!=process.Id||hello.StartTicks!=session.StartTicks)throw new IOException("游戏会话认证失败。");
            await Send(session,new(){Kind="Start",Icons=icons,Count=settings.Count,Guide=settings.Guide,Left=work.Left,Top=work.Top,Width=work.Width,Height=work.Height,TestMode=test},timeout.Token);
            session.ReadTask=Task.Run(()=>ReadLoop(session,reader));
            await session.Ready.Task.WaitAsync(timeout.Token);
            if(!ReferenceEquals(_session,session))throw new OperationCanceledException();
            _storage.SaveSettings(settings);
        }
        catch{if(session is null)pipe.Dispose();else if(ReferenceEquals(_session,session))Stop();throw;}
    }
    private async Task ReadLoop(Session session,StreamReader reader)
    {
        try
        {
            while(!session.Cancel.IsCancellationRequested)
            {
                var msg=await GameWire.Read(reader,session.Cancel.Token);if(msg is null)break;
                if(msg.Nonce!=session.Nonce)throw new IOException("游戏会话已失效。");
                switch(msg.Kind)
                {
                    case "Ready":session.Ready.TrySetResult();break;
                    case "SettingsChanged":session.Settings.Guide=msg.Guide;_storage.SaveSettings(session.Settings);break;
                    case "RoundFinished":
                        if(msg.Round>0&&msg.Count==session.Settings.Count&&msg.Shots is >0 and <=100000&&double.IsFinite(msg.Milliseconds)&&msg.Milliseconds is >0 and <=604800000&&session.Recorded.Add(msg.Round))
                            _storage.SaveScore(new(DateTime.UtcNow,session.Settings.Source,msg.Count,msg.Shots,msg.Milliseconds));
                        break;
                    case "Closed":return;
                    case "Probe":if(session.Probes.TryRemove(msg.Request,out var probe))probe.TrySetResult(msg);break;
                    default:throw new IOException("未知游戏消息。");
                }
            }
        }
        catch(OperationCanceledException){}
        catch(Exception ex){LastError=ex.Message;session.Ready.TrySetException(ex);}
        finally{session.Ready.TrySetException(new IOException("台球进程已退出。"));session.Cancel.Cancel();session.Pipe.Dispose();reader.Dispose();_=Task.Run(async()=>{try{await session.Process.WaitForExitAsync();lock(session){Interlocked.CompareExchange(ref _session,null,session);Cleanup(session);}}catch(InvalidOperationException){} });}
    }
    private static async Task Send(Session session,GameMessage message,CancellationToken token=default)
    {
        message.Nonce=session.Nonce;await session.Writes.WaitAsync(token).ConfigureAwait(false);
        try{if(session.Writer is not null)await session.Writer.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(),token).ConfigureAwait(false);}finally{session.Writes.Release();}
    }
    internal void Activate(){if(_session is { } s)_=SafeSend(s,new(){Kind="Activate"});}
    internal async Task<GameMessage> Probe(string action="state")
    {
        var s=_session??throw new IOException("游戏未运行。");int request=Interlocked.Increment(ref s.Request);var ready=new TaskCompletionSource<GameMessage>(TaskCreationOptions.RunContinuationsAsynchronously);s.Probes[request]=ready;
        try{await Send(s,new(){Kind="Test",Action=action,Request=request},s.Cancel.Token);return await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));}finally{s.Probes.TryRemove(request,out _);}
    }
    private static async Task SafeSend(Session s,GameMessage m){try{await Send(s,m,s.Cancel.Token);}catch(Exception){} }
    internal void Stop()
    {
        var session=_session;if(session is null)return;
        lock(session)
        {
            if(session.Cleaned){Interlocked.CompareExchange(ref _session,null,session);return;}
            if(!session.Process.HasExited)
            {
                try{Send(session,new(){Kind="Close"}).Wait(400);}catch{}
                if(!session.Process.WaitForExit(2500))
                {
                    if(session.Process.StartTime.ToUniversalTime().Ticks!=session.StartTicks||!string.Equals(session.Process.MainModule?.FileName,Environment.ProcessPath,StringComparison.OrdinalIgnoreCase))throw new IOException("不能确认待退出的游戏进程身份。");
                    session.Process.Kill();if(!session.Process.WaitForExit(1500))throw new IOException("游戏进程尚未退出。");
                }
            }
            if(session.ReadTask is { } read&&!read.IsCompleted&&!read.Wait(1500))throw new IOException("正在保存本局成绩，请稍后再试。");
            Interlocked.CompareExchange(ref _session,null,session);Cleanup(session);
        }
    }
    private static void Cleanup(Session s){if(s.Cleaned)return;s.Cleaned=true;s.Cancel.Cancel();s.Pipe.Dispose();s.Process.Dispose();}
    public void Dispose(){Stop();if(ReferenceEquals(Current,this))Current=null;}
}
