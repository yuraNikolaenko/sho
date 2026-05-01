using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Sho.App.Services;

public static class FileIconCache
{
    private static readonly ConcurrentDictionary<string, BitmapSource?> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static BitmapSource? GetForPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) ext = "<noext>";
        return _cache.GetOrAdd(ext, Load);
    }

    private static BitmapSource? Load(string ext)
    {
        try
        {
            var fakePath = ext == "<noext>" ? "icon" : "icon" + ext;
            var info = new SHFILEINFO();
            const uint FILE_ATTRIBUTE_NORMAL = 0x80;
            const uint SHGFI_ICON = 0x100;
            const uint SHGFI_SMALLICON = 0x1;
            const uint SHGFI_USEFILEATTRIBUTES = 0x10;

            IntPtr res = SHGetFileInfo(fakePath, FILE_ATTRIBUTE_NORMAL, ref info,
                (uint)Marshal.SizeOf(info), SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);
            if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

            try
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                return bmp;
            }
            finally { DestroyIcon(info.hIcon); }
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
