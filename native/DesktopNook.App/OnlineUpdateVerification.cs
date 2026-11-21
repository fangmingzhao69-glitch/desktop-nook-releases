using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace DesktopNook.App;

internal static class OnlineUpdateVerification
{
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> reply):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var response=reply(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
    internal static async Task<int> Run(string output,string candidate,bool live=false,bool liveCheck=false)
    {
        Directory.CreateDirectory(output);var checks=new List<string>();string failure="";
        try
        {
            var version=LocalUpdater.Inspect(candidate).Version;long size=new FileInfo(candidate).Length;
            string digest=LocalUpdater.Hash(candidate),url=$"https://github.com/{OnlineUpdater.Repository}/releases/download/v{version.ToString(3)}/{OnlineUpdater.AssetName}";
            object Asset(string hash,string address)=>new{name=OnlineUpdater.AssetName,browser_download_url=address,size,digest="sha256:"+hash};
            string Feed(object asset)=>JsonSerializer.Serialize(new{tag_name="v"+version.ToString(3),draft=false,prerelease=false,body="更新验证",assets=new[]{asset}});
            string feed=Feed(Asset(digest,url));
            using var client=new HttpClient(new Handler(request=>request.RequestUri==OnlineUpdater.Latest
                ?new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(feed,Encoding.UTF8,"application/json")}
                :new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(File.OpenRead(candidate))}));
            var release=await OnlineUpdater.CheckGitHubAsync(client,new Version(0,7,1))??throw new Exception("未检测到更新。");checks.Add("最新发行版本与指定 EXE 识别");
            if(await OnlineUpdater.CheckGitHubAsync(client,version) is not null)throw new Exception("相同版本不应提示更新。");checks.Add("已是最新版时不重复更新");
            var file=await OnlineUpdater.DownloadAsync(client,release,output,null,CancellationToken.None);
            if(LocalUpdater.Hash(file)!=digest)throw new Exception("下载文件不一致。");checks.Add("流式下载、长度、SHA-256 与 EXE 产品版本联合校验");
            try{await OnlineUpdater.DownloadAsync(client,release with{Sha256=new string('0',64)},output,null,CancellationToken.None);throw new Exception("损坏文件未拒绝。");}
            catch(IOException){checks.Add("损坏下载拒绝执行并清理临时文件");}
            using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();try{await OnlineUpdater.DownloadAsync(client,release,output,null,cancelled.Token);throw new Exception("取消无效。");}catch(OperationCanceledException){checks.Add("取消下载保留现有程序");}}
            using(var bad=JsonDocument.Parse(Feed(Asset(digest,"https://example.com/update.exe"))))
            {try{OnlineUpdater.Parse(bad.RootElement,new Version(0,1));throw new Exception("外部发布源未拒绝。");}catch(IOException){checks.Add("非专用仓库下载地址拒绝");}}
            if(Directory.EnumerateFiles(output,"*.partial",SearchOption.AllDirectories).Any())throw new Exception("失败的下载遗留不完整文件。");
            checks.Add("失败及取消不会留下可执行的半成品");
            var tagUri=new Uri($"https://github.com/{OnlineUpdater.Repository}/releases/tag/v{version.ToString(3)}");
            var checksumUri=new Uri(new Uri(url),"SHA256SUMS.txt");
            int fallbackRequests=0,headRequests=0;
            HttpResponseMessage Fallback(HttpRequestMessage request,string sums,Uri? redirect=null,long? length=null)
            {
                fallbackRequests++;
                if(request.RequestUri==OnlineUpdater.LatestPage)return new(HttpStatusCode.OK){RequestMessage=new(HttpMethod.Get,redirect??tagUri)};
                if(request.RequestUri==checksumUri)return new(HttpStatusCode.OK){Content=new StringContent(sums)};
                if(request.RequestUri==new Uri(url)&&request.Method==HttpMethod.Head){headRequests++;var response=new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent([])};response.Content.Headers.ContentLength=length??size;return response;}
                throw new Exception("备用通道请求了意外地址或提前下载了程序。");
            }
            string sums=digest+"  "+OnlineUpdater.AssetName+"\n";
            foreach(var status in new[]{HttpStatusCode.Forbidden,HttpStatusCode.TooManyRequests,HttpStatusCode.BadGateway})
            {
                using var limited=new HttpClient(new Handler(request=>request.RequestUri==OnlineUpdater.Latest?new(status):Fallback(request,sums)));
                var found=await OnlineUpdater.CheckGitHubAsync(limited,new Version(0,1))??throw new Exception("备用通道未检测到更新。");
                if(found.Version!=release.Version||found.Download!=release.Download||found.Sha256!=release.Sha256||found.Size!=release.Size)throw new Exception("备用通道元数据不一致。");
                if(status==HttpStatusCode.Forbidden)await OnlineUpdater.DownloadAsync(client,found,output,null,CancellationToken.None);
            }
            checks.Add("API 403、429、502 自动转官方发行页，固定版本校验清单与 HEAD 大小，下载仍校验程序");
            using(var offline=new HttpClient(new Handler(request=>request.RequestUri==OnlineUpdater.Latest?throw new HttpRequestException("offline"):Fallback(request,sums))))
                if(await OnlineUpdater.CheckGitHubAsync(offline,new Version(0,1)) is null)throw new Exception("网络异常未使用备用通道。");
            using(var timedOut=new HttpClient(new Handler(request=>request.RequestUri==OnlineUpdater.Latest?throw new TaskCanceledException():Fallback(request,sums))))
                if(await OnlineUpdater.CheckGitHubAsync(timedOut,new Version(0,1)) is null)throw new Exception("超时未使用备用通道。");
            checks.Add("API 网络异常及超时可回退，备用检查不会提前下载 EXE");
            int before=headRequests;
            using(var limited=new HttpClient(new Handler(request=>request.RequestUri==OnlineUpdater.Latest?new(HttpStatusCode.Forbidden):Fallback(request,sums))))
            {
                if(await OnlineUpdater.CheckGitHubAsync(limited,version) is not null||await OnlineUpdater.CheckGitHubAsync(limited,new Version(version.Major+1,0)) is not null||headRequests!=before)throw new Exception("备用通道相同或更旧版本行为异常。");
                int requests=fallbackRequests;using var cancel=new CancellationTokenSource();cancel.Cancel();
                try{await OnlineUpdater.CheckGitHubAsync(limited,new Version(0,1),cancel.Token);throw new Exception("取消无效。");}catch(OperationCanceledException){}
                if(fallbackRequests!=requests)throw new Exception("取消后仍访问备用通道。");
            }
            checks.Add("备用通道相同或旧版返回已是最新版；用户取消不会启动备用请求");
            foreach(var invalid in new[]{"missing","duplicate","hash","foreign","size","prerelease"})
            {
                using var badFallback=new HttpClient(new Handler(request=>request.RequestUri==OnlineUpdater.Latest?new(HttpStatusCode.Forbidden):Fallback(request,
                    invalid=="missing"?"":invalid=="duplicate"?sums+sums:invalid=="hash"?new string('z',64)+"  "+OnlineUpdater.AssetName:sums,
                    invalid=="foreign"?new Uri("https://example.com/releases/tag/v1.0"):invalid=="prerelease"?new Uri(tagUri+"-beta"):null,invalid=="size"?0:null)));
                try{await OnlineUpdater.CheckGitHubAsync(badFallback,new Version(0,1));throw new Exception("无效备用元数据未拒绝："+invalid);}catch(IOException){}
            }
            checks.Add("备用通道拒绝缺失、重复、非法哈希、外部发行地址、无效大小及预发行标签");
            using(var invalidApi=new HttpClient(new Handler(request=>request.RequestUri==OnlineUpdater.Latest
                ?new(HttpStatusCode.OK){Content=new StringContent(Feed(Asset(digest,"https://example.com/update.exe")))}:throw new Exception("无效 API 元数据不应绕过校验"))))
            {
                try{await OnlineUpdater.CheckGitHubAsync(invalidApi,new Version(0,1));throw new Exception("无效 API 数据未拒绝。");}catch(IOException){}
            }
            checks.Add("API 返回无效发布信息时保持拒绝，不通过备用通道掩盖校验错误");
            await CheckGiteeMirror(output,candidate,version,size,digest,sums,client,checks);
            if(liveCheck)
            {
                using var remote=OnlineUpdater.CreateClient();
                var published=await OnlineUpdater.CheckReleasePageAsync(remote,new Version(0,1))??throw new Exception("官方发行页未发现版本。");
                if(await OnlineUpdater.CheckAsync(remote,published.Version) is not null)throw new Exception("线上相同版本仍提示更新。");
                checks.Add($"真实官方发行页元数据可用 v{published.Version.ToString(3)}；公共检查相同版本正确返回已是最新版");
            }
            if(live)
            {
                using var remote=OnlineUpdater.CreateClient();var published=await OnlineUpdater.CheckAsync(remote,new Version(0,7,1))??throw new Exception("线上未发现发行版。");
                var actual=await OnlineUpdater.DownloadAsync(remote,published,output,null,CancellationToken.None);
                if(LocalUpdater.Hash(actual)!=digest)throw new Exception("线上资产与本地产物不同。");checks.Add("真实 GitHub 公共接口与发行包下载校验通过");
            }
        }
        catch(Exception ex){failure=ex.ToString();}
        File.WriteAllText(Path.Combine(output,"online-update-verification.json"),JsonSerializer.Serialize(new{success=failure=="",passed=checks.Count,checks,failure},new JsonSerializerOptions{WriteIndented=true}));return failure==""?0:1;
    }

    private static async Task CheckGiteeMirror(string output,string candidate,Version version,long size,
        string digest,string sums,HttpClient github,List<string> checks)
    {
        var mirror=new GiteeUpdateSource("fold-mirror/fixture");
        var tag=GiteeUpdateSource.VersionTag(version);
        var download=mirror.Attachment(tag,OnlineUpdater.AssetName);
        var checksum=mirror.Attachment(tag,"SHA256SUMS.txt");
        var cdn=new Uri("https://foruda.gitee.com/attach_file/fixture/desktopnook-win-x64.exe");
        var checksumCdn=new Uri("https://foruda.gitee.com/attach_file/fixture/sha256sums.txt");
        var older=new Version(0,7,1);
        string Metadata(Uri binary) => JsonSerializer.Serialize(new
        {
            tag_name=tag, prerelease=false, body="Gitee fixture",
            assets=new[]
            {
                new {name=OnlineUpdater.AssetName,browser_download_url=binary.AbsoluteUri},
                new {name="SHA256SUMS.txt",browser_download_url=checksum.AbsoluteUri}
            }
        });
        using var unexpectedGitHub = new HttpClient(new Handler(_ => throw new Exception("优先通道仍可用时访问了 GitHub")));
        using var good = new HttpClient(new Handler(request => Reply(request)));
        var release=await OnlineUpdater.CheckWithMirrorAsync(unexpectedGitHub,good,mirror,older,CancellationToken.None);
        if (release is null || release.Mirror!=mirror || release.Size!=size || release.Sha256!=digest)
            throw new Exception("Gitee 无附件大小与哈希字段的发行版未正确读取");
        var candidatePath=await OnlineUpdater.DownloadWithMirrorAsync(unexpectedGitHub,good,release,output,null,CancellationToken.None);
        if (LocalUpdater.Hash(candidatePath)!=digest) throw new Exception("Gitee CDN 下载内容不一致");
        checks.Add("Gitee 优先查询发行 API，经同版本校验清单、HEAD 和官方 CDN 下载并核对 SHA-256");
        try
        {
            await OnlineUpdater.DownloadWithMirrorAsync(unexpectedGitHub,good,release with {Sha256=new string('0',64)},output,null,CancellationToken.None);
            throw new Exception("错误的 Gitee 哈希未被拒绝");
        }
        catch (IOException) { }
        if (Directory.EnumerateFiles(output,"*.partial",SearchOption.AllDirectories).Any())
            throw new Exception("Gitee 下载失败遗留了临时文件");
        checks.Add("Gitee 附件 SHA-256 不匹配时拒绝更新并清理半成品");

        using var noMirror = new HttpClient(new Handler(_ => new(HttpStatusCode.TooManyRequests)));
        var recovered=await OnlineUpdater.CheckWithMirrorAsync(github,noMirror,mirror,older,CancellationToken.None);
        if (recovered is null || recovered.Mirror is not null || recovered.Version!=version)
            throw new Exception("Gitee 限流没有切换到 GitHub");
        var backupPath=await OnlineUpdater.DownloadWithMirrorAsync(github,noMirror,release,output,null,CancellationToken.None);
        if (LocalUpdater.Hash(backupPath)!=digest) throw new Exception("GitHub 回退文件与 Gitee 发行记录不同");
        checks.Add("Gitee 限流后检查和下载回退 GitHub，沿用已确认版本与 SHA-256");

        using var badAttachment = new HttpClient(new Handler(request =>
            Reply(request,downloadUrl:new Uri("https://gitee.com/unrelated/fixture/releases/download/" + tag + "/" + OnlineUpdater.AssetName))));
        using var badRedirect = new HttpClient(new Handler(request => Reply(request,redirect:new Uri("https://example.org/update.exe"))));
        foreach (var invalid in new[] {badAttachment,badRedirect})
        {
            var fromGitHub=await OnlineUpdater.CheckWithMirrorAsync(github,invalid,mirror,older,CancellationToken.None);
            if (fromGitHub is null || fromGitHub.Mirror is not null) throw new Exception("Gitee 的外部仓库或 CDN 未拒绝");
        }
        checks.Add("拒绝 Gitee 外部仓库和未授权重定向，仍可查询折页 GitHub 发行版");

        using var unreachable = new HttpClient(new Handler(_ => throw new HttpRequestException("offline")));
        if (await OnlineUpdater.CheckWithMirrorAsync(unreachable,good,mirror,version,CancellationToken.None) is not null)
            throw new Exception("镜像同版本不应重复提示");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await OnlineUpdater.CheckWithMirrorAsync(unexpectedGitHub,good,mirror,older,cancelled.Token);
            throw new Exception("取消后 Gitee 仍返回更新");
        }
        catch (OperationCanceledException) { }
        checks.Add("Gitee 可独立判断已是最新版，取消不会启动 GitHub 回退");

        HttpResponseMessage Reply(HttpRequestMessage request,Uri? downloadUrl=null,Uri? redirect=null)
        {
            if (request.RequestUri==mirror.Latest)
                return new(HttpStatusCode.OK) { Content=new StringContent(Metadata(downloadUrl??download)) };
            if (request.RequestUri==checksum) return Redirect(checksumCdn);
            if (request.RequestUri==checksumCdn) return new(HttpStatusCode.OK) { Content=new StringContent(sums) };
            if (request.RequestUri==download) return Redirect(redirect??cdn);
            if (request.RequestUri==cdn)
            {
                var binary = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content=request.Method==HttpMethod.Head ? new ByteArrayContent([]) : new StreamContent(File.OpenRead(candidate))
                };
                binary.Content.Headers.ContentLength=size;
                return binary;
            }
            throw new Exception("请求了意外的 Gitee 附件地址");
        }

        static HttpResponseMessage Redirect(Uri location)
        {
            var response=new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location=location;
            return response;
        }
    }
}
