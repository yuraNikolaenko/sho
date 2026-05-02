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
        Icon = AppIconFactory.Create();
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            try { PreviewWebView.Dispose(); } catch { }
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Vm.ShowPreview = App.Settings.ShowPreview;
        Vm.AutoUpdateIndex = App.Settings.AutoUpdateIndex;
        Vm.CompactMode = App.Settings.CompactMode;
        Vm.SetDarkTheme(ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark);

        var folders = App.Settings.LastFolders ?? new List<string>();
        if (folders.Count == 0 && !string.IsNullOrEmpty(App.Settings.LastFolder))
            folders = new List<string> { App.Settings.LastFolder };
        foreach (var f in folders.Where(p => Directory.Exists(p)))
            Vm.FolderPaths.Add(f);

        Vm.FolderPaths.CollectionChanged += (_, _) =>
        {
            App.Settings.LastFolders = Vm.FolderPaths.ToList();
            App.SettingsService.Save(App.Settings);
        };

        foreach (var q in App.Settings.QueryHistory ?? new List<string>())
            Vm.QueryHistory.Add(q);

        Vm.QueryHistory.CollectionChanged += (_, _) =>
        {
            App.Settings.QueryHistory = Vm.QueryHistory.ToList();
            App.SettingsService.Save(App.Settings);
        };

        Vm.PropertyChanged += OnVmPropertyChanged;
        Vm.SelectedFileChanged += OnVmSelectedFileChanged;
        Closed += (_, _) => { try { Vm.Watcher.Dispose(); } catch { } };

        UpdateThemeIcon();
        UpdateLanguageLabel();
        UpdatePreviewButton();
        UpdatePreviewLayout();
        UpdateCompactToggleIcon();
        UpdateContrastBrushes(ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark);

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
        else if (e.PropertyName == nameof(MainViewModel.AutoUpdateIndex))
        {
            App.Settings.AutoUpdateIndex = Vm.AutoUpdateIndex;
            App.SettingsService.Save(App.Settings);
        }
        else if (e.PropertyName == nameof(MainViewModel.CompactMode))
        {
            App.Settings.CompactMode = Vm.CompactMode;
            App.SettingsService.Save(App.Settings);
        }
    }

    private void OnVmSelectedFileChanged(object? sender, Sho.Core.Models.FileSummary? file)
    {
        // When the toggle is off, scrolling to the first matching row makes the
        // bold-row highlight discoverable in long Lines panels. With the toggle on
        // there's nothing to scroll to — the filter already moves the rows up.
        if (file == null || Vm.OnlySelectedFileLines) return;
        var firstHit = Vm.Lines.FirstOrDefault(l =>
            string.Equals(l.FilePath, file.FilePath, StringComparison.OrdinalIgnoreCase));
        if (firstHit == null) return;
        // Defer to dispatcher so the row exists in visual tree after any pending
        // CollectionView refresh.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { LinesGrid.ScrollIntoView(firstHit); } catch { }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void CompactToggleButton_Click(object sender, RoutedEventArgs e)
    {
        Vm.CompactMode = !Vm.CompactMode;
        UpdateCompactToggleIcon();
    }

    private void UpdateCompactToggleIcon()
    {
        CompactToggleButton.Icon = new SymbolIcon
        {
            Symbol = Vm.CompactMode ? SymbolRegular.ChevronDown24 : SymbolRegular.ChevronUp24
        };
        CompactToggleButton.ToolTip =
            (string)Application.Current.Resources["Str.CompactModeTooltip"];
    }

    private void AiAnalysisButton_Click(object sender, RoutedEventArgs e)
    {
        // Placeholder for the local-LLM analysis hooked up in a future session.
        System.Windows.MessageBox.Show(
            (string)Application.Current.Resources["Str.AiAnalysisStub"],
            (string)Application.Current.Resources["Str.AppTitle"],
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    private void QueryComboBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            // Commit the editable text to the bound property and close the dropdown
            QueryComboBox.IsDropDownOpen = false;
            if (Vm.SearchCommand.CanExecute(null))
                Vm.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void ChooseFoldersButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Sho.App.Views.FolderPickerWindow(Vm.FolderPaths.ToList())
        {
            Owner = this
        };
        if (dlg.ShowDialog() == true)
        {
            Vm.FolderPaths.Clear();
            foreach (var p in dlg.SelectedPaths)
                Vm.FolderPaths.Add(p);
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
        UpdateContrastBrushes(next == ApplicationTheme.Dark);
        Vm.SetDarkTheme(next == ApplicationTheme.Dark);
    }

    private static void UpdateContrastBrushes(bool isDark)
    {
        var bg = new System.Windows.Media.SolidColorBrush(
            isDark ? System.Windows.Media.Color.FromRgb(0, 0, 0)
                   : System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF));
        bg.Freeze();
        var fg = new System.Windows.Media.SolidColorBrush(
            isDark ? System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF)
                   : System.Windows.Media.Color.FromRgb(0, 0, 0));
        fg.Freeze();
        Application.Current.Resources["ResultsBackground"] = bg;
        Application.Current.Resources["ResultsForeground"] = fg;
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
        UpdatePreviewLayout();
        if (Vm.ShowPreview) await EnsureWebViewAsync();
    }

    // Remembers the user's last-resized preview-row height so toggling the panel
    // off and back on doesn't snap to the default 3*. Updated whenever we collapse.
    private GridLength _savedPreviewRowHeight = new GridLength(3, GridUnitType.Star);

    private void UpdatePreviewLayout()
    {
        if (Vm.ShowPreview)
        {
            PreviewSplitterRow.Height = GridLength.Auto;
            PreviewRow.Height = _savedPreviewRowHeight;
        }
        else
        {
            // Capture current size so re-enabling restores it.
            if (PreviewRow.Height.Value > 0) _savedPreviewRowHeight = PreviewRow.Height;
            PreviewSplitterRow.Height = new GridLength(0);
            PreviewRow.Height = new GridLength(0);
        }
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
