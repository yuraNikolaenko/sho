using System.IO;
using Sho.Core.Abstractions;
using Sho.Core.Models;

namespace Sho.App.Services;

/// <summary>
/// Watches the active root folders and applies incremental updates to the
/// indexed search engine when supported files change. Events are debounced
/// (~1.5s after the last change) and serialized so that one update batch
/// runs at a time. UI is informed via <see cref="Updated"/>.
/// </summary>
public sealed class IndexWatcherService : IDisposable
{
    private readonly IIndexedSearchEngine _engine;
    private readonly HashSet<string> _supportedExt;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly HashSet<string> _pendingUpsert = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingDelete = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly System.Threading.SemaphoreSlim _runLock = new(1, 1);
    private readonly System.Timers.Timer _debounce;
    private IReadOnlyList<string> _roots = Array.Empty<string>();
    private bool _disposed;

    public TimeSpan DebounceInterval
    {
        get => TimeSpan.FromMilliseconds(_debounce.Interval);
        set => _debounce.Interval = Math.Max(100, value.TotalMilliseconds);
    }

    public bool IsRunning => _watchers.Count > 0;

    /// <summary>
    /// Raised after each successful incremental update batch, on a background thread.
    /// </summary>
    public event EventHandler<IndexUpdateEventArgs>? Updated;

    /// <summary>
    /// Raised when an update batch fails. Args carry the exception. UI should not block on it.
    /// </summary>
    public event EventHandler<Exception>? UpdateFailed;

    public IndexWatcherService(IIndexedSearchEngine engine, IReadOnlyCollection<string> supportedExtensions)
    {
        _engine = engine;
        _supportedExt = new HashSet<string>(supportedExtensions, StringComparer.OrdinalIgnoreCase);
        _debounce = new System.Timers.Timer(1500) { AutoReset = false };
        _debounce.Elapsed += async (_, _) => await FlushAsync().ConfigureAwait(false);
    }

    public void Start(IReadOnlyList<string> roots)
    {
        Stop();
        if (roots == null || roots.Count == 0) return;
        _roots = roots.ToList();

        foreach (var r in _roots)
        {
            if (string.IsNullOrWhiteSpace(r) || !Directory.Exists(r)) continue;
            try
            {
                var w = new FileSystemWatcher(r)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName
                                 | NotifyFilters.LastWrite
                                 | NotifyFilters.Size
                                 | NotifyFilters.DirectoryName,
                    InternalBufferSize = 64 * 1024,
                };
                w.Created += OnCreatedOrChanged;
                w.Changed += OnCreatedOrChanged;
                w.Deleted += OnDeleted;
                w.Renamed += OnRenamed;
                w.Error += OnWatcherError;
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch
            {
                // Permission denied / invalid path — skip silently; other roots still work.
            }
        }
    }

    public void Stop()
    {
        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
        }
        _watchers.Clear();
        lock (_gate)
        {
            _pendingUpsert.Clear();
            _pendingDelete.Clear();
        }
        _debounce.Stop();
    }

    private bool IsSupported(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return false;
        return _supportedExt.Contains(ext);
    }

    private void OnCreatedOrChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsSupported(e.FullPath)) return;
        // FileSystemWatcher fires Changed for directories too; skip those.
        try { if (Directory.Exists(e.FullPath)) return; } catch { return; }
        lock (_gate)
        {
            _pendingDelete.Remove(e.FullPath);
            _pendingUpsert.Add(e.FullPath);
        }
        _debounce.Stop(); _debounce.Start();
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        if (!IsSupported(e.FullPath)) return;
        lock (_gate)
        {
            _pendingUpsert.Remove(e.FullPath);
            _pendingDelete.Add(e.FullPath);
        }
        _debounce.Stop(); _debounce.Start();
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        bool oldSupp = IsSupported(e.OldFullPath);
        bool newSupp = IsSupported(e.FullPath);
        if (!oldSupp && !newSupp) return;
        lock (_gate)
        {
            if (oldSupp)
            {
                _pendingUpsert.Remove(e.OldFullPath);
                _pendingDelete.Add(e.OldFullPath);
            }
            if (newSupp)
            {
                _pendingDelete.Remove(e.FullPath);
                _pendingUpsert.Add(e.FullPath);
            }
        }
        _debounce.Stop(); _debounce.Start();
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        UpdateFailed?.Invoke(this, e.GetException());
    }

    private async Task FlushAsync()
    {
        if (_disposed) return;
        if (!await _runLock.WaitAsync(0).ConfigureAwait(false))
        {
            // Another flush in progress; reschedule.
            _debounce.Stop(); _debounce.Start();
            return;
        }
        try
        {
            string[] up;
            string[] del;
            lock (_gate)
            {
                if (_pendingUpsert.Count == 0 && _pendingDelete.Count == 0) return;
                up = _pendingUpsert.ToArray();
                del = _pendingDelete.ToArray();
                _pendingUpsert.Clear();
                _pendingDelete.Clear();
            }
            try
            {
                int changed = await _engine.UpdateDocumentsAsync(_roots, up, del, null, CancellationToken.None).ConfigureAwait(false);
                Updated?.Invoke(this, new IndexUpdateEventArgs(changed, up.Length, del.Length));
            }
            catch (Exception ex)
            {
                UpdateFailed?.Invoke(this, ex);
            }
        }
        finally
        {
            _runLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _debounce.Dispose();
        _runLock.Dispose();
    }
}

public sealed class IndexUpdateEventArgs : EventArgs
{
    public int Changed { get; }
    public int Upserted { get; }
    public int Deleted { get; }
    public DateTime UtcTime { get; } = DateTime.UtcNow;
    public IndexUpdateEventArgs(int changed, int upserted, int deleted)
    {
        Changed = changed;
        Upserted = upserted;
        Deleted = deleted;
    }
}
