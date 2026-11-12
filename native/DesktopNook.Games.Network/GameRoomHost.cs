using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DesktopNook.Games.Core;

namespace DesktopNook.Games.Network;
public sealed class GameRoomHost : IDisposable
{
    public const int DefaultPort=38471;
    private readonly object _gate=new();
    private readonly TcpListener _listener;
    private readonly ECDsa _key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _stop=new();
    private readonly SemaphoreSlim _wake=new(0,1),_admissions=new(4,4);
    private readonly ConcurrentDictionary<TcpClient,byte> _sockets=new();
    private readonly BilliardsRound _round;
    private readonly string _inviteToken=GameInvite.Secret(),_resume=GameInvite.Secret();
    private readonly bool[] _ready=[false,false],_pause=[false,false],_resumeVotes=[false,false],_restart=[false,false];
    private readonly long[] _requests=[0,0];
    private readonly List<SoundView> _sounds=[];
    private long _sequence,_soundId,_lastPublish,_lastStep,_lostAt;
    private bool _connected,_hasSeat,_started,_ended,_bothPaused;
    private string _status="等待好友加入";
    private NetConnection? _guest;
    private int _disposed,_pendingApproval;
    private readonly TimeSpan _grace;
    private readonly Task _simulation,_accept;
    private LanAnnouncer? _announcer;
    public GameInvite Invitation {get;}
    public string LastTransportError {get;private set;}="";
    public int Port=>((IPEndPoint)_listener.LocalEndpoint).Port;
    public event Action<RoomView>? Changed;
    public Func<string,Task<bool>>? ApproveLanJoin {get;set;}
    public GameRoomHost(string rule,IPAddress? bind=null,int port=DefaultPort,TimeSpan? reconnectGrace=null)
    {
        if(!BilliardsRules.IsKnown(rule))throw new ArgumentException("球组无效。");
        _grace=reconnectGrace??TimeSpan.FromSeconds(60);_round=new(1,rule,2);_round.SetPaused(true);
        _round.Sound=s=>{_sounds.Add(new(++_soundId,s.Kind,s.Strength));if(_sounds.Count>96)_sounds.RemoveAt(0);};
        var request=new CertificateRequest("CN=Qingxia Friend Room",_key,HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,false));
        using(var generated=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-2),DateTimeOffset.UtcNow.AddHours(3)))
        {
            // Schannel cannot use an ephemeral ECDSA key. Import a temporary
            // user key container (without PersistKeySet), deleted on disposal.
            var pfx=generated.Export(X509ContentType.Pfx);try{_certificate=X509CertificateLoader.LoadPkcs12(pfx,null,X509KeyStorageFlags.UserKeySet);}finally{CryptographicOperations.ZeroMemory(pfx);}
        }
        IPAddress address=bind??(Socket.OSSupportsIPv6?IPAddress.IPv6Any:IPAddress.Any);
        try{_listener=Listen(address,port);}catch{_certificate.Dispose();_key.Dispose();throw;}string room=GameInvite.Secret(12);var endpoints=bind is not null?[new GameAddress(bind.ToString(),Port)]:LocalAddresses(Port);
        Invitation=new(1,GameInvite.RulesVersion,room,rule,DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds(),_certificate.GetCertHashString(HashAlgorithmName.SHA256),_inviteToken,endpoints);
        _lastStep=Stopwatch.GetTimestamp();_accept=Task.Run(AcceptLoop);_simulation=Task.Run(Simulate);
    }
    private static TcpListener Listen(IPAddress address,int port)
    {
        TcpListener Open(int p){var listener=new TcpListener(address,p);if(address.AddressFamily==AddressFamily.InterNetworkV6)listener.Server.DualMode=address.Equals(IPAddress.IPv6Any);listener.Start(4);return listener;}
        try{return Open(port);}catch(SocketException ex)when(port!=0&&ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied){return Open(0);}
    }
    public static GameAddress[] LocalAddresses(int port)
    {
        var ips=NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType!=NetworkInterfaceType.Loopback)
            .SelectMany(n=>n.GetIPProperties().UnicastAddresses).Select(a=>a.Address)
            .Where(a=>!IPAddress.IsLoopback(a)&&!a.IsIPv6LinkLocal&&!a.IsIPv6Multicast&&(a.AddressFamily==AddressFamily.InterNetwork||a.ToString().StartsWith('2')||a.ToString().StartsWith('3')))
            .Distinct().OrderByDescending(a=>a.AddressFamily==AddressFamily.InterNetworkV6).Take(12).Select(a=>new GameAddress(a.ToString(),port)).ToArray();
        return ips.Length>0?ips:[new("127.0.0.1",port)];
    }
    public void Announce(IPAddress? bind=null,int port=LanDiscovery.Port){_announcer=new(Invitation,bind,port,()=>{lock(_gate)return !_hasSeat&&!_ended;});}
    private async Task AcceptLoop()
    {
        try
        {
            while(!_stop.IsCancellationRequested)
            {
                var tcp=await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                if(!_admissions.Wait(0)){tcp.Dispose();continue;}_sockets[tcp]=0;
                _=Task.Run(async()=>{try{await Serve(tcp).ConfigureAwait(false);}finally{_sockets.TryRemove(tcp,out _);_admissions.Release();}});
            }
        }
        catch(Exception ex)when(ex is OperationCanceledException or SocketException or ObjectDisposedException){}
    }
    private async Task Serve(TcpClient tcp)
    {
        NetConnection? connection=null;
        try
        {
            tcp.NoDelay=true;var ssl=new SslStream(tcp.GetStream(),false);connection=new(tcp,ssl);
            using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                deadline.CancelAfter(8000);await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=_certificate,ClientCertificateRequired=false,EnabledSslProtocols=SslProtocols.None},deadline.Token).ConfigureAwait(false);
            }
            var hello=await connection.Read().ConfigureAwait(false);
            if(hello.Kind!="Join"||hello.Version!=1||hello.Rules!=GameInvite.RulesVersion||hello.Room!=Invitation.Room||hello.Name.Length>24||hello.Name.Any(char.IsControl)||hello.Token.Length>64||hello.Resume.Length>64)throw new InvalidDataException("加入信息无效。");
            bool recovering;
            lock(_gate){recovering=_hasSeat&&GameInvite.SameSecret(hello.Resume,_resume);if(_ended||(_hasSeat&&!recovering))throw new IOException("房间已有好友或本局已结束。");}
            if(!recovering&&DateTimeOffset.UtcNow.ToUnixTimeSeconds()>Invitation.Expires)throw new IOException("邀请已过期。");
            bool invited=GameInvite.SameSecret(hello.Token,_inviteToken);
            if(!recovering&&!invited)
            {
                var remote=((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
                if(hello.Token.Length!=0||!IsLocal(remote)||ApproveLanJoin is null||Interlocked.CompareExchange(ref _pendingApproval,1,0)!=0)throw new IOException("邀请凭证不正确。");
                try
                {
                    // A discovered room does not reveal the invitation secret.
                    await connection.Send(new(){Kind="ApprovalPending",Message="等待房主确认加入"}).ConfigureAwait(false);
                    if(!await ApproveLanJoin($"{hello.Name}（{remote}）").WaitAsync(TimeSpan.FromSeconds(60),_stop.Token).ConfigureAwait(false))throw new IOException("房主拒绝了加入请求。");
                }
                finally{Interlocked.Exchange(ref _pendingApproval,0);}
            }
            lock(_gate)
            {
                if(_ended||(_hasSeat&&!recovering))throw new IOException("座位已被占用。");
                var old=_guest;_guest=connection;_connected=true;_hasSeat=true;_lostAt=0;old?.Dispose();
                _status=recovering?"好友已重连":"好友已加入，等待双方准备";UpdatePause();
            }
            await connection.Send(new(){Kind="Welcome",Seat=1,Resume=_resume,View=Snapshot()}).ConfigureAwait(false);_=connection.Pump();Publish();
            long rateWindow=Environment.TickCount64;int received=0;while(!_stop.IsCancellationRequested)
            {
                var command=await connection.Read().ConfigureAwait(false);if(Environment.TickCount64-rateWindow>1000){received=0;rateWindow=Environment.TickCount64;}if(++received>40)throw new IOException("联机操作过于频繁。");if(command.Kind=="Ping"){await connection.Send(new(){Kind="Pong"}).ConfigureAwait(false);continue;}
                var ack=Command(1,command);await connection.Send(ack).ConfigureAwait(false);
            }
        }
        catch(Exception ex)when(ex is not OutOfMemoryException)
        {LastTransportError=ex.ToString();if(connection is not null)try{await connection.Send(new(){Kind="Error",Message=ex.Message}).ConfigureAwait(false);}catch{}}
        finally
        {
            lock(_gate)
            {
                if(ReferenceEquals(_guest,connection)){_guest=null;_connected=false;if(!_ended){_lostAt=Stopwatch.GetTimestamp();_status="好友断线，保留对局等待重连";UpdatePause();}}
            }
            connection?.Dispose();tcp.Dispose();Publish();
        }
    }
    private static bool IsLocal(IPAddress address)
    {
        if(address.IsIPv4MappedToIPv6)address=address.MapToIPv4();if(IPAddress.IsLoopback(address)||address.IsIPv6LinkLocal)return true;
        var b=address.GetAddressBytes();return b.Length==4&&(b[0]==10||b[0]==192&&b[1]==168||b[0]==172&&b[1] is >=16 and <=31||b[0]==100&&b[1] is >=64 and <=127)||b.Length==16&&(b[0]&0xfe)==0xfc;
    }
    public RoomView Snapshot(){lock(_gate)return View();}
    private RoomView View()=>new(_sequence,Invitation.Room,_connected,_started,_ended,_status,_ready[0],_ready[1],_round.CaptureView(),_sounds.ToArray());
    private void UpdatePause(){bool paused=!_started||!_connected||_ended||_bothPaused;if(paused!=_round.Paused)_lastStep=Stopwatch.GetTimestamp();_round.SetPaused(paused);}
    public NetPacket Command(int seat,NetPacket command)
    {
        NetPacket ack=new(){Kind="Ack",Request=command.Request};
        lock(_gate)
        {
            if(seat is <0 or >1||command.Room!=Invitation.Room||command.Request<=_requests[Math.Clamp(seat,0,1)]||command.Request>1000000){ack.Message="重复或无效的操作";return ack;}
            _requests[seat]=command.Request;
            if(_ended){ack.Message="本局已结束";return ack;}
            bool finite=double.IsFinite(command.X)&&double.IsFinite(command.Y)&&double.IsFinite(command.Power);
            switch(command.Kind)
            {
                case "Ready":_ready[seat]=true;if(_ready.All(v=>v)&&_connected){_started=true;_status="双方已准备，玩家 1 开球";}else _status=$"玩家 {seat+1} 已准备，等待好友";ack.Accept=true;break;
                case "Shot":
                    if(CanPlay(seat,command)&&finite&&command.Power is >=0 and <=1&&command.X is >=0 and <=1600&&command.Y is >=0 and <=900)ack.Accept=_round.Shoot(new(command.X,command.Y),command.Power);break;
                case "Place":if(CanPlay(seat,command)&&finite)ack.Accept=_round.PlaceWhite(new(command.X,command.Y));break;
                case "Pause":_pause[seat]=true;if(_pause.All(v=>v)){_bothPaused=true;_status="双方已同意暂停";}else _status=$"玩家 {seat+1} 请求暂停，另一方点击暂停同意";ack.Accept=true;break;
                case "Resume":_resumeVotes[seat]=true;if(_resumeVotes.All(v=>v)){_bothPaused=false;Array.Fill(_pause,false);Array.Fill(_resumeVotes,false);_status="双方已继续";}else _status=$"玩家 {seat+1} 请求继续";ack.Accept=true;break;
                case "Restart":_restart[seat]=true;if(_restart.All(v=>v)){_round.Reset(1,Invitation.Rule,2);Array.Fill(_ready,false);Array.Fill(_pause,false);Array.Fill(_resumeVotes,false);Array.Fill(_restart,false);_started=false;_bothPaused=false;_status="双方同意重开，请重新阅读规则并准备";}else _status=$"玩家 {seat+1} 请求重开，另一方点击重开同意";ack.Accept=true;break;
                case "Leave":_ended=true;_status=$"玩家 {seat+1} 已离开，本局结束";ack.Accept=true;break;
            }
            UpdatePause();if(!ack.Accept)ack.Message="当前不是你的可操作回合，或操作已过期";else if(command.Kind is "Shot" or "Place"){_lastStep=Stopwatch.GetTimestamp();_status=_round.Match.LastMessage;}
        }
        Publish();return ack;
    }
    private bool CanPlay(int seat,NetPacket command)=>_connected&&_started&&!_bothPaused&&!_round.Paused&&command.Round==_round.RoundNumber&&command.BaseShot==_round.Shots&&_round.Match.CurrentPlayer==seat;
    internal void MutateForVerification(Action<BilliardsRound> mutation){lock(_gate)mutation(_round);Publish();}
    private async Task Simulate()
    {
        double accumulated=0;
        try
        {
            while(!_stop.IsCancellationRequested)
            {
                bool moving;lock(_gate)
                {
                    long now=Stopwatch.GetTimestamp();double elapsed=Stopwatch.GetElapsedTime(_lastStep,now).TotalSeconds;_lastStep=now;
                    if(!_connected&&_hasSeat&&_lostAt!=0&&Stopwatch.GetElapsedTime(_lostAt).TotalSeconds>=_grace.TotalSeconds){_ended=true;_status="重连等待超时，本局结束";UpdatePause();}
                    moving=_round.NeedsFrames;
                    if(moving){accumulated+=Math.Min(elapsed,.12);for(int n=0;accumulated>=1d/240&&n<32;n++){_round.Advance(1d/240);accumulated-=1d/240;}if(!_round.NeedsFrames)_status=_round.Match.LastMessage;}
                    else{accumulated=0;if(!_round.Paused)_round.Advance(elapsed);}
                }
                long stamp=Stopwatch.GetTimestamp();if(Stopwatch.GetElapsedTime(_lastPublish,stamp).TotalMilliseconds>=(moving?50:1000))Publish();
                await _wake.WaitAsync(moving?8:250,_stop.Token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException){}
    }
    private void Publish()
    {
        RoomView view;NetConnection? guest;lock(_gate){if(_disposed!=0&& !_ended)return;_sequence++;_lastPublish=Stopwatch.GetTimestamp();view=View();guest=_guest;}
        try{Changed?.Invoke(view);}catch{}guest?.State(view);try{if(_wake.CurrentCount==0)_wake.Release();}catch(SemaphoreFullException){}
    }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref _disposed,1)!=0)return;
        lock(_gate){_ended=true;_status="房主已关闭房间";UpdatePause();}
        if(_guest is { } guest)try{using var limit=new CancellationTokenSource(500);guest.Send(new(){Kind="RoomClosed",Message="房主已关闭房间"},limit.Token).GetAwaiter().GetResult();}catch{}
        _stop.Cancel();_listener.Stop();_announcer?.Dispose();foreach(var socket in _sockets.Keys)socket.Dispose();_guest?.Dispose();
        try{Task.WhenAll(_accept,_simulation).Wait(1500);}catch{} _certificate.Dispose();_key.Dispose();
    }
}
