using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DesktopNook.Games.Core;

namespace DesktopNook.Games.Network;
public sealed class GamePeer : IDisposable
{
    private readonly object _gate=new();
    private readonly GameInvite _invite;
    private readonly GameRoomHost? _host;
    private readonly CancellationTokenSource _stop=new();
    private readonly ConcurrentDictionary<long,TaskCompletionSource<NetPacket>> _acks=new();
    private NetConnection? _connection;
    private RoomView? _latest,_previous;
    private string _resume="",_transport="连接中";
    private long _request,_lastSound,_receivedAt;
    private int _disposed;
    private bool _terminal;
    private Task? _reader,_ping;
    public int Seat {get;}
    public GameRoomHost? Host=>_host;
    public event Action? Changed;
    public event Action<BallSound>? Sound;
    public string Status{get{lock(_gate)return _transport.Length>0?_transport:_latest?.Status??"等待连接";}}
    public RoomView? Latest{get{lock(_gate)return _latest;}}
    public bool Connected=>_host is not null?!_terminal:_connection is not null&&!_terminal;
    public bool CanAct
    {
        get{var view=Latest;return Connected&&view is {Connected:true,Started:true,Ended:false}&&!view.Round.Paused&&view.Round.Match.CurrentPlayer==Seat;}
    }
    public bool Animating
    {
        get{var r=Latest?.Round;return r is not null&&!r.Paused&&(r.Phase is RoundPhase.Rolling or RoundPhase.Stroke||r.Balls.Any(b=>b.Sunk&&b.ExitAge<.62));}
    }
    private GamePeer(GameInvite invite,int seat,GameRoomHost? host=null){_invite=invite;Seat=seat;_host=host;}
    public static GamePeer ForHost(GameRoomHost host)
    {
        var peer=new GamePeer(host.Invitation,0,host);host.Changed+=peer.Update;peer._transport="";peer.Update(host.Snapshot());return peer;
    }
    public static async Task<GamePeer> Join(GameInvite invite,CancellationToken token=default,Action<string>? progress=null)
    {
        invite.Validate();var peer=new GamePeer(invite,1);
        try
        {
            await peer.Connect(token,progress).ConfigureAwait(false);peer._reader=Task.Run(peer.Receive);peer._ping=Task.Run(peer.Ping);return peer;
        }
        catch{peer.Dispose();throw;}
    }
    private async Task Connect(CancellationToken token,Action<string>? progress=null)
    {
        Exception? last=null;
        foreach(var address in _invite.Addresses)
        {
            TcpClient? tcp=null;NetConnection? connection=null;
            try
            {
                token.ThrowIfCancellationRequested();tcp=new(IPAddress.Parse(address.Ip).AddressFamily){NoDelay=true};
                using(var connect=CancellationTokenSource.CreateLinkedTokenSource(token,_stop.Token))
                {
                    connect.CancelAfter(2200);await tcp.ConnectAsync(IPAddress.Parse(address.Ip),address.Port,connect.Token).ConfigureAwait(false);
                    var ssl=new SslStream(tcp.GetStream(),false,(_,certificate,_,_)=>
                    {
                        if(certificate is null)return false;
                        var expected=Convert.FromHexString(_invite.Fingerprint);var actual=SHA256.HashData(certificate.GetRawCertData());return CryptographicOperations.FixedTimeEquals(expected,actual);
                    });
                    connection=new(tcp,ssl);await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{TargetHost="Qingxia Friend Room",CertificateRevocationCheckMode=X509RevocationMode.NoCheck},connect.Token).ConfigureAwait(false);
                }
                await connection.Send(new(){Kind="Join",Room=_invite.Room,Token=_invite.Token,Resume=_resume,Name="好友"},token).ConfigureAwait(false);
                using var admission=CancellationTokenSource.CreateLinkedTokenSource(token,_stop.Token);admission.CancelAfter(65000);
                NetPacket welcome;
                while(true)
                {
                    welcome=await NetWire.Read(connection.Stream,admission.Token).ConfigureAwait(false);
                    if(welcome.Kind=="ApprovalPending"){progress?.Invoke(welcome.Message);continue;}break;
                }
                if(welcome.Kind!="Welcome"||welcome.Seat!=1||welcome.Resume.Length!=64||welcome.View is null)throw new IOException(welcome.Message.Length>0?welcome.Message:"房间未接受连接。");
                Validate(welcome.View);_resume=welcome.Resume;_connection=connection;_transport="";Update(welcome.View);return;
            }
            catch(Exception ex)when(ex is IOException or SocketException or System.Security.Authentication.AuthenticationException or OperationCanceledException or System.Text.Json.JsonException)
            {
                connection?.Dispose();tcp?.Dispose();if(token.IsCancellationRequested||_stop.IsCancellationRequested)throw;last=ex;
            }
        }
        throw new IOException("无法加入：请确认房间仍在、邀请未过期，并允许双方网络连接。"+(last is null?"":" "+last.Message),last);
    }
    private async Task Receive()
    {
        while(!_stop.IsCancellationRequested&&!_terminal)
        {
            try
            {
                var connection=_connection??throw new IOException("连接已中断。");
                while(!_stop.IsCancellationRequested&&!_terminal)
                {
                    var packet=await connection.Read().ConfigureAwait(false);
                    if(packet.Kind=="State"&&packet.View is not null)Update(packet.View);
                    else if(packet.Kind=="Ack"){if(_acks.TryRemove(packet.Request,out var waiter))waiter.TrySetResult(packet);}
                    else if(packet.Kind=="RoomClosed"){_terminal=true;SetTransport(packet.Message);return;}
                    else if(packet.Kind=="Error")throw new IOException(packet.Message);
                    else if(packet.Kind!="Pong")throw new InvalidDataException("未知联机消息。");
                }
            }
            catch(Exception ex)when(ex is not OutOfMemoryException)
            {
                _connection?.Dispose();_connection=null;foreach(var pending in _acks.Values)pending.TrySetException(new IOException("连接中断，正在同步对局。"));_acks.Clear();
                if(_stop.IsCancellationRequested||_terminal)break;
                long started=Stopwatch.GetTimestamp();SetTransport("连接中断，正在自动重连（最多 60 秒）");
                while(!_stop.IsCancellationRequested&&Stopwatch.GetElapsedTime(started).TotalSeconds<60)
                {
                    try{await Task.Delay(500,_stop.Token).ConfigureAwait(false);await Connect(_stop.Token).ConfigureAwait(false);break;}
                    catch(Exception retry)when(retry is IOException or OperationCanceledException){}
                }
                if(_connection is null){_terminal=true;SetTransport("重连未成功：房主可能已退出，或网络入站未放通。");}
            }
        }
    }
    private async Task Ping()
    {
        try{while(!_stop.IsCancellationRequested){await Task.Delay(2000,_stop.Token).ConfigureAwait(false);var connection=_connection;if(connection is not null)try{await connection.Send(new(){Kind="Ping"},_stop.Token).ConfigureAwait(false);}catch{connection.Dispose();}}}catch(OperationCanceledException){}
    }
    private void Validate(RoomView view)
    {
        if(view.Room!=_invite.Room||view.Round.Rule!=_invite.Rule||view.Sequence<0||view.Status.Length>220||view.Sounds.Length>96)throw new InvalidDataException("房间状态不正确。");
        BilliardsRound.ValidateView(view.Round);if(view.Sounds.Any(s=>s.Id<0||!Enum.IsDefined(s.Kind)||!double.IsFinite(s.Strength)||s.Strength is <0 or >1))throw new InvalidDataException("声音事件无效。");
    }
    private void Update(RoomView view)
    {
        Validate(view);List<BallSound> sounds=[];
        lock(_gate)
        {
            if(_latest is not null&&view.Sequence<=_latest.Sequence)return;
            _previous=_latest;_latest=view;_receivedAt=Stopwatch.GetTimestamp();if(view.Ended)_terminal=true;
            foreach(var sound in view.Sounds.OrderBy(s=>s.Id))if(sound.Id>_lastSound){_lastSound=sound.Id;sounds.Add(new(sound.Kind,sound.Strength));}
        }
        foreach(var sound in sounds)try{Sound?.Invoke(sound);}catch{}try{Changed?.Invoke();}catch{}
    }
    private void SetTransport(string status){lock(_gate)_transport=status;try{Changed?.Invoke();}catch{}}
    public async Task<NetPacket> Command(string kind,double x=0,double y=0,double power=0,long? request=null,int? round=null)
    {
        var view=Latest;if(view is null)return new(){Kind="Ack",Message="尚未连接房间"};
        if(!double.IsFinite(x)||!double.IsFinite(y)||!double.IsFinite(power))return new(){Kind="Ack",Message="操作数值无效"};
        var packet=new NetPacket{Kind=kind,Room=_invite.Room,Seat=Seat,Request=request??Interlocked.Increment(ref _request),Round=round??view.Round.Round,BaseShot=view.Round.Shots,X=x,Y=y,Power=power};
        if(_host is not null)return _host.Command(0,packet);
        if(_connection is not { } connection)return new(){Kind="Ack",Message="连接中断，正在等待重连"};
        var pending=new TaskCompletionSource<NetPacket>(TaskCreationOptions.RunContinuationsAsynchronously);if(!_acks.TryAdd(packet.Request,pending))return new(){Kind="Ack",Message="重复请求"};
        try{await connection.Send(packet,_stop.Token).ConfigureAwait(false);return await pending.Task.WaitAsync(TimeSpan.FromSeconds(4),_stop.Token).ConfigureAwait(false);}
        catch(Exception ex)when(ex is IOException or OperationCanceledException or TimeoutException){return new(){Kind="Ack",Request=packet.Request,Message="操作未确认，请等待状态恢复后再操作"};}
        finally{_acks.TryRemove(packet.Request,out _);}
    }
    public RoundView? ViewForRender()
    {
        lock(_gate)
        {
            if(_latest is null)return null;var current=_latest.Round;
            if(_previous is null||current.Round!=_previous.Round.Round||current.Shots!=_previous.Round.Shots)return current;
            double t=Math.Clamp(Stopwatch.GetElapsedTime(_receivedAt).TotalSeconds/.05,0,1);if(t>=1)return current;
            var balls=current.Balls.Select((b,n)=>
            {
                var a=_previous.Round.Balls[n];if(a.Sunk!=b.Sunk||Math.Abs(a.X-b.X)+Math.Abs(a.Y-b.Y)>400)return b;
                return b with{X=a.X+(b.X-a.X)*t,Y=a.Y+(b.Y-a.Y)*t,ExitAge=a.ExitAge+(b.ExitAge-a.ExitAge)*t};
            }).ToArray();return current with{Balls=balls};
        }
    }
    public void DropConnectionForVerification(){if(_host is not null)throw new InvalidOperationException("房主不能作为断线重连客户端。");_connection?.Dispose();}
    public void Dispose()
    {
        if(Interlocked.Exchange(ref _disposed,1)!=0)return;
        if(_host is not null){_host.Changed-=Update;_host.Dispose();}
        else if(_connection is not null&&!_terminal)try{Command("Leave").Wait(500);}catch{}
        _stop.Cancel();_connection?.Dispose();foreach(var pending in _acks.Values)pending.TrySetCanceled();
        try{Task.WhenAll(new[]{_reader,_ping}.Where(t=>t is not null).Cast<Task>()).Wait(1000);}catch{}
    }
}
