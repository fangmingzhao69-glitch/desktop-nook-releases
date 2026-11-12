using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DesktopNook.Games.Core;

namespace DesktopNook.Games.Network;
public sealed record DiscoveredRoom(string Room,string Rule,string Ip,int Port,string Fingerprint)
{
    public GameInvite Invitation()=>new(1,GameInvite.RulesVersion,Room,Rule,DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds(),Fingerprint,"",[new(Ip,Port)]);
    public override string ToString()=>$"{BilliardsRules.Get(Rule).Name} · {Ip}";
}
internal sealed record DiscoveryQuery(string Type,int Version,string Nonce);
internal sealed record DiscoveryReply(string Type,int Version,string Rules,string Nonce,string Room,string Rule,int Port,string Fingerprint);
public static class LanDiscovery
{
    public const int Port=38472;
    public static async Task<IReadOnlyList<DiscoveredRoom>> Find(int port=Port,IPAddress? destination=null,CancellationToken token=default)
    {
        using var udp=new UdpClient(AddressFamily.InterNetwork){EnableBroadcast=true};using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(1400);
        string nonce=GameInvite.Secret(8);var query=JsonSerializer.SerializeToUtf8Bytes(new DiscoveryQuery("qingxia-discover",1,nonce));
        var rooms=new Dictionary<string,DiscoveredRoom>();
        try
        {
            await udp.SendAsync(query,new IPEndPoint(destination??IPAddress.Broadcast,port),deadline.Token).ConfigureAwait(false);
            if(destination is null)await udp.SendAsync(query,new IPEndPoint(IPAddress.Loopback,port),deadline.Token).ConfigureAwait(false);
            while(!deadline.IsCancellationRequested)
            {
                var packet=await udp.ReceiveAsync(deadline.Token).ConfigureAwait(false);if(packet.Buffer.Length>4096)continue;
                try
                {
                    var reply=JsonSerializer.Deserialize<DiscoveryReply>(packet.Buffer);if(reply?.Type!="qingxia-room"||reply.Version!=1||reply.Rules!=GameInvite.RulesVersion||reply.Nonce!=nonce)continue;
                    var room=new DiscoveredRoom(reply.Room,reply.Rule,packet.RemoteEndPoint.Address.ToString(),reply.Port,reply.Fingerprint);room.Invitation().Validate();
                    if(rooms.Count<32)rooms[room.Room+room.Ip]=room;
                }
                catch(Exception ex)when(ex is JsonException or InvalidDataException or NullReferenceException){}
            }
        }
        catch(OperationCanceledException)when(!token.IsCancellationRequested){}
        return rooms.Values.ToArray();
    }
}
public sealed class LanAnnouncer : IDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cancel=new();
    private readonly Task _task;
    public int Port=>((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    public LanAnnouncer(GameInvite invite,IPAddress? bind=null,int port=LanDiscovery.Port,Func<bool>? available=null)
    {
        _udp=new(AddressFamily.InterNetwork);try{_udp.Client.SetSocketOption(SocketOptionLevel.Socket,SocketOptionName.ReuseAddress,true);_udp.Client.Bind(new IPEndPoint(bind??IPAddress.Any,port));}catch{_udp.Dispose();throw;}
        _task=Task.Run(async()=>
        {
            long window=Environment.TickCount64;int count=0;
            try
            {
                while(!_cancel.IsCancellationRequested)
                {
                    var input=await _udp.ReceiveAsync(_cancel.Token).ConfigureAwait(false);if(input.Buffer.Length>512||available?.Invoke()==false)continue;
                    if(Environment.TickCount64-window>1000){window=Environment.TickCount64;count=0;}if(++count>24)continue;
                    try
                    {
                        var query=JsonSerializer.Deserialize<DiscoveryQuery>(input.Buffer);if(query?.Type!="qingxia-discover"||query.Version!=1||query.Nonce.Length!=16)continue;
                        var response=new DiscoveryReply("qingxia-room",1,GameInvite.RulesVersion,query.Nonce,invite.Room,invite.Rule,invite.Addresses[0].Port,invite.Fingerprint);
                        await _udp.SendAsync(JsonSerializer.SerializeToUtf8Bytes(response),input.RemoteEndPoint,_cancel.Token).ConfigureAwait(false);
                    }
                    catch(Exception ex)when(ex is JsonException or NullReferenceException){}
                }
            }
            catch(Exception ex)when(ex is OperationCanceledException or SocketException or ObjectDisposedException){}
        });
    }
    public void Dispose(){_cancel.Cancel();_udp.Dispose();try{_task.Wait(500);}catch{}}
}
