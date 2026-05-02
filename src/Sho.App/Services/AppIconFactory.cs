using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;

namespace Sho.App.Services;

public static class AppIconFactory
{
    public static readonly System.Windows.Media.Color AccentColor =
        System.Windows.Media.Color.FromRgb(0xFF, 0x57, 0x22);

    private static readonly SolidColorBrush AccentBrush;
    private static readonly SolidColorBrush BackgroundBrush;

    static AppIconFactory()
    {
        AccentBrush = new SolidColorBrush(AccentColor);
        AccentBrush.Freeze();
        BackgroundBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x1A));
        BackgroundBrush.Freeze();
    }

    public static System.Windows.Media.ImageSource Create(int size = 64)
    {
        // Transparent background — taskbar / Alt-Tab / desktop shortcut composite
        // the icon over their own surface, so a dark fill looks like a black box.
        var grid = new System.Windows.Controls.Grid
        {
            Width = size,
            Height = size,
            Background = System.Windows.Media.Brushes.Transparent,
        };

        var symbol = new SymbolIcon
        {
            Symbol = SymbolRegular.SearchInfo24,
            FontSize = size * 0.95,
            Foreground = AccentBrush,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
        grid.Children.Add(symbol);

        grid.Measure(new System.Windows.Size(size, size));
        grid.Arrange(new System.Windows.Rect(0, 0, size, size));
        grid.UpdateLayout();

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(grid);
        rtb.Freeze();
        return rtb;
    }

    public static SymbolIcon CreateTitleBarIcon() => new()
    {
        Symbol = SymbolRegular.SearchInfo24,
        Foreground = AccentBrush,
    };

    /// <summary>
    /// Render the icon at each requested size and write a multi-resolution
    /// PNG-payload .ico file. Used by the "Shozilla.exe --write-icon &lt;path&gt;"
    /// CLI flag to (re)generate the file embedded as Win32 ApplicationIcon.
    /// </summary>
    public static void WriteIcoFile(string path, int[]? sizes = null)
    {
        sizes ??= new[] { 16, 24, 32, 48, 64, 128, 256 };

        var pngBlobs = new List<byte[]>(sizes.Length);
        foreach (var s in sizes)
            pngBlobs.Add(EncodeAsPng(s));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);

        // ICONDIR
        w.Write((ushort)0);            // reserved
        w.Write((ushort)1);            // type = 1 (icon)
        w.Write((ushort)sizes.Length); // count

        const int dirEntrySize = 16;
        int dataOffset = 6 + dirEntrySize * sizes.Length;

        for (int i = 0; i < sizes.Length; i++)
        {
            int s = sizes[i];
            int blobLen = pngBlobs[i].Length;
            w.Write((byte)(s == 256 ? 0 : s));   // width  (0 means 256)
            w.Write((byte)(s == 256 ? 0 : s));   // height (0 means 256)
            w.Write((byte)0);                    // color count
            w.Write((byte)0);                    // reserved
            w.Write((ushort)1);                  // planes
            w.Write((ushort)32);                 // bit count
            w.Write((uint)blobLen);              // bytes in resource
            w.Write((uint)dataOffset);           // offset
            dataOffset += blobLen;
        }

        foreach (var blob in pngBlobs)
            w.Write(blob);
    }

    private static byte[] EncodeAsPng(int size)
    {
        // Transparent background, large glyph — same composition as Create().
        var grid = new System.Windows.Controls.Grid
        {
            Width = size,
            Height = size,
            Background = System.Windows.Media.Brushes.Transparent,
        };
        var symbol = new SymbolIcon
        {
            Symbol = SymbolRegular.SearchInfo24,
            FontSize = size * 0.95,
            Foreground = AccentBrush,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
        grid.Children.Add(symbol);
        grid.Measure(new System.Windows.Size(size, size));
        grid.Arrange(new System.Windows.Rect(0, 0, size, size));
        grid.UpdateLayout();

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(grid);
        rtb.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
