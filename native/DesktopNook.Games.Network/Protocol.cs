using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using DesktopNook.Games.Core;

namespace DesktopNook.Games.Network;

public sealed record GameAddress(string Ip,int Port);
public sealed record GameInvite(int Version,string Rules,string Room,string Rule,long Expires,string Fingerprint,string Token,GameAddress[] Addresses)
{
    public const string Prefix="qingxia-billiards://join/";
    public const string RulesVersion="qx-local-rules-2026-11-v1";
    public string Encode()=>Prefix+Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this)).TrimEnd('=').Replace('+','-').Replace('/','_');
    public static GameInvite Parse(string text,bool allowExpired=false)
    {
        if(text.Length>8192||!text.StartsWith(Prefix,StringComparison.Ordinal))throw new InvalidDataException("邀请格式不正确，请完整复制邀请信息。");
        try
        {
            string encoded=text[Prefix.Length..].Trim().Replace('-','+').Replace('_','/');encoded=encoded.PadRight((encoded.Length+3)/4*4,'=');
            var invite=JsonSerializer.Deserialize<GameInvite>(Convert.FromBase64String(encoded))??throw new InvalidDataException();invite.Validate(allowExpired);return invite;
        }
        catch(Exception ex)when(ex is JsonException or FormatException or NullReferenceException){throw new InvalidDataException("邀请信息损坏。",ex);}
    }
    public void Validate(bool allowExpired=false)
    {
        if(Version!=1||Rules!=RulesVersion)throw new InvalidDataException("联机协议或规则版本不兼容，请双方更新软件。");
        if(!BilliardsRules.IsKnown(Rule)||Room.Length!=24||!Hex(Room)||Fingerprint.Length!=64||!Hex(Fingerprint)||Token.Length is not (0 or 64)||!Hex(Token)||Addresses.Length is <1 or >12)throw new InvalidDataException("邀请参数无效。");
        long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();if(!allowExpired&&(Expires<now||Expires>now+86400))throw new InvalidDataException("邀请已过期，请房主重新创建房间。");
        foreach(var address in Addresses)if(!IPAddress.TryParse(address.Ip,out var ip)||ip.Equals(IPAddress.Any)||ip.Equals(IPAddress.IPv6Any)||ip.IsIPv6Multicast||address.Port is <1 or >65535)throw new InvalidDataException("邀请连接地址无效。");
    }
    private static bool Hex(string s)=>s.All(Uri.IsHexDigit);
    public static string Secret(int bytes=32)=>Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes));
    internal static bool SameSecret(string a,string b)=>a.Length==b.Length&&CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a),System.Text.Encoding.UTF8.GetBytes(b));
}
public sealed record SoundView(long Id,BallSoundKind Kind,double Strength);
public sealed record RoomView(long Sequence,string Room,bool Connected,bool Started,bool Ended,string Status,
    bool HostReady,bool GuestReady,RoundView Round,SoundView[] Sounds);
public sealed class NetPacket
{
    public string Kind {get;set;}="";
    public int Version {get;set;}=1;
    public string Rules {get;set;}=GameInvite.RulesVersion;
    public string Room {get;set;}="";
    public string Token {get;set;}="";
    public string Resume {get;set;}="";
    public string Name {get;set;}="好友";
    public int Seat {get;set;}=1;
    public long Request {get;set;}
    public int Round {get;set;}
    public int BaseShot {get;set;}
    public double X {get;set;}
    public double Y {get;set;}
    public double Power {get;set;}
    public bool Accept {get;set;}
    public string Message {get;set;}="";
    public RoomView? View {get;set;}
}
public static class NetWire
{
    public const int MaxBytes=256*1024;
    private static readonly JsonSerializerOptions Json=new(){MaxDepth=24};
    public static async Task Write(Stream stream,NetPacket packet,CancellationToken token)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(packet,Json);if(bytes.Length>MaxBytes)throw new InvalidDataException("联机消息超出上限。");
        var header=new byte[4];BinaryPrimitives.WriteInt32BigEndian(header,bytes.Length);await stream.WriteAsync(header,token).ConfigureAwait(false);await stream.WriteAsync(bytes,token).ConfigureAwait(false);await stream.FlushAsync(token).ConfigureAwait(false);
    }
    public static async Task<NetPacket> Read(Stream stream,CancellationToken token)
    {
        var header=new byte[4];await stream.ReadExactlyAsync(header,token).ConfigureAwait(false);int count=BinaryPrimitives.ReadInt32BigEndian(header);
        if(count is <2 or >MaxBytes)throw new InvalidDataException("联机消息长度无效。");var bytes=new byte[count];await stream.ReadExactlyAsync(bytes,token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<NetPacket>(bytes,Json)??throw new InvalidDataException("联机消息为空。");
    }
}
