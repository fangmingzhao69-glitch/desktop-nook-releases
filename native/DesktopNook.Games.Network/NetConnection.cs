using System.Net.Security;
using System.Net.Sockets;
using System.Threading.Channels;

namespace DesktopNook.Games.Network;
internal sealed class NetConnection(TcpClient tcp,SslStream stream) : IDisposable
{
    internal readonly TcpClient Tcp=tcp;
    internal readonly SslStream Stream=stream;
    internal readonly CancellationTokenSource Cancel=new();
    private readonly SemaphoreSlim _writes=new(1,1);
    private readonly Channel<NetPacket> _states=Channel.CreateBounded<NetPacket>(new BoundedChannelOptions(3){FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true});
    private int _disposed;
    internal async Task Send(NetPacket packet,CancellationToken token=default)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(Cancel.Token,token);timeout.CancelAfter(2500);
        await _writes.WaitAsync(timeout.Token).ConfigureAwait(false);
        try{await NetWire.Write(Stream,packet,timeout.Token).ConfigureAwait(false);}finally{_writes.Release();}
    }
    internal void State(RoomView view)=>_states.Writer.TryWrite(new(){Kind="State",View=view});
    internal async Task Pump()
    {
        try{await foreach(var packet in _states.Reader.ReadAllAsync(Cancel.Token).ConfigureAwait(false))await Send(packet).ConfigureAwait(false);}
        catch(Exception){Dispose();}
    }
    internal async Task<NetPacket> Read()
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(Cancel.Token);timeout.CancelAfter(10000);
        return await NetWire.Read(Stream,timeout.Token).ConfigureAwait(false);
    }
    public void Dispose(){if(Interlocked.Exchange(ref _disposed,1)!=0)return;Cancel.Cancel();_states.Writer.TryComplete();Tcp.Dispose();Stream.Dispose();}
}
