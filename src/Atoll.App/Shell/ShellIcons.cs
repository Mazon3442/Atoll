using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Atoll.Shell;

/// <summary>Windows Explorer icons for files and folders, cached per extension (or per path for .exe/.lnk/folders).</summary>
public static class ShellIcons
{
    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);

    private static readonly Dictionary<string, BitmapSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static BitmapSource? For(string path)
    {
        var ext = Path.GetExtension(path);
        var key = Directory.Exists(path) || ext is ".exe" or ".lnk" or ".ico" or ".url" || ext.Length == 0 ? path : ext;
        if (Cache.TryGetValue(key, out var cached)) return cached;

        BitmapSource? icon = null;
        var info = new SHFILEINFO();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON) != 0 && info.hIcon != 0)
        {
            try
            {
                icon = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                icon.Freeze();
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        if (Cache.Count > 500) Cache.Clear();
        Cache[key] = icon;
        return icon;
    }
}
