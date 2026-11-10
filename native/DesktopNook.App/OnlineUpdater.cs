using System.IO;
using System.Net.Http;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopNook.App;

internal sealed record OnlineRelease(Version Version,Uri Download,long Size,string Sha256,string Notes);

internal static class OnlineUpdater
{
    internal const string Repository="fangmingzhao69-glitch/desktop-nook-releases";
    internal const string AssetName="DesktopNook-win-x64.exe";
    internal static readonly Uri Latest=new($"https://api.github.com/repos/{Repository}/releases/latest");
    internal static readonly Uri LatestPage=new($"https://github.com/{Repository}/releases/latest");
    internal static HttpClient CreateClient()
    {
        // HttpClient uses the Windows proxy settings; no account token is shipped.
        var client=new HttpClient{Timeout=TimeSpan.FromMinutes(15)};
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopNook-Updater/0.8");
        return client;
    }
    internal static async Task<OnlineRelease?> CheckAsync(HttpClient client,Version current,CancellationToken token=default)
    {
        try { return await CheckApiAsync(client,current,token); }
        catch(HttpRequestException ex) when(ex.StatusCode is null or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests || (int?)ex.StatusCode>=500) { }
        catch(OperationCanceledException) when(!token.IsCancellationRequested) { }
        return await CheckReleasePageAsync(client,current,token);
    }
    private static async Task<OnlineRelease?> CheckApiAsync(HttpClient client,Version current,CancellationToken token)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var response=await client.GetAsync(Latest,timeout.Token);response.EnsureSuccessStatusCode();
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));var root=json.RootElement;
        return Parse(root,current);
    }
    internal static async Task<OnlineRelease?> CheckReleasePageAsync(HttpClient client,Version current,CancellationToken token=default)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(40));
        // GitHub's public release page and assets remain available when its REST API is rate limited.
        using var page=await client.GetAsync(LatestPage,HttpCompletionOption.ResponseHeadersRead,timeout.Token);page.EnsureSuccessStatusCode();
        var location=page.RequestMessage?.RequestUri;
        string prefix=$"/{Repository}/releases/tag/";
        if(location is null || location.Scheme!=Uri.UriSchemeHttps || location.Host!="github.com" || !location.IsDefaultPort || !string.IsNullOrEmpty(location.UserInfo)
            || !string.IsNullOrEmpty(location.Query) || !string.IsNullOrEmpty(location.Fragment) || !location.AbsolutePath.StartsWith(prefix,StringComparison.Ordinal))
            throw new IOException("无法核验官方发行页的版本地址。");
        var tag=location.AbsolutePath[prefix.Length..];
        if(!Version.TryParse(tag.TrimStart('v','V'),out var version))throw new IOException("官方发行页的版本号无效。");
        version=Normalize(version);
        if(version<=Normalize(current))return null;
        // Pin every subsequent request to this exact tag, so a concurrent release cannot mix metadata.
        var download=new Uri($"https://github.com/{Repository}/releases/download/{tag}/{AssetName}");
        using var checksum=await client.GetAsync(new Uri(download,"SHA256SUMS.txt"),HttpCompletionOption.ResponseHeadersRead,timeout.Token);checksum.EnsureSuccessStatusCode();
        await checksum.Content.LoadIntoBufferAsync(64*1024,timeout.Token);
        var matches=(await checksum.Content.ReadAsStringAsync(timeout.Token)).Split('\n')
            .Select(line=>line.Trim().Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries))
            .Where(parts=>parts.Length==2 && parts[1].TrimStart('*')==AssetName).ToList();
        if(matches.Count!=1 || matches[0][0].Length!=64 || !matches[0][0].All(Uri.IsHexDigit))throw new IOException("官方发行包缺少唯一有效的 SHA-256 校验值。");
        using var request=new HttpRequestMessage(HttpMethod.Head,download);
        using var asset=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);asset.EnsureSuccessStatusCode();
        if(asset.Content.Headers.ContentLength is not long size || size<=0 || size>512L*1024*1024)throw new IOException("无法核验官方更新文件的大小。");
        return new(version,download,size,matches[0][0],$"新版已发布，更新说明：{location}");
    }
    internal static string FailureMessage(Exception exception)=>exception switch
    {
        HttpRequestException {StatusCode:HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests}=>"GitHub 请求受到限制，官方备用通道也暂时不可用，请稍后重试。",
        HttpRequestException=>"无法连接官方更新服务，请检查网络或系统代理后重试。",
        OperationCanceledException=>"连接官方更新服务超时，请稍后重试。",
        IOException=>exception.Message,
        _=>"无法读取官方更新信息，请稍后重试。"
    };
    internal static OnlineRelease? Parse(JsonElement root,Version current)
    {
        if(root.GetProperty("draft").GetBoolean()||root.GetProperty("prerelease").GetBoolean())return null;
        if(!Version.TryParse(root.GetProperty("tag_name").GetString()?.TrimStart('v','V'),out var version))throw new IOException("发布版本号无效。");
        version=Normalize(version);
        if(version<=Normalize(current))return null;
        foreach(var asset in root.GetProperty("assets").EnumerateArray())
        {
            if(asset.GetProperty("name").GetString()!=AssetName)continue;
            var url=new Uri(asset.GetProperty("browser_download_url").GetString()!);
            string digest=asset.TryGetProperty("digest",out var hash)?hash.GetString()??"":"";
            long size=asset.GetProperty("size").GetInt64();
            if(!TrustedUrl(url)||!digest.StartsWith("sha256:",StringComparison.OrdinalIgnoreCase)||digest.Length!=71||!digest[7..].All(Uri.IsHexDigit)||size<=0||size>512L*1024*1024)
                throw new IOException("发布文件缺少有效的下载地址、大小或 SHA-256 校验信息。");
            return new(version,url,size,digest[7..],root.TryGetProperty("body",out var body)?body.GetString()??"":"");
        }
        throw new IOException("新版本尚未提供 Windows x64 更新文件，请稍后再试。");
    }
    private static bool TrustedUrl(Uri uri)=>uri.Scheme==Uri.UriSchemeHttps&&uri.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase)
        &&uri.IsDefaultPort&&string.IsNullOrEmpty(uri.UserInfo)&&uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/",StringComparison.Ordinal);
    private static Version Normalize(Version v)=>new(v.Major,v.Minor,Math.Max(0,v.Build),Math.Max(0,v.Revision));
    internal static async Task<string> DownloadAsync(HttpClient client,OnlineRelease release,string dataRoot,IProgress<double>? progress,CancellationToken token)
    {
        if(!TrustedUrl(release.Download))throw new IOException("更新下载地址不属于轻匣发布仓库。");
        string directory=Path.Combine(dataRoot,"updates","download-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string partial=Path.Combine(directory,"candidate.partial"),file=Path.Combine(directory,"candidate.exe");
        try
        {
            using var response=await client.GetAsync(release.Download,HttpCompletionOption.ResponseHeadersRead,token);response.EnsureSuccessStatusCode();
            if(response.Content.Headers.ContentLength is long length&&length!=release.Size)throw new IOException("更新文件大小与发布记录不一致。");
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using(var input=await response.Content.ReadAsStreamAsync(token))
            await using(var output=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true))
            {
                byte[] buffer=new byte[65536];long total=0;int count;
                while((count=await input.ReadAsync(buffer,token))>0)
                {
                    total+=count;if(total>release.Size)throw new IOException("更新文件超过预期大小。");
                    hash.AppendData(buffer,0,count);await output.WriteAsync(buffer.AsMemory(0,count),token);progress?.Report(total*100d/release.Size);
                }
                if(total!=release.Size)throw new IOException("更新下载不完整，请重试。");
            }
            if(!Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("更新文件 SHA-256 校验失败，请重试。");
            token.ThrowIfCancellationRequested();File.Move(partial,file);
            if(LocalUpdater.Inspect(file).Version!=Normalize(release.Version))throw new IOException("更新程序版本与发布记录不一致。");
            return file;
        }
        catch{if(File.Exists(partial))File.Delete(partial);if(File.Exists(file))File.Delete(file);throw;}
    }
}
