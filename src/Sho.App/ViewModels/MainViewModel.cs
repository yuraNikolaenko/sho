using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public MainViewModel()
    {
        _registry = new TextExtractorRegistry();
        _brute = new BruteForceSearchEngine(_registry);
        _indexed = new IndexedSearchEngine(_registry);
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
            Files.Clear();
            Lines.Clear();
            foreach (var f in result.Files) Files.Add(f);
            foreach (var l in result.Lines) Lines.Add(l);
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
        _cts = new CancellationTokenSource();
        SearchCommand.NotifyCanExecuteChanged();
        BuildIndexCommand.NotifyCanExecuteChanged();
        DeleteIndexCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();

        var progress = new Progress<IndexProgress>(p =>
        {
            ProgressFraction = p.Fraction;
            ProgressStage = p.Stage;
            if (!string.IsNullOrEmpty(p.CurrentFile))
                StatusText = $"{p.Stage}: {Path.GetFileName(p.CurrentFile)} ({p.FilesProcessed}/{p.FilesTotal})";
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
            IsBusy = false;
            ProgressFraction = 0;
            _cts?.Dispose();
            _cts = null;
            SearchCommand.NotifyCanExecuteChanged();
            BuildIndexCommand.NotifyCanExecuteChanged();
            DeleteIndexCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
        }
    }
}
