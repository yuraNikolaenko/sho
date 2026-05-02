using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
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

    public BulkObservableCollection<FileSummary> Files { get; } = new();
    public BulkObservableCollection<SearchHit> Lines { get; } = new();
    public ObservableCollection<string> FolderPaths { get; } = new();
    public ObservableCollection<string> QueryHistory { get; } = new();
    public const int MaxQueryHistory = 50;

    [ObservableProperty] private string _folderSummary = string.Empty;
    [ObservableProperty] private string _queryText = string.Empty;
    [ObservableProperty] private bool _caseSensitive;
    [ObservableProperty] private bool _wholeWord;
    [ObservableProperty] private bool _useIndex = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private double _progressFraction;
    [ObservableProperty] private string? _progressStage;
    [ObservableProperty] private bool _indexExists;
    [ObservableProperty] private string _indexInfoText = string.Empty;
    [ObservableProperty] private string? _currentFile;
    [ObservableProperty] private string _progressDetail = string.Empty;
    [ObservableProperty] private FileSummary? _selectedFile;
    [ObservableProperty] private bool _isLinesFiltered;
    [ObservableProperty] private SearchHit? _selectedHit;
    [ObservableProperty] private bool _showPreview;
    [ObservableProperty] private string? _previewFilePath;
    [ObservableProperty] private string _pathFilterText = string.Empty;
    [ObservableProperty] private string _filterCountsText = string.Empty;
    [ObservableProperty] private bool _autoUpdateIndex = true;
    [ObservableProperty] private string _watcherStatusText = string.Empty;
    [ObservableProperty] private bool _onlySelectedFileLines;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandedToolbarVisibility))]
    private bool _compactMode;
    public Visibility ExpandedToolbarVisibility => CompactMode ? Visibility.Collapsed : Visibility.Visible;
    public DocxPreviewService PreviewService { get; } = new();
    public IndexWatcherService Watcher { get; }

    private readonly ICollectionView _filesView;
    private readonly ICollectionView _linesView;
    private string[] _filterTokens = Array.Empty<string>();

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
        FolderPaths.CollectionChanged += (_, _) => OnFolderPathsChanged();

        _filesView = CollectionViewSource.GetDefaultView(Files);
        _filesView.Filter = PathFilterPredicate;
        _linesView = CollectionViewSource.GetDefaultView(Lines);
        _linesView.Filter = LinesFilterPredicate;
        Files.CollectionChanged += (_, _) => UpdateFilterCounts();
        Lines.CollectionChanged += (_, _) => UpdateFilterCounts();

        Watcher = new IndexWatcherService(_indexed, _registry.SupportedExtensions);
        Watcher.Updated += OnWatcherUpdated;
        Watcher.UpdateFailed += OnWatcherFailed;
    }

    private void OnWatcherUpdated(object? sender, IndexUpdateEventArgs e)
    {
        Application.Current?.Dispatcher.Invoke(async () =>
        {
            WatcherStatusText = $"Auto-update: {e.Changed} file(s) at {e.UtcTime.ToLocalTime():HH:mm:ss}";
            await RefreshIndexStateAsync();
        });
    }

    private void OnWatcherFailed(object? sender, Exception e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            WatcherStatusText = "Auto-update error: " + e.Message;
        });
    }

    partial void OnAutoUpdateIndexChanged(bool value) => UpdateWatcherState();

    public void UpdateWatcherState()
    {
        if (AutoUpdateIndex && IndexExists && SelectedRoots.Count > 0)
        {
            Watcher.Start(SelectedRoots);
            WatcherStatusText = "Auto-update: on";
        }
        else
        {
            Watcher.Stop();
            WatcherStatusText = string.Empty;
        }
    }

    partial void OnPathFilterTextChanged(string value)
    {
        _filterTokens = string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        _filesView.Refresh();
        _linesView.Refresh();
        UpdateFilterCounts();
    }

    private bool PathFilterPredicate(object item)
    {
        if (_filterTokens.Length == 0) return true;
        var path = item switch
        {
            FileSummary f => f.FilePath,
            SearchHit h => h.FilePath,
            _ => null,
        };
        if (string.IsNullOrEmpty(path)) return false;
        for (int i = 0; i < _filterTokens.Length; i++)
            if (path.IndexOf(_filterTokens[i], StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        return true;
    }

    /// <summary>
    /// Lines view predicate: same path filter as Files PLUS an optional
    /// "show only lines from the selected file" toggle. Lets the user collapse
    /// a several-thousand-row Lines panel down to just the file they're
    /// inspecting without losing access to the unfiltered view (toggle off).
    /// </summary>
    private bool LinesFilterPredicate(object item)
    {
        if (!PathFilterPredicate(item)) return false;
        if (OnlySelectedFileLines && SelectedFile != null
            && item is SearchHit h
            && !string.Equals(h.FilePath, SelectedFile.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return true;
    }

    partial void OnOnlySelectedFileLinesChanged(bool value)
    {
        _linesView.Refresh();
        UpdateFilterCounts();
    }

    private void UpdateFilterCounts()
    {
        if (_filterTokens.Length == 0)
        {
            FilterCountsText = string.Empty;
            return;
        }
        int filesShown = 0;
        foreach (var f in Files) if (PathFilterPredicate(f)) filesShown++;
        int linesShown = 0;
        foreach (var l in Lines) if (PathFilterPredicate(l)) linesShown++;
        FilterCountsText = $"{filesShown}/{Files.Count} · {linesShown}/{Lines.Count}";
    }

    [RelayCommand]
    private void ClearPathFilter() => PathFilterText = string.Empty;

    private void OnFolderPathsChanged()
    {
        FolderSummary = FolderPaths.Count switch
        {
            0 => string.Empty,
            1 => Path.GetFileName(FolderPaths[0].TrimEnd('\\', '/'))
                 + " (" + FolderPaths[0] + ")",
            _ => $"{FolderPaths.Count} folders",
        };
        _ = RefreshIndexStateAsync();
        // Roots changed → re-arm watcher (or stop it if folders cleared).
        UpdateWatcherState();
    }

    private bool _suppressSelectionSync;

    /// <summary>
    /// Raised after SelectedFile changes so the View can scroll the Lines DataGrid
    /// to the first matching row when the file-filter toggle is off.
    /// </summary>
    public event EventHandler<FileSummary?>? SelectedFileChanged;

    partial void OnSelectedFileChanged(FileSummary? value)
    {
        IsLinesFiltered = value != null;
        // Re-evaluate the Lines view: if the toggle is on, only the selected
        // file's lines remain visible.
        _linesView?.Refresh();
        UpdateFilterCounts();
        SelectedFileChanged?.Invoke(this, value);
        if (_suppressSelectionSync) return;
        // File click drives WHICH document is shown. Drop a stale hit selection
        // from a different file so RefreshPreview uses this file's path.
        if (value != null && SelectedHit != null
            && !string.Equals(SelectedHit.FilePath, value.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            _suppressSelectionSync = true;
            try { SelectedHit = null; }
            finally { _suppressSelectionSync = false; }
        }
        RefreshPreview();
    }

    partial void OnSelectedHitChanged(SearchHit? value)
    {
        if (_suppressSelectionSync) return;
        // Line click drives WHERE in the preview (mark index). If the line is
        // from a different file, auto-select that file too so the preview
        // switches to it and then positions to this line.
        if (value != null)
        {
            var fileMatch = Files.FirstOrDefault(f =>
                string.Equals(f.FilePath, value.FilePath, StringComparison.OrdinalIgnoreCase));
            if (fileMatch != null && !ReferenceEquals(fileMatch, SelectedFile))
            {
                _suppressSelectionSync = true;
                try { SelectedFile = fileMatch; }
                finally { _suppressSelectionSync = false; }
            }
        }
        RefreshPreview();
    }

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
            PreviewFilePath = null;
            return;
        }

        // File selection drives WHICH document is in preview; line selection drives
        // WHERE within it (mark index). When only a file is picked, scroll to the
        // first match. When a line is picked, scroll to that line's mark in the
        // line's parent file.
        var path = SelectedHit?.FilePath ?? SelectedFile?.FilePath;
        int markIdx = ComputeMarkIndexForCurrentSelection(path);
        var queryText = CurrentMatcherTermsHint ?? QueryText;
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) User's literal tokens (split by punctuation / Lucene operators).
        foreach (var t in queryText.Split(
            new[] { ' ', '\t', '\r', '\n', '"', '+', '-', '(', ')' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (t.Length > 1 && !IsLuceneKeyword(t)) terms.Add(t);
        }

        // 2) Analyzed lemmas — covers the case where the user typed lemma form
        //    but the document has an inflected form whose lemma equals the query.
        // 3) Stems (lemma minus 1-2 trailing chars) — bridges UA case-endings so
        //    a query "ніколаєнко" still highlights "Ніколаєнка" / "НІКОЛАЄНКА"
        //    via prefix match. Only for ≥6-char terms to limit false positives.
        try
        {
            foreach (var lemma in IndexedSearchEngine.AnalyzeToTokens(queryText))
            {
                if (string.IsNullOrEmpty(lemma)) continue;
                terms.Add(lemma);
                if (lemma.Length >= 6) terms.Add(lemma.Substring(0, lemma.Length - 2));
            }
        }
        catch { /* analyzer failure is non-fatal for preview */ }

        PreviewFilePath = PreviewService.Render(path, terms, _previewDarkTheme, markIdx);
    }

    /// <summary>
    /// Find the SelectedHit's index among hits of <paramref name="filePath"/> in
    /// the Lines collection. Marks in the rendered HTML are numbered in document
    /// order top-to-bottom, and Lines is sorted by (file, line number) which is
    /// the same order — so position-within-file equals mark id (m0, m1, ...).
    /// </summary>
    private int ComputeMarkIndexForCurrentSelection(string? filePath)
    {
        if (SelectedHit == null || string.IsNullOrEmpty(filePath)) return 0;
        if (!string.Equals(SelectedHit.FilePath, filePath, StringComparison.OrdinalIgnoreCase)) return 0;
        int markIdx = 0;
        foreach (var l in Lines)
        {
            if (!string.Equals(l.FilePath, filePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (ReferenceEquals(l, SelectedHit)) return markIdx;
            markIdx++;
        }
        return 0;
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

    public IReadOnlyList<string> SelectedRoots =>
        FolderPaths.Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p)).ToList();

    private async Task RefreshIndexStateAsync()
    {
        var roots = SelectedRoots;
        if (roots.Count == 0)
        {
            IndexExists = false;
            IndexInfoText = "No folders selected";
            return;
        }
        try
        {
            IndexExists = await _indexed.IsIndexBuiltAsync(roots, CancellationToken.None);
            if (IndexExists)
            {
                var meta = await _indexed.GetIndexMetadataAsync(roots, CancellationToken.None);
                if (meta != null)
                {
                    var localTime = meta.BuiltUtc.ToLocalTime();
                    IndexInfoText = $"Index · {localTime:yyyy-MM-dd HH:mm} · {meta.DocumentCount:N0} files";
                    if (!string.Equals(meta.AnalyzerVersion, IndexedSearchEngine.AnalyzerVersion, StringComparison.Ordinal))
                        IndexInfoText += " · rebuild for UA morphology";
                }
                else
                {
                    // Old index pre-dating meta.json — likely built with StandardAnalyzer.
                    IndexInfoText = "Index · built · rebuild for UA morphology";
                }
            }
            else
            {
                IndexInfoText = "Index · not built";
            }
        }
        catch
        {
            IndexExists = false;
            IndexInfoText = "Index · error";
        }
        UpdateWatcherState();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task BuildIndexAsync()
    {
        var roots = SelectedRoots;
        if (roots.Count == 0)
        {
            StatusText = "Select at least one folder";
            return;
        }
        // Stop watcher while we hold the writer.
        Watcher.Stop();
        await RunAsync(async (progress, ct) =>
        {
            await _indexed.BuildIndexAsync(roots, progress, ct);
            await RefreshIndexStateAsync();
            StatusText = "Index built";
        }, "Building index");
        UpdateWatcherState();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task UpdateIndexAsync()
    {
        var roots = SelectedRoots;
        if (roots.Count == 0)
        {
            StatusText = "Select at least one folder";
            return;
        }
        if (!IndexExists)
        {
            StatusText = "Index not built";
            return;
        }
        Watcher.Stop();
        await RunAsync(async (progress, ct) =>
        {
            int changed = await _indexed.SyncIndexAsync(roots, progress, ct);
            await RefreshIndexStateAsync();
            StatusText = changed == 0 ? "Index up to date" : $"Index synced · {changed} changes";
        }, "Updating index");
        UpdateWatcherState();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task DeleteIndexAsync()
    {
        var roots = SelectedRoots;
        if (roots.Count == 0) return;
        Watcher.Stop();
        await _indexed.DeleteIndexAsync(roots, CancellationToken.None);
        await RefreshIndexStateAsync();
        StatusText = "Index deleted";
        UpdateWatcherState();
    }

    public void RememberQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        var existing = QueryHistory.IndexOf(query);
        if (existing >= 0) QueryHistory.RemoveAt(existing);
        QueryHistory.Insert(0, query);
        while (QueryHistory.Count > MaxQueryHistory)
            QueryHistory.RemoveAt(QueryHistory.Count - 1);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task SearchAsync()
    {
        var roots = SelectedRoots;
        if (roots.Count == 0)
        {
            StatusText = "Select at least one folder";
            return;
        }
        // Snapshot the query before mutating QueryHistory: if the current text was
        // selected from the dropdown, RememberQuery removes-and-reinserts the
        // matching item, which makes the editable ComboBox drop SelectedItem and
        // clear its bound Text — wiping QueryText to "" mid-method.
        var queryText = QueryText;
        if (string.IsNullOrEmpty(queryText))
        {
            StatusText = "Enter search text";
            return;
        }

        RememberQuery(queryText);
        if (QueryText != queryText) QueryText = queryText;

        var query = new SearchQuery(queryText, CaseSensitive, WholeWord);
        ISearchEngine engine = UseIndex ? _indexed : _brute;

        await RunAsync(async (progress, ct) =>
        {
            var result = await engine.SearchAsync(roots, query, progress, ct);
            SelectedFile = null;
            SelectedHit = null;
            CurrentMatcherTermsHint = queryText;
            PreviewService.ClearCache();
            // Sort first, then bulk-replace: each ObservableCollection emits a single
            // Reset notification so the bound DataGrids repopulate once instead of
            // taking N CollectionChanged events on the dispatcher (5000-row searches
            // would otherwise flash "(Not Responding)" in the title while WPF
            // processed each insert + the cascading filter-count recount).
            var orderedFiles = result.Files.OrderBy(f => f.ModifiedUtc).ToList();
            var fileOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < orderedFiles.Count; i++) fileOrder[orderedFiles[i].FilePath] = i;
            var orderedLines = result.Lines
                .OrderBy(l => fileOrder.TryGetValue(l.FilePath, out var idx) ? idx : int.MaxValue)
                .ThenBy(l => l.LineNumber)
                .ToList();
            Files.ReplaceAll(orderedFiles);
            Lines.ReplaceAll(orderedLines);
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
