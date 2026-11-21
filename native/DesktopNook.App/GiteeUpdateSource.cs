using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DesktopNook.App;

internal sealed partial class GiteeUpdateSource
{
    private const int MaximumRedirects = 4;
    private const string ChecksumsName = "SHA256SUMS.txt";
    private readonly string _repository;

    internal GiteeUpdateSource(string repository)
    {
        if (!RepositoryName().IsMatch(repository))
            throw new ArgumentException("Gitee 仓库应使用 owner/repository 格式。", nameof(repository));
        _repository = repository;
    }

    internal static GiteeUpdateSource? Configured { get; } = ReadConfiguration();

    private static GiteeUpdateSource? ReadConfiguration()
    {
        var repository = typeof(GiteeUpdateSource).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(entry => entry.Key == "GiteeMirrorRepository")?.Value;
        return string.IsNullOrWhiteSpace(repository) ? null : new(repository);
    }

    internal Uri Latest => new($"https://gitee.com/api/v5/repos/{_repository}/releases/latest");

    internal Uri Attachment(string tag, string filename) =>
        new($"https://gitee.com/{_repository}/releases/download/{tag}/{filename}");

    internal async Task<OnlineRelease?> CheckAsync(HttpClient client, Version current, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var requestToken = timeout.Token;
        using var response = await client.GetAsync(Latest, HttpCompletionOption.ResponseHeadersRead, requestToken);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri != Latest)
            throw new IOException("Gitee 发行接口返回了意外地址。");
        await response.Content.LoadIntoBufferAsync(1024 * 1024, requestToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(requestToken));
        var release = json.RootElement;
        if (release.GetProperty("prerelease").GetBoolean() ||
            (release.TryGetProperty("draft", out var draft) && draft.GetBoolean())) return null;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!ReleaseTag().IsMatch(tag) || !Version.TryParse(tag[1..], out var version) || tag != VersionTag(version))
            throw new IOException("Gitee 发行版本号无效。");
        version = OnlineUpdater.Normalize(version);
        if (version <= OnlineUpdater.Normalize(current)) return null;

        var download = FindAttachment(release, tag, OnlineUpdater.AssetName);
        var checksums = FindAttachment(release, tag, ChecksumsName);
        using var sums = await OpenAttachmentAsync(client, checksums, tag, ChecksumsName, HttpMethod.Get, requestToken);
        await sums.Content.LoadIntoBufferAsync(64 * 1024, requestToken);
        var hash = OnlineUpdater.ParseChecksum(await sums.Content.ReadAsStringAsync(requestToken));
        using var binary = await OpenAttachmentAsync(client, download, tag, OnlineUpdater.AssetName, HttpMethod.Head, requestToken);
        var size = OnlineUpdater.ParseAssetSize(binary.Content.Headers.ContentLength);
        var notes = release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new(version, download, size, hash, notes, this);
    }

    private Uri FindAttachment(JsonElement release, string tag, string name)
    {
        var matches = release.GetProperty("assets").EnumerateArray()
            .Where(asset => asset.TryGetProperty("name", out var label) && label.GetString() == name).ToArray();
        if (matches.Length != 1 ||
            !Uri.TryCreate(matches[0].GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var url) ||
            !IsAttachment(url, tag, name))
            throw new IOException($"Gitee 发行版缺少唯一有效的 {name} 附件。");
        return url;
    }

    internal async Task<HttpResponseMessage> OpenAttachmentAsync(HttpClient client, Uri url, string tag, string name,
        HttpMethod method, CancellationToken token)
    {
        if (!IsAttachment(url, tag, name))
            throw new IOException("Gitee 下载地址不属于折页发布仓库。");

        for (int redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(method, url);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            try
            {
                if (response.RequestMessage?.RequestUri != url)
                    throw new IOException("Gitee 下载跳过了重定向校验。");
                if (response.StatusCode is not (HttpStatusCode.Moved or HttpStatusCode.Found or
                    HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                {
                    response.EnsureSuccessStatusCode();
                    return response;
                }
                var location = response.Headers.Location;
                if (location is null || redirects >= MaximumRedirects)
                    throw new IOException("Gitee 附件重定向无效。");
                var next = new Uri(url, location);
                if (!IsAttachment(next, tag, name) && !IsStorageAttachment(next, name))
                    throw new IOException("Gitee 附件重定向到了未授权的地址。");
                url = next;
            }
            catch
            {
                response.Dispose();
                throw;
            }
            response.Dispose();
        }
    }

    private bool IsAttachment(Uri url, string tag, string name) =>
        Secure(url) && url.Host == "gitee.com" && string.IsNullOrEmpty(url.Query) &&
        url.AbsolutePath == Attachment(tag, name).AbsolutePath;

    private static bool IsStorageAttachment(Uri url, string name)
    {
        if (!Secure(url) || url.Host != "foruda.gitee.com") return false;
        var parts = url.AbsolutePath.Split('/');
        return parts.Length == 4 && parts[1] == "attach_file" &&
            parts[2].Length > 0 && parts[2].All(IsAsciiIdentifier) &&
            parts[3].Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Secure(Uri url) => url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps &&
        url.IsDefaultPort && string.IsNullOrEmpty(url.UserInfo) && string.IsNullOrEmpty(url.Fragment);

    private static bool IsAsciiIdentifier(char character) => char.IsAsciiLetterOrDigit(character) || character is '-' or '_';

    internal static string VersionTag(Version version) => "v" + OnlineUpdater.Normalize(version).ToString(3);

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*\z")]
    private static partial Regex RepositoryName();

    [GeneratedRegex(@"\Av[0-9]+\.[0-9]+\.[0-9]+\z")]
    private static partial Regex ReleaseTag();
}
