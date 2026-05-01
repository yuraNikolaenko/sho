using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sho.App.Services;
using Sho.Core.Abstractions;
using Sho.Core.Models;
using Sho.Extractors;
using Sho.Search;

namespace Sho.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ITextExtractorRegistry _registry;
    private readonly BruteForceSearchEngine _brute;
    private readonly IndexedSearchEngine _indexed;
    private CancellationTokenSource? _cts;

    public ObservableCollection<FileSummary> Files { get; } = new();
    public ObservableCollection<SearchHit> Lines { get; } = new();

    [ObservableProperty] private string? _folderPath;
    [ObservableProperty] private string _queryText = string.Empty;
    [ObservableProperty] private bool _caseSensitive;
    [ObservableProperty] private bool _wholeWord;
    [ObservableProperty] private bool _useIndex = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private double _progressFraction;
    [ObservableProperty] private string? _progressStage;
    [ObservableProperty] private bool _indexExists;
    [ObservableProperty] private string? _currentFile;
    [ObservableProperty] private string _progressDetail = string.Empty;
    [ObservableProperty] private FileSummary? _selectedFile;
    [ObservableProperty] private bool _isLinesFiltered;
    [ObservableProperty] private SearchHit? _selectedHit;
    [ObservableProperty] private bool _showPreview;
    [ObservableProperty] private string? _previewHtml;
    [ObservableProperty] private bool _isPreviewSupported;
    public DocxPreviewService PreviewService { get; } = new();

    private readonly DispatcherTimer _heartbeat = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private DateTime _currentFileStartedUtc;
    private long _lastReportedElapsedMs;
    private int _lastFilesProcessed;
    private int _lastFilesTotal;

    public MainViewModel()
    {
        _registry = new TextExtractorRegistry();
        _brute = new BruteForceSearchEngine(_registry);
        _indexed = new IndexedSearchEngine(_registry);
        _heartbeat.Tick += OnHeartbeat;
    }

    partial void OnSelectedFileChanged(FileSummary? value)
    {
        IsLinesFiltered = value != null;
    }

    partial void OnSelectedHitChanged(SearchHit? value) => RefreshPreview();
    partial void OnShowPreviewChanged(bool value) => RefreshPreview();

    public string? CurrentMatcherTermsHint { get; set; }

    public void SetDarkTheme(bool dark)
    {
        _previewDarkTheme = dark;
        RefreshPreview();
    }
    private bool _previewDarkTheme = true;

    private void RefreshPreview()
    {
        if (!ShowPreview)
        {
            IsPreviewSupported = false;
            PreviewHtml = null;
            return;
        }

        var path = SelectedHit?.FilePath ?? SelectedFile?.FilePath;
        if (string.IsNullOrEmpty(path))
        {
            IsPreviewSupported = false;
            PreviewHtml = "<html><body style='background:#1e1e1e;color:#888;font-family:Segoe UI;padding:20px'>" +
                          "Select a line or a file to preview." +
                          "</body></html>";
            return;
        }

        IsPreviewSupported = PreviewService.IsSupported(path);
        var terms = (CurrentMatcherTermsHint ?? QueryText)
            .Split(new[] { ' ', '\t', '\r', '\n', '"', '+', '-', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1 && !IsLuceneKeyword(t))
            .ToList();
        PreviewHtml = PreviewService.Render(path, terms, _previewDarkTheme, SelectedHit?.LineNumber);
    }

    private static bool IsLuceneKeyword(string s) =>
        s.Equals("AND", StringComparison.Ordinal)
        || s.Equals("OR", StringComparison.Ordinal)
        || s.Equals("NOT", StringComparison.Ordinal);

    [RelayCommand]
    private void ClearFileFilter() => SelectedFile = null;

    private void OnHeartbeat(object? sender, EventArgs e)
    {
        if (!IsBusy) return;
        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        if (_lastFilesTotal == 0)
        {
            StatusText = ProgressStage ?? "Working…";
            return;
        }
        double pct = ProgressFraction * 100.0;
        double elapsedSec = _lastReportedElapsedMs / 1000.0;
        double fps = elapsedSec > 0 ? _lastFilesProcessed / elapsedSec : 0;
        var eta = ComputeEta(elapsedSec);
        var onCurrent = (DateTime.UtcNow - _currentFileStartedUtc).TotalSeconds;

        var sb = new System.Text.StringBuilder();
        sb.Append(ProgressStage ?? "Working")
          .Append(' ').Append(_lastFilesProcessed).Append('/').Append(_lastFilesTotal)
          .Append(" (").Append(pct.ToString("F1")).Append("%)");
        if (fps > 0) sb.Append(" · ").Append(fps.ToString("F1")).Append(" f/s");
        if (eta != null) sb.Append(" · ETA ").Append(FormatDuration(eta.Value));
        if (!string.IsNullOrEmpty(CurrentFile))
        {
            sb.Append(" · ").Append(Path.GetFileName(CurrentFile));
            if (onCurrent >= 2) sb.Append(" (").Append(FormatDuration(TimeSpan.FromSeconds(onCurrent))).Append(" on this file)");
        }
        StatusText = sb.ToString();
    }

    private TimeSpan? ComputeEta(double elapsedSec)
    {
        if (_lastFilesProcessed == 0 || _lastFilesTotal == 0 || elapsedSec <= 0) return null;
        int remaining = _lastFilesTotal - _lastFilesProcessed;
        if (remaining <= 0) return TimeSpan.Zero;
        double secPerFile = elapsedSec / _lastFilesProcessed;
        return TimeSpan.FromSeconds(secPerFile * remaining);
    }

    private static string FormatDuration(TimeSpan t)
    {
        if (t.TotalSeconds < 60) return $"{t.TotalSeconds:F0}s";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes}m{t.Seconds:D2}s";
        return $"{(int)t.TotalHours}h{t.Minutes:D2}m";
    }

    partial void OnFolderPathChanged(string? value) => _ = RefreshIndexStateAsync();

    private async Task RefreshIndexStateAsync()
    {
        if (string.IsNullOrWhiteSpace(FolderPath)) { IndexExists = false; return; }
        try { IndexExists = await _indexed.IsIndexBuiltAsync(FolderPath, CancellationToken.None); }
        catch { IndexExists = false; }
    }

    [RelayCommand]
    private void SelectFolder()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select folder to search",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (!string.IsNullOrWhiteSpace(FolderPath) && Directory.Exists(FolderPath))
            dlg.SelectedPath = FolderPath;

        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            FolderPath = dlg.SelectedPath;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task BuildIndexAsync()
    {
        if (string.IsNullOrWhiteSpace(FolderPath) || !Directory.Exists(FolderPath))
        {
            StatusText = "Select a valid folder first";
            return;
        }
        await RunAsync(async (progress, ct) =>
        {
            await _indexed.BuildIndexAsync(FolderPath!, progress, ct);
            await RefreshIndexStateAsync();
            StatusText = "Index built";
        }, "Building index");
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task DeleteIndexAsync()
    {
        if (string.IsNullOrWhiteSpace(FolderPath)) return;
        await _indexed.DeleteIndexAsync(FolderPath, CancellationToken.None);
        await RefreshIndexStateAsync();
        StatusText = "Index deleted";
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(FolderPath) || !Directory.Exists(FolderPath))
        {
            StatusText = "Select a valid folder";
            return;
        }
        if (string.IsNullOrEmpty(QueryText))
        {
            StatusText = "Enter search text";
            return;
        }

        var query = new SearchQuery(QueryText, CaseSensitive, WholeWord);
        ISearchEngine engine = UseIndex ? _indexed : _brute;

        await RunAsync(async (progress, ct) =>
        {
            var result = await engine.SearchAsync(FolderPath!, query, progress, ct);
            SelectedFile = null;
            SelectedHit = null;
            CurrentMatcherTermsHint = QueryText;
            PreviewService.ClearCache();
            Files.Clear();
            Lines.Clear();
            foreach (var f in result.Files) Files.Add(f);
            foreach (var l in result.Lines) Lines.Add(l);
            RefreshPreview();
            if (!string.IsNullOrEmpty(result.Error))
                StatusText = result.Error!;
            else
                StatusText = $"{result.FilesMatched} files / {result.TotalHits} hits in {result.Elapsed.TotalSeconds:F2}s ({engine.Name})";
        }, $"Searching ({engine.Name})");
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _cts?.Cancel();

    private bool CanRun() => !IsBusy;

    public void OpenFile(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open file: {ex.Message}", "sho", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void RevealInExplorer(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private async Task RunAsync(Func<IProgress<IndexProgress>, CancellationToken, Task> action, string startStatus)
    {
        if (IsBusy) return;
        IsBusy = true;
        ProgressFraction = 0;
        ProgressStage = startStatus;
        StatusText = startStatus + "…";
        CurrentFile = null;
        _lastFilesProcessed = 0;
        _lastFilesTotal = 0;
        _lastReportedElapsedMs = 0;
        _currentFileStartedUtc = DateTime.UtcNow;
        _heartbeat.Start();

        _cts = new CancellationTokenSource();
        SearchCommand.NotifyCanExecuteChanged();
        BuildIndexCommand.NotifyCanExecuteChanged();
        DeleteIndexCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();

        var progress = new Progress<IndexProgress>(p =>
        {
            ProgressFraction = p.Fraction;
            ProgressStage = p.Stage;
            _lastFilesProcessed = p.FilesProcessed;
            _lastFilesTotal = p.FilesTotal;
            _lastReportedElapsedMs = p.ElapsedMs;
            if (p.CurrentFile != CurrentFile)
            {
                CurrentFile = p.CurrentFile;
                _currentFileStartedUtc = DateTime.UtcNow;
            }
            UpdateStatusText();
        });

        try
        {
            await action(progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled";
        }
        catch (Exception ex)
        {
            StatusText = "Error: " + ex.Message;
        }
        finally
        {
            _heartbeat.Stop();
            IsBusy = false;
            ProgressFraction = 0;
            CurrentFile = null;
            _cts?.Dispose();
            _cts = null;
            SearchCommand.NotifyCanExecuteChanged();
            BuildIndexCommand.NotifyCanExecuteChanged();
            DeleteIndexCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
        }
    }
}
