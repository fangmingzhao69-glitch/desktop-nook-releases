using System.Runtime.InteropServices;
using System.Windows;

namespace DesktopNook.App;

internal sealed class NativeTaskbarAnimation
{
    private readonly Dictionary<nint, HashSet<string>> _published = new();

    internal static string PropertyName(string applicationId)
    {
        ulong hash = 14695981039346656037;
        foreach (char character in applicationId)
        {
            char normalized = character is >= 'a' and <= 'z' ? (char)(character - 32) : character;
            hash = unchecked((hash ^ normalized) * 1099511628211);
        }
        return "DesktopNook.Animation." + hash.ToString("x16");
    }

    internal void Update(nint bridge, NativeTaskbarContents.Measurement contents)
    {
        var keys = new HashSet<string>();
        for (int i = 0; i < contents.AppIds.Length; i++)
        {
            Rect button = contents.AppButtons[i];
            long position = (uint)(int)Math.Round(button.Left) | ((long)(int)Math.Round(button.Top) << 32);
            long size = (uint)(int)Math.Round(button.Width) | ((long)(int)Math.Round(button.Height) << 32);
            Write(bridge, $"DesktopNook.Capsule.Button.{i}.Position", position, keys);
            Write(bridge, $"DesktopNook.Capsule.Button.{i}.Size", size, keys);
            if (string.IsNullOrEmpty(contents.AppIds[i])) continue;
            string key = PropertyName(contents.AppIds[i]);
            Write(bridge, key + ".Position", position, keys);
            Write(bridge, key + ".Size", size, keys);
        }
        Write(bridge, "DesktopNook.Capsule.ButtonCount", contents.AppButtons.Length, keys);
        if (_published.TryGetValue(bridge, out var previous))
            foreach (string key in previous.Except(keys)) RemoveProp(bridge, key);
        _published[bridge] = keys;
    }

    private static void Write(nint bridge, string name, long value, HashSet<string> keys)
    {
        if (GetProp(bridge, name) != (nint)value) SetProp(bridge, name, (nint)value);
        keys.Add(name);
    }

    internal void Remove(nint bridge)
    {
        if (_published.Remove(bridge, out var keys))
            foreach (string key in keys) RemoveProp(bridge, key);
    }

    internal void Stop()
    {
        foreach (nint bridge in _published.Keys.ToArray()) Remove(bridge);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint window, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetProp(nint window, string name, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint RemoveProp(nint window, string name);
}
