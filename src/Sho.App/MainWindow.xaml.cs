using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Sho.App.Services;
using Sho.App.ViewModels;
using Sho.Core.Models;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Sho.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var lang = App.Settings.Language;
        foreach (ComboBoxItem item in LanguageCombo.Items)
        {
            if ((item.Tag as string) == lang) { LanguageCombo.SelectedItem = item; break; }
        }
        if (LanguageCombo.SelectedItem == null) LanguageCombo.SelectedIndex = 0;

        UpdateThemeIcon();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void FilesGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FilesGrid.SelectedItem is FileSummary fs) Vm.OpenFile(fs.FilePath);
    }

    private void LinesGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (LinesGrid.SelectedItem is SearchHit hit) Vm.OpenFile(hit.FilePath);
    }

    private void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is FileSummary fs) Vm.OpenFile(fs.FilePath);
    }

    private void MenuReveal_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is FileSummary fs) Vm.RevealInExplorer(fs.FilePath);
    }

    private void MenuCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is FileSummary fs)
        {
            try { Clipboard.SetText(fs.FilePath); } catch { }
        }
    }

    private void MenuLineOpen_Click(object sender, RoutedEventArgs e)
    {
        if (LinesGrid.SelectedItem is SearchHit hit) Vm.OpenFile(hit.FilePath);
    }

    private void MenuLineReveal_Click(object sender, RoutedEventArgs e)
    {
        if (LinesGrid.SelectedItem is SearchHit hit) Vm.RevealInExplorer(hit.FilePath);
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        var current = ApplicationThemeManager.GetAppTheme();
        var next = current == ApplicationTheme.Dark ? ApplicationTheme.Light : ApplicationTheme.Dark;
        ApplicationThemeManager.Apply(next);
        App.Settings.Theme = next == ApplicationTheme.Dark ? "Dark" : "Light";
        App.SettingsService.Save(App.Settings);
        UpdateThemeIcon();
    }

    private void UpdateThemeIcon()
    {
        var icon = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark
            ? SymbolRegular.WeatherSunny24
            : SymbolRegular.WeatherMoon24;
        ThemeButton.Icon = new SymbolIcon { Symbol = icon };
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageCombo.SelectedItem is ComboBoxItem item && item.Tag is string lang)
        {
            if (lang != App.Settings.Language || lang != LocalizationService.CurrentLanguage)
            {
                LocalizationService.Apply(lang);
                App.Settings.Language = lang;
                App.SettingsService.Save(App.Settings);
            }
        }
    }
}
