using System.Windows;
using Sho.App.Services;
using Wpf.Ui.Appearance;

namespace Sho.App;

public partial class App : System.Windows.Application
{
    public static AppSettings Settings { get; private set; } = new();
    public static SettingsService SettingsService { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        Settings = SettingsService.Load();

        var theme = string.Equals(Settings.Theme, "Light", StringComparison.OrdinalIgnoreCase)
            ? ApplicationTheme.Light
            : ApplicationTheme.Dark;
        ApplicationThemeManager.Apply(theme);

        LocalizationService.Apply(Settings.Language);

        base.OnStartup(e);
    }
}
