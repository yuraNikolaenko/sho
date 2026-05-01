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
        var grid = new System.Windows.Controls.Grid
        {
            Width = size,
            Height = size,
            Background = BackgroundBrush,
        };

        var symbol = new SymbolIcon
        {
            Symbol = SymbolRegular.SearchInfo24,
            FontSize = size * 0.7,
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
}
