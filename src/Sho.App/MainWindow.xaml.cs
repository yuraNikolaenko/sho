using System.ComponentModel;
using System.IO;
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
    private bool _webViewInitialized;
    private bool _webViewInitializing;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            try { PreviewWebView.Dispose(); } catch { }
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Vm.ShowPreview = App.Settings.ShowPreview;
        Vm.SetDarkTheme(ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark);

        if (!string.IsNullOrEmpty(App.Settings.LastFolder) && Directory.Exists(App.Settings.LastFolder))
            Vm.FolderPath = App.Settings.LastFolder;

        Vm.PropertyChanged += OnVmPropertyChanged;

        UpdateThemeIcon();
        UpdateLanguageLabel();
        UpdatePreviewButton();

        if (Vm.ShowPreview) await EnsureWebViewAsync();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private async void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ShowPreview))
        {
            App.Settings.ShowPreview = Vm.ShowPreview;
            App.SettingsService.Save(App.Settings);
            if (Vm.ShowPreview) await EnsureWebViewAsync();
        }
        else if (e.PropertyName == nameof(MainViewModel.PreviewFilePath))
        {
            NavigatePreview(Vm.PreviewFilePath);
        }
        else if (e.PropertyName == nameof(MainViewModel.FolderPath))
        {
            App.Settings.LastFolder = Vm.FolderPath;
            App.SettingsService.Save(App.Settings);
        }
    }

    private async System.Threading.Tasks.Task EnsureWebViewAsync()
    {
        if (_webViewInitialized || _webViewInitializing) return;
        _webViewInitializing = true;
        try
        {
            var userDataDir = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "sho", "webview2");
            Directory.CreateDirectory(userDataDir);
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null, userDataFolder: userDataDir);
            await PreviewWebView.EnsureCoreWebView2Async(env);
            PreviewWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            PreviewWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            PreviewWebView.CoreWebView2.Settings.IsZoomControlEnabled = true;
            PreviewWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(30, 30, 30);

            PreviewWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "sho-preview",
                Vm.PreviewService.PreviewDirectory,
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

            PreviewWebView.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess)
                {
                    PreviewError.Visibility = Visibility.Collapsed;
                    return;
                }
                var status = args.WebErrorStatus;
                if (status == Microsoft.Web.WebView2.Core.CoreWebView2WebErrorStatus.OperationCanceled
                    || status == Microsoft.Web.WebView2.Core.CoreWebView2WebErrorStatus.ConnectionAborted)
                {
                    return;
                }
                PreviewError.Visibility = Visibility.Visible;
                PreviewError.Text = $"Preview navigation failed: {status}";
            };

            _webViewInitialized = true;
            PreviewError.Visibility = Visibility.Collapsed;

            NavigatePreview(Vm.PreviewFilePath);
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("WebView2 init failed: " + ex);
            PreviewWebView.Visibility = Visibility.Collapsed;
            PreviewError.Visibility = Visibility.Visible;
            PreviewError.Text =
                "DOCX preview requires Microsoft Edge WebView2 Runtime. " +
                "It is preinstalled on Windows 11; on Windows 10 install from " +
                "https://developer.microsoft.com/microsoft-edge/webview2/\n\n" +
                "Details: " + ex.Message;
        }
        finally
        {
            _webViewInitializing = false;
        }
    }

    private void NavigatePreview(string? filePath)
    {
        if (!_webViewInitialized) return;
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            PreviewWebView.CoreWebView2.NavigateToString("<html><body style='background:#1e1e1e'></body></html>");
            return;
        }
        try
        {
            var fileName = Path.GetFileName(filePath);
            // Timestamp param defeats any caching so the latest highlighted HTML is fetched
            var url = $"https://sho-preview/{fileName}?t={System.DateTime.UtcNow.Ticks}";
            PreviewWebView.CoreWebView2.Navigate(url);
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Preview navigation failed: " + ex);
            PreviewError.Visibility = Visibility.Visible;
            PreviewError.Text = "Preview navigation error: " + ex.Message;
        }
    }

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
        Vm.SetDarkTheme(next == ApplicationTheme.Dark);
    }

    private void UpdateThemeIcon()
    {
        var icon = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark
            ? SymbolRegular.WeatherSunny24
            : SymbolRegular.WeatherMoon24;
        ThemeButton.Icon = new SymbolIcon { Symbol = icon };
    }

    private async void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        Vm.ShowPreview = !Vm.ShowPreview;
        UpdatePreviewButton();
        if (Vm.ShowPreview) await EnsureWebViewAsync();
    }

    private void UpdatePreviewButton()
    {
        PreviewButton.Appearance = Vm.ShowPreview
            ? Wpf.Ui.Controls.ControlAppearance.Primary
            : Wpf.Ui.Controls.ControlAppearance.Secondary;
        PreviewButton.Icon = new SymbolIcon
        {
            Symbol = Vm.ShowPreview ? SymbolRegular.Eye24 : SymbolRegular.EyeOff24
        };
    }

    private void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        var current = LocalizationService.CurrentLanguage;
        var next = current == "uk" ? "en" : "uk";
        LocalizationService.Apply(next);
        App.Settings.Language = next;
        App.SettingsService.Save(App.Settings);
        UpdateLanguageLabel();
    }

    private void UpdateLanguageLabel()
    {
        LanguageLabel.Text = LocalizationService.CurrentLanguage == "uk" ? "UA" : "EN";
    }
}
