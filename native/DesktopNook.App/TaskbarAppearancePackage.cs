using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace DesktopNook.App;

internal sealed class TaskbarAppearancePackage(string storePath, bool useCompatibilityPackage = false)
{
    internal const string Version = "2026.2";
    private const int Size = 1_769_775;
    private const string Sha256 = "0DBE8E0255C20E131CDE536DCD0AE490D45989A7D360E26D0150DFF1922AC420";
    private const int CompatibilitySize = 1799291;
    private const string CompatibilitySha256 = "1C91B81D1296842F4AF22D79A9E6EBE9FA084DCE2374C5CC1DB7C180207CC624";
    private static readonly Uri Source = new("https://github.com/TranslucentTB/TranslucentTB/releases/download/2026.2/TranslucentTB-portable-x64.zip");
    private const string CompatibilityFile = "TranslucentTB-2026.2-26340.zip";
    private static readonly Uri CompatibilitySource = new($"https://github.com/{OnlineUpdater.Repository}/releases/download/v0.17.0/{CompatibilityFile}");
    // Preserve the existing cache across application upgrades.
    private string PackageName => "TranslucentTB-" + Version + (useCompatibilityPackage ? "-26340-preview" : "");
    internal string ArchivePath => Path.Combine(storePath, "components", PackageName + ".zip");
    private string ComponentDirectory => Path.Combine(storePath, "components", PackageName);
    internal bool HasArchive => File.Exists(ArchivePath);
    internal bool UsesCompatibilityPackage => useCompatibilityPackage;

    internal async Task<string> PrepareAsync(bool allowDownload, CancellationToken token = default)
    {
        var archive = ArchivePath;
        PrepareCache();
        if (!File.Exists(archive))
        {
            if (!allowDownload) throw new IOException("首次启用需要下载任务栏外观组件。");
            using var client = OnlineUpdater.CreateClient();
            await DownloadAsync(client, token).ConfigureAwait(false);
        }
        return await Task.Run(() => VerifyAndExtract(archive, ComponentDirectory, UsesCompatibilityPackage), token).ConfigureAwait(false);
    }

    private void PrepareCache()
    {
        var directory = Path.GetDirectoryName(ArchivePath)!;
        Directory.CreateDirectory(directory);
        CheckRegularPath(storePath);
        CheckRegularPath(directory);
    }

    internal async Task DownloadAsync(HttpClient client, CancellationToken token = default)
    {
        PrepareCache();
        string finalPath = ArchivePath;
        int expectedSize = UsesCompatibilityPackage ? CompatibilitySize : Size;
        var sourceUri = UsesCompatibilityPackage ? CompatibilitySource : Source;
        var temp = finalPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        token = timeout.Token;
        try
        {
            using var response = await client.GetAsync(sourceUri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
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
            using (var file = OpenVerifiedArchive(temp, UsesCompatibilityPackage)) { }
            File.Move(temp, finalPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    internal static string VerifyAndExtract(string archivePath, string destination, bool compatibility = false)
    {
        using var file = OpenVerifiedArchive(archivePath, compatibility);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
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

    private static FileStream OpenVerifiedArchive(string path, bool compatibility = false)
    {
        CheckRegularPath(path);
        var file = File.OpenRead(path);
        try
        {
            int expectedSize = compatibility ? CompatibilitySize : Size;
            string expectedHash = compatibility ? CompatibilitySha256 : Sha256;
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
        var config = Path.Combine(root, "settings.json");
        if (File.Exists(config))
        {
            CheckRegularPath(config);
            allowed.Add(config);
        }
        string assets = Path.Combine(root, "Assets");
        if (Directory.GetDirectories(root).Any(dir => !dir.Equals(assets, StringComparison.OrdinalIgnoreCase)) ||
            Directory.GetDirectories(assets).Length != 0 ||
            Directory.EnumerateFiles(root).Concat(Directory.EnumerateFiles(assets)).Any(path => !allowed.Contains(path)))
            throw new IOException("任务栏外观组件目录包含未验证的文件。");
    }

    private static void CheckRegularPath(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("任务栏外观组件路径不能使用链接或联接。");
    }
}
