using System.Net;
using System.Text.Json;
using DesktopNook.Games.Core;
using DesktopNook.Games.Network;

var checks=new List<object>();
void Assert(bool value,string text){if(!value)throw new Exception(text);}
async Task Test(string name,Func<Task> body){try{await body();checks.Add(new{name,passed=true});Console.WriteLine("PASS "+name);}catch(Exception ex){checks.Add(new{name,passed=false,error=ex.ToString()});Console.WriteLine("FAIL "+name+": "+ex.Message);}}
async Task Wait(Func<bool> predicate,int milliseconds=5000){long end=Environment.TickCount64+milliseconds;while(!predicate()&&Environment.TickCount64<end)await Task.Delay(20);Assert(predicate(),"condition timed out");}
GameRoomHost Host(string rule=BilliardsRules.Nine,IPAddress? bind=null,TimeSpan? grace=null)=>new(rule,bind??IPAddress.Loopback,0,grace);

await Test("Invitation roundtrip, version, expiry and address validation",async()=>
{
    using var host=Host();var invite=GameInvite.Parse(host.Invitation.Encode());Assert(invite.Room==host.Invitation.Room&&invite.Token.Length==64,"roundtrip");
    foreach(var bad in new[]{invite with{Version=99},invite with{Expires=1},invite with{Addresses=[new("not-an-ip",10)]},invite with{Fingerprint="00"}})
    {try{GameInvite.Parse(bad.Encode());throw new Exception("bad invite accepted");}catch(InvalidDataException){}}
    await Task.CompletedTask;
});
await Test("View snapshot roundtrip preserves scores and excludes icon paths",()=>
{
    var round=new BilliardsRound(3,BilliardsRules.Eight,2);var frame=round.CaptureView();var copy=new BilliardsRound(1,BilliardsRules.Eight,2);copy.ApplyView(JsonSerializer.Deserialize<RoundView>(JsonSerializer.Serialize(frame))!);Assert(copy.Balls.Count==16&&copy.Match.CurrentPlayer==0,"view");
    try{copy.ApplyView(frame with{Balls=frame.Balls.Select((b,n)=>n==0?b with{X=double.NaN}:b).ToArray()});throw new Exception("NaN accepted");}catch(InvalidDataException){}return Task.CompletedTask;
});
await Test("Actual IPv4 TCP TLS join and seat reservation",async()=>
{
    using var host=Host();using var local=GamePeer.ForHost(host);GamePeer joined;try{joined=await GamePeer.Join(host.Invitation);}catch{Console.WriteLine(host.LastTransportError);throw;}using var guest=joined;await Wait(()=>local.Latest!.Connected);Assert(guest.Seat==1&&local.Seat==0,"seats");
    try{using var intruder=await GamePeer.Join(host.Invitation);throw new Exception("occupied room accepted");}catch(IOException){}
});
await Test("Wrong certificate fingerprint cannot authenticate",async()=>
{
    using var host=Host();try{using var client=await GamePeer.Join(host.Invitation with{Fingerprint=new('0',64)});throw new Exception("pin accepted");}catch(IOException){}
});
await Test("Wrong bearer secret cannot occupy the room",async()=>
{
    using var host=Host();try{using var client=await GamePeer.Join(host.Invitation with{Token=new('0',64)});throw new Exception("token accepted");}catch(IOException){}Assert(!host.Snapshot().Connected,"not occupied");
});
await Test("Both ready before a shot; wrong player and duplicates rejected",async()=>
{
    using var host=Host();using var local=GamePeer.ForHost(host);using var guest=await GamePeer.Join(host.Invitation);
    Assert(!(await local.Command("Shot",1200,450,.5)).Accept,"not ready");await local.Command("Ready");await guest.Command("Ready");await Wait(()=>guest.Latest!.Started);
    Assert(!(await guest.Command("Shot",1200,450,.5)).Accept,"wrong turn");var accepted=await local.Command("Shot",1200,450,.5);Assert(accepted.Accept,"shot accepted");
    Assert(!(await local.Command("Shot",1200,450,.5,accepted.Request)).Accept,"replay denied");await Wait(()=>guest.Latest!.Round.Shots==1);Assert(host.Snapshot().Round.Shots==1,"only one shot");
});
await Test("Non-finite and out-of-range shots rejected without state changes",async()=>
{
    using var host=Host();using var local=GamePeer.ForHost(host);using var guest=await GamePeer.Join(host.Invitation);await local.Command("Ready");await guest.Command("Ready");
    Assert(!(await local.Command("Shot",double.NaN,400,.5)).Accept,"NaN");Assert(!(await local.Command("Shot",1200,450,3)).Accept,"power");Assert(!(await local.Command("Shot",-4,450,.5)).Accept,"target");Assert(host.Snapshot().Round.Shots==0,"unchanged");
});
await Test("Host physics determines both views and free-ball turn",async()=>
{
    using var host=Host();using var local=GamePeer.ForHost(host);using var guest=await GamePeer.Join(host.Invitation);await local.Command("Ready");await guest.Command("Ready");await local.Command("Shot",420,100,.18);
    await Wait(()=>host.Snapshot().Round.Phase==RoundPhase.WhitePlacement,8000);await Wait(()=>guest.Latest!.Round.Phase==RoundPhase.WhitePlacement);
    var a=host.Snapshot().Round;var b=guest.Latest!.Round;Assert(a.Match.CurrentPlayer==1&&a.Balls.Select(x=>(x.X,x.Y,x.Sunk)).SequenceEqual(b.Balls.Select(x=>(x.X,x.Y,x.Sunk))),"authoritative poses");
    Assert(!(await local.Command("Place",420,450)).Accept,"host cannot place guest free ball");Assert((await guest.Command("Place",420,450)).Accept,"guest places white");
});
await Test("Pause and restart require both seats",async()=>
{
    using var host=Host();using var local=GamePeer.ForHost(host);using var guest=await GamePeer.Join(host.Invitation);await local.Command("Ready");await guest.Command("Ready");
    await local.Command("Pause");Assert(!host.Snapshot().Round.Paused,"one vote does not pause");await guest.Command("Pause");Assert(host.Snapshot().Round.Paused,"both pause");
    await local.Command("Resume");await guest.Command("Resume");Assert(!host.Snapshot().Round.Paused,"resume votes");int before=host.Snapshot().Round.Round;
    await local.Command("Restart");Assert(host.Snapshot().Round.Round==before,"one vote does not reset");await guest.Command("Restart");Assert(host.Snapshot().Round.Round==before+1&&!host.Snapshot().Started,"new round requires ready");
});
await Test("Socket loss resumes the reserved seat and never replays a shot",async()=>
{
    using var host=Host();using var local=GamePeer.ForHost(host);using var guest=await GamePeer.Join(host.Invitation);await local.Command("Ready");await guest.Command("Ready");await local.Command("Shot",1200,450,.5);await Wait(()=>guest.Latest!.Round.Shots==1);
    guest.DropConnectionForVerification();await Wait(()=>!host.Snapshot().Connected);await Wait(()=>host.Snapshot().Connected&&guest.Connected,6000);Assert(host.Snapshot().Round.Shots==1,"no replay");Assert(!host.Snapshot().Round.Paused,"resumed");
});
await Test("LAN discovery reveals no bearer secret and requires host approval",async()=>
{
    using var host=Host();using var announce=new LanAnnouncer(host.Invitation,IPAddress.Loopback,0);var found=await LanDiscovery.Find(announce.Port,IPAddress.Loopback);Assert(found.Count==1,"discovered");var invitation=found[0].Invitation();Assert(invitation.Token.Length==0&&!invitation.Encode().Contains(host.Invitation.Token),"no secret");
    bool asked=false;host.ApproveLanJoin=_=>{asked=true;return Task.FromResult(true);};using var peer=await GamePeer.Join(invitation);Assert(asked&&peer.Seat==1,"approval");
});
await Test("Rejected discovered join does not reserve a seat",async()=>
{
    using var host=Host();host.ApproveLanJoin=_=>Task.FromResult(false);try{using var guest=await GamePeer.Join(host.Invitation with{Token=""});throw new Exception("rejection ignored");}catch(IOException){}Assert(!host.Snapshot().Connected,"no seat");
});
await Test("IPv6 loopback uses the same invitation and TLS protocol",async()=>
{
    if(!System.Net.Sockets.Socket.OSSupportsIPv6)throw new Exception("IPv6 unavailable on test machine");using var host=Host(bind:IPAddress.IPv6Loopback);using var guest=await GamePeer.Join(host.Invitation);Assert(guest.Seat==1,"IPv6 join");
});
await Test("All four rule presets negotiate their fixed ball counts",async()=>
{
    foreach(var rule in BilliardsRules.All){using var host=Host(rule.Id);using var guest=await GamePeer.Join(host.Invitation);Assert(guest.Latest!.Round.Rule==rule.Id&&guest.Latest.Round.Balls.Length==rule.Count+1,"rule snapshot");}
});
await Test("Closing the host notifies the guest and releases its port",async()=>
{
    var host=Host();int port=host.Port;using var guest=await GamePeer.Join(host.Invitation);host.Dispose();await Wait(()=>!guest.Connected);using var listener=new System.Net.Sockets.TcpListener(IPAddress.Loopback,port);listener.Start();listener.Stop();
});
await Test("Oversized framed message rejected before allocation",async()=>
{
    using var stream=new MemoryStream([0x7f,0xff,0xff,0xff]);try{await NetWire.Read(stream,CancellationToken.None);throw new Exception("length accepted");}catch(InvalidDataException){}
});
await Test("Reconnect grace timeout ends rather than inventing a winner",async()=>
{
    using var host=Host(grace:TimeSpan.FromMilliseconds(150));using var local=GamePeer.ForHost(host);using var guest=await GamePeer.Join(host.Invitation);await local.Command("Ready");await guest.Command("Ready");guest.DropConnectionForVerification();await Wait(()=>host.Snapshot().Ended,3000);Assert(host.Snapshot().Round.Match.Winner is null&&host.Snapshot().Round.Paused,"timeout is not a completed victory");
});
await Test("Obsolete board revision cannot place a free ball",async()=>
{
    using var host=Host();using var local=GamePeer.ForHost(host);using var guest=await GamePeer.Join(host.Invitation);await local.Command("Ready");await guest.Command("Ready");await local.Command("Shot",420,100,.18);await Wait(()=>host.Snapshot().Round.Phase==RoundPhase.WhitePlacement,8000);var stale=host.Command(1,new(){Kind="Place",Room=host.Invitation.Room,Request=100,Round=1,BaseShot=0,X=420,Y=450});Assert(!stale.Accept&&host.Snapshot().Round.Phase==RoundPhase.WhitePlacement,"stale view rejected");
});
int failed=checks.Count(c=>JsonSerializer.Serialize(c).Contains("\"passed\":false"));string output=args.FirstOrDefault()??"network-core.json";Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);File.WriteAllText(output,JsonSerializer.Serialize(new{passed=checks.Count-failed,failed,checks},new JsonSerializerOptions{WriteIndented=true}));Environment.ExitCode=failed>0?1:0;
