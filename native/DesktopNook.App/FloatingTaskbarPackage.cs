using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopNook.App;

internal static class FloatingTaskbarPackage
{
    internal static string Ensure(string dataRoot)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("DesktopNook.FloatingTaskbar")
            ?? throw new IOException("此构建缺少悬浮任务栏组件，请重新运行 Build-Dock.ps1 后构建。");
        var digest = Convert.ToHexString(SHA256.HashData(resource));
        resource.Position = 0;
        var destination = Path.Combine(dataRoot, "FloatingTaskbar", "Components", digest[..20]);
        var executable = Path.Combine(destination, "ZheyeDock.exe");
        using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
        // The resource is part of this assembly. Validate every extracted file on reuse, too.
        var entries = zip.Entries.Where(entry => entry.Name.Length > 0).ToArray();
        bool Valid()
        {
            foreach (var entry in entries)
            {
                var path = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                if (!path.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("组件路径无效。");
                if (!File.Exists(path) || new FileInfo(path).Length != entry.Length) return false;
                using var expected = entry.Open(); using var actual = File.OpenRead(path);
                if (!SHA256.HashData(expected).AsSpan().SequenceEqual(SHA256.HashData(actual))) return false;
            }
            return File.Exists(executable);
        }
        if (Valid()) return executable;
        // Build the complete directory beside its final location, then publish it atomically.
        var staging = destination + "." + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        foreach (var entry in entries)
        {
            var path = Path.GetFullPath(Path.Combine(staging, entry.FullName));
            if (!path.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("组件路径无效。");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path);
        }
        if (Directory.Exists(destination))
            throw new IOException("悬浮组件校验未通过。请退出折页，将数据目录中的 FloatingTaskbar/Components 对应目录移走后重试。");
        Directory.Move(staging, destination);
        if (!Valid()) throw new IOException("悬浮组件校验未通过。");
        return executable;
    }
}
