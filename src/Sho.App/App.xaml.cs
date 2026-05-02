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
        // Headless flag: regenerate the embedded app.ico without showing the UI.
        // Usage: Shozilla.exe --write-icon "src\Sho.App\Resources\app.ico"
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (string.Equals(e.Args[i], "--write-icon", StringComparison.OrdinalIgnoreCase) && i + 1 < e.Args.Length)
            {
                AppIconFactory.WriteIcoFile(e.Args[i + 1]);
                Shutdown(0);
                return;
            }
        }

        Settings = SettingsService.Load();

        var theme = string.Equals(Settings.Theme, "Light", StringComparison.OrdinalIgnoreCase)
            ? ApplicationTheme.Light
            : ApplicationTheme.Dark;
        ApplicationThemeManager.Apply(theme);

        LocalizationService.Apply(Settings.Language);

        base.OnStartup(e);
    }
}
