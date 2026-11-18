using System.Runtime.InteropServices;

namespace DesktopNook.App;

internal static class TaskbarAppearanceDependencies
{
    private const int InsufficientBuffer = 122;

    internal static string? GetRestriction()
    {
        if (!HasPackage("Microsoft.VCLibs.140.00_8wekyb3d8bbwe", new Version(14, 0, 33519, 0)))
            return "任务栏外观需要当前用户具备 Microsoft VCLibs 14.0 x64 运行库。";
        if (!HasPackage("Microsoft.UI.Xaml.2.8_8wekyb3d8bbwe", new Version(8, 2501, 31001, 0)))
            return "任务栏外观需要当前用户具备 Microsoft UI Xaml 2.8 x64 运行库。";
        return null;
    }

    private static bool HasPackage(string family, Version minimum)
    {
        uint count = 0, length = 0;
        int result = GetPackagesByPackageFamily(family, ref count, IntPtr.Zero, ref length, IntPtr.Zero);
        if (result != InsufficientBuffer || count is 0 or > 256 || length is 0 or > 65536) return false;

        var pointers = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
        var names = IntPtr.Zero;
        try
        {
            names = Marshal.AllocHGlobal(checked((int)length * sizeof(char)));
            if (GetPackagesByPackageFamily(family, ref count, pointers, ref length, names) != 0) return false;
            for (int i = 0; i < count; i++)
            {
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointers, i * IntPtr.Size));
                if (name is not null && MeetsMinimum(name, minimum)) return true;
            }
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(names);
            Marshal.FreeHGlobal(pointers);
        }
    }

    internal static bool MeetsMinimum(string packageFullName, Version minimum)
    {
        var fields = packageFullName.Split('_');
        return fields.Length == 5 && fields[2].Equals("x64", StringComparison.OrdinalIgnoreCase) &&
            Version.TryParse(fields[1], out var version) && version >= minimum;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackagesByPackageFamily(string packageFamilyName, ref uint count,
        IntPtr packageFullNames, ref uint bufferLength, IntPtr buffer);
}
