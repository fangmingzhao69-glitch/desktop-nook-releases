using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace DesktopNook.App;

internal sealed class TaskbarAppearancePackage(string storePath)
{
    internal const string ReleaseTag = "v0.21.16";
    internal const string PackageFile = "Qingxia-TaskbarCore-win-x64.zip";
    private const int Size = 830518;
    private const string ResourceName = "DesktopNook.NativeTaskbarPackage";
    private const string Sha256 = "451C4D85A2330239241C319AD1F68B5390030DE3839ADFD3FCAB6A43B0D3386B";
    private static readonly Uri Source = new($"https://github.com/{OnlineUpdater.Repository}/releases/download/{ReleaseTag}/{PackageFile}");
    internal static readonly IReadOnlyList<string> Files = Array.AsReadOnly(new[]
    {
        "QingxiaTaskbarCore.dll", "ProgramLog.dll", "ExplorerTAP.dll", "ExplorerHooks.dll",
        "LICENSE.txt", "THIRD-PARTY-NOTICES.txt"
    });
    private static string PackageName => Path.GetFileNameWithoutExtension(PackageFile) + "-" + ReleaseTag;
    internal string ArchivePath => Path.Combine(storePath, "components", PackageName + ".zip");
    private string ComponentDirectory => Path.Combine(storePath, "components", PackageName);
    internal bool HasArchive => File.Exists(ArchivePath) ||
        typeof(TaskbarAppearancePackage).Assembly.GetManifestResourceInfo(ResourceName) is not null;

    internal async Task<string> PrepareAsync(bool allowDownload, CancellationToken token = default)
    {
        var archive = ArchivePath;
        PrepareCache();
        if (!File.Exists(archive) && !await TryExtractEmbeddedArchiveAsync(archive, token).ConfigureAwait(false))
        {
            if (!allowDownload) throw new IOException("首次启用需要下载任务栏外观组件。");
            using var client = OnlineUpdater.CreateClient();
            if (GiteeUpdateSource.Configured is { } mirror)
            {
                using var mirrorClient = OnlineUpdater.CreateClient(allowRedirects: false);
                try { await DownloadFromAsync(mirrorClient, mirror, token).ConfigureAwait(false); }
                catch (Exception ex) when (!token.IsCancellationRequested && OnlineUpdater.IsNetworkFailure(ex))
                {
                    await DownloadAsync(client, token).ConfigureAwait(false);
                }
            }
            else await DownloadAsync(client, token).ConfigureAwait(false);
        }
        return await Task.Run(() => VerifyAndExtract(archive, ComponentDirectory), token).ConfigureAwait(false);
    }

    private static async Task<bool> TryExtractEmbeddedArchiveAsync(string path, CancellationToken token)
    {
        using var embedded = typeof(TaskbarAppearancePackage).Assembly.GetManifestResourceStream(ResourceName);
        if (embedded is null) return false;
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await embedded.CopyToAsync(output, token).ConfigureAwait(false);
            using (OpenVerifiedArchive(temp)) { }
            File.Move(temp, path, overwrite: false);
            return true;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private void PrepareCache()
    {
        var directory = Path.GetDirectoryName(ArchivePath)!;
        Directory.CreateDirectory(directory);
        CheckRegularPath(storePath);
        CheckRegularPath(directory);
    }

    internal Task DownloadAsync(HttpClient client, CancellationToken token = default) =>
        DownloadFromAsync(client, null, token);

    internal async Task DownloadFromAsync(HttpClient client, GiteeUpdateSource? mirror, CancellationToken token)
    {
        PrepareCache();
        string finalPath = ArchivePath;
        const int expectedSize = Size;
        var temp = finalPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        token = timeout.Token;
        try
        {
            using var response = mirror is null
                ? await client.GetAsync(Source, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false)
                : await OpenMirrorComponentAsync(client, mirror, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != expectedSize)
                throw new IOException("组件下载的文件大小与发布记录不一致。");
            using (var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            {
                var buffer = new byte[65536];
                long lengthRead = 0;
                for (int read; (read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0;)
                {
                    lengthRead += read;
                    if (lengthRead > expectedSize) throw new IOException("组件下载超出预期大小。");
                    await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
                destination.Flush(flushToDisk: true);
            }
            using (var file = OpenVerifiedArchive(temp)) { }
            File.Move(temp, finalPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static Task<HttpResponseMessage> OpenMirrorComponentAsync(HttpClient client, GiteeUpdateSource mirror, CancellationToken token)
    {
        var tag = GiteeUpdateSource.VersionTag(typeof(TaskbarAppearancePackage).Assembly.GetName().Version!);
        return mirror.OpenAttachmentAsync(client, mirror.Attachment(tag, PackageFile), tag, PackageFile, HttpMethod.Get, token);
    }

    internal static string VerifyAndExtract(string archivePath, string destination)
    {
        using var file = OpenVerifiedArchive(archivePath);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        if (archive.Entries.Count != Files.Count || Files.Any(name => archive.GetEntry(name) is null))
            throw new IOException("任务栏外观组件内容清单不匹配。");
        if (Directory.Exists(destination))
        {
            CheckExtracted(archive, destination);
            return destination;
        }

        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            archive.ExtractToDirectory(temp);
            CheckExtracted(archive, temp);
            Directory.Move(temp, destination);
            return destination;
        }
        finally
        {
            // Only this invocation's newly generated sibling directory is eligible for cleanup.
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    private static FileStream OpenVerifiedArchive(string path)
    {
        CheckRegularPath(path);
        var file = File.OpenRead(path);
        try
        {
            const int expectedSize = Size;
            const string expectedHash = Sha256;
            if (file.Length != expectedSize || Convert.ToHexString(SHA256.HashData(file)) != expectedHash)
                throw new IOException("组件文件的 SHA-256 或大小未通过校验。");
            file.Position = 0;
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static void CheckExtracted(ZipArchive archive, string root)
    {
        CheckRegularPath(root);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var path = Path.GetFullPath(Path.Combine(root, entry.FullName));
            allowed.Add(path);
            using var content = entry.Open();
            CheckRegularPath(Path.GetDirectoryName(path)!);
            CheckRegularPath(path);
            using var file = File.OpenRead(path);
            if (file.Length != entry.Length || !SHA256.HashData(file).AsSpan().SequenceEqual(SHA256.HashData(content)))
                throw new IOException("任务栏外观组件目录中的文件与已校验压缩包不一致。");
        }
        if (Directory.EnumerateFileSystemEntries(root).Any(path => !allowed.Contains(path)))
            throw new IOException("任务栏外观组件目录包含未验证的文件。");
    }

    private static void CheckRegularPath(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("任务栏外观组件路径不能使用链接或联接。");
    }
}
