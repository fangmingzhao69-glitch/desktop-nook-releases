using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopNook.App;

internal static class ThemeShellIcon
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
    private static readonly Dictionary<FoldTheme, byte[]> Encoded = new();

    internal static string FileFor(FoldTheme theme, string? folder = null)
    {
        if (!Encoded.TryGetValue(theme, out var icon))
            Encoded[theme] = icon = Encode(FoldTheme.Mark(theme));
        return SavePublicIcon(icon, folder);
    }

    internal static string SavePublicIcon(byte[] icon, string? folder = null)
    {
        var artwork = Convert.ToHexString(SHA256.HashData(icon))[..16];
        try { return Save(folder ?? Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "Branding")); }
        catch (Exception ex) when (folder is null && ex is IOException or UnauthorizedAccessException)
        {
            var shared = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
            if (string.IsNullOrEmpty(shared)) throw;
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            return Save(Path.Combine(shared, "QingxiaIconCache", sid));
        }

        string Save(string destination)
        {
            Directory.CreateDirectory(destination);
            var file = Path.Combine(destination, "Zheye-" + artwork + ".ico");
            if (!File.Exists(file) || !File.ReadAllBytes(file).AsSpan().SequenceEqual(icon))
            {
                var staged = Path.Combine(destination, Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.WriteAllBytes(staged, icon);
                    if ((File.GetAttributes(staged) & FileAttributes.Encrypted) != 0)
                        throw new IOException("图标缓存位置被加密。");
                    File.Move(staged, file, overwrite: true);
                }
                finally { File.Delete(staged); }
            }
            if ((File.GetAttributes(file) & FileAttributes.Encrypted) != 0)
                throw new IOException("图标缓存位置被加密。");
            return file;
        }
    }

    private static byte[] Encode(DrawingImage image)
    {
        var frames = new List<byte[]>(Sizes.Length);
        foreach (var size in Sizes)
        {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.PushTransform(new ScaleTransform(size / 1024d, size / 1024d));
                drawing.DrawDrawing(image.Drawing);
                drawing.Pop();
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var frame = new MemoryStream();
            encoder.Save(frame);
            frames.Add(frame.ToArray());
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)Sizes.Length);
        var offset = 6 + Sizes.Length * 16;
        for (int i = 0; i < Sizes.Length; i++)
        {
            var size = (byte)(Sizes[i] == 256 ? 0 : Sizes[i]);
            writer.Write(size);
            writer.Write(size);
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(frames[i].Length);
            writer.Write(offset);
            offset += frames[i].Length;
        }
        foreach (var frame in frames) writer.Write(frame);
        return stream.ToArray();
    }
}
