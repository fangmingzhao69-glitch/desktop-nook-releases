using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows;
using DesktopNook.Games.Core;

namespace DesktopNook.App;
internal static class BilliardsHost
{
    internal static async Task Run(string[] args,int index)
    {
        using var cancel=new CancellationTokenSource();BilliardsWindow? window=null;
        try
        {
            string name=args[index+1],nonce=args[index+2];int parentId=int.Parse(args[index+3]);long ticks=long.Parse(args[index+4]);
            using var parent=Process.GetProcessById(parentId);
            if(parent.StartTime.ToUniversalTime().Ticks!=ticks||!string.Equals(parent.MainModule?.FileName,Environment.ProcessPath,StringComparison.OrdinalIgnoreCase))throw new IOException("父进程身份不一致。");
            using var pipe=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(8000,cancel.Token);
            if(!GameWire.GetNamedPipeServerProcessId(pipe.SafePipeHandle,out var server)||server!=parentId)throw new IOException("游戏服务端身份不一致。");
            using var reader=new StreamReader(pipe,System.Text.Encoding.UTF8,false,4096,true);
            using var writer=new StreamWriter(pipe,new System.Text.UTF8Encoding(false),4096,true){AutoFlush=true};var writes=new SemaphoreSlim(1,1);
            async Task Send(GameMessage m){m.Nonce=nonce;await writes.WaitAsync(cancel.Token);try{await writer.WriteLineAsync(JsonSerializer.Serialize(m));}finally{writes.Release();}}
            using(var self=Process.GetCurrentProcess())await Send(new(){Kind="Hello",Pid=self.Id,StartTicks=self.StartTime.ToUniversalTime().Ticks});
            var start=await GameWire.Read(reader,cancel.Token);
            if(start?.Kind!="Start"||start.Nonce!=nonce||start.Players is not (1 or 2)||start.Icons.Count is <1 or >45||!BilliardsRules.Matches(start.Rule,start.Count)||start.Icons.Any(i=>string.IsNullOrWhiteSpace(i.Name)||i.Name.Length>4096||i.Path.Length>32768))throw new IOException("台球配置无效。");
            if(start.Online is { } online){if(start.Players!=2||online.Role is not ("host" or "join"))throw new IOException("好友配置无效。");if(!start.TestMode)online.LoopbackOnly=false;}
            ShellIntegration.SetAppId("DesktopNook.IconBilliards");
            window=new BilliardsWindow(start,Send);Application.Current.MainWindow=window;
            var closed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);window.Closed+=(_,_)=>closed.TrySetResult();
            window.Show();await Send(new(){Kind="Ready"});
            async Task Receive()
            {
                try{while(!cancel.IsCancellationRequested){var message=await GameWire.Read(reader,cancel.Token);if(message is null||message.Nonce!=nonce)break;if(message.Kind=="Close")break;if(message.Kind=="Activate")await window.Dispatcher.InvokeAsync(()=>{window.WindowState=WindowState.Normal;window.Activate();});if(start.TestMode&&message.Kind=="Test"){var response=await window.Dispatcher.InvokeAsync(()=>window.Test(message.Action));response.Kind="Probe";response.Request=message.Request;await Send(response);}}}
                catch(Exception){}finally{if(!cancel.IsCancellationRequested)await window.Dispatcher.InvokeAsync(()=>window.CloseFromOwner());}
            }
            async Task WatchParent(){try{await parent.WaitForExitAsync(cancel.Token);await window.Dispatcher.InvokeAsync(()=>window.CloseFromOwner());}catch(OperationCanceledException){} }
            _=Receive();_=WatchParent();await closed.Task;
            try{await Send(new(){Kind="Closed"});}catch{}
        }
        catch(Exception){window?.CloseFromOwner();}
        finally{cancel.Cancel();Application.Current.Shutdown();}
    }
}
