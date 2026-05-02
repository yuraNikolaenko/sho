using Sho.Core.Models;

namespace Sho.Core.Abstractions;

public interface ISearchEngine
{
    string Name { get; }

    Task<SearchResult> SearchAsync(
        IReadOnlyList<string> rootFolders,
        SearchQuery query,
        IProgress<IndexProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IIndexedSearchEngine : ISearchEngine
{
    Task BuildIndexAsync(
        IReadOnlyList<string> rootFolders,
        IProgress<IndexProgress>? progress,
        CancellationToken cancellationToken);

    Task<bool> IsIndexBuiltAsync(IReadOnlyList<string> rootFolders, CancellationToken cancellationToken);

    Task DeleteIndexAsync(IReadOnlyList<string> rootFolders, CancellationToken cancellationToken);

    Task<IndexMetadata?> GetIndexMetadataAsync(IReadOnlyList<string> rootFolders, CancellationToken cancellationToken);

    /// <summary>
    /// Apply incremental changes to an existing index without rebuilding from scratch.
    /// Files in <paramref name="filesUpserted"/> are re-extracted and replace existing docs (UpdateDocument by path key);
    /// files in <paramref name="filesDeleted"/> are removed (DeleteDocuments by path key).
    /// Returns the number of documents actually written/removed.
    /// No-op if the index is not yet built for the given roots.
    /// </summary>
    Task<int> UpdateDocumentsAsync(
        IReadOnlyList<string> rootFolders,
        IReadOnlyList<string> filesUpserted,
        IReadOnlyList<string> filesDeleted,
        IProgress<IndexProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reconcile the index with the current filesystem state of <paramref name="rootFolders"/>:
    /// new files are added, removed files are dropped, and files whose modified-time changed
    /// are re-extracted. Cheap in the steady state. Use this when the FileSystemWatcher was
    /// off or might have missed events (app restart, bulk filesystem ops, network drives).
    /// Returns the number of documents touched. No-op if the index is not yet built.
    /// </summary>
    Task<int> SyncIndexAsync(
        IReadOnlyList<string> rootFolders,
        IProgress<IndexProgress>? progress,
        CancellationToken cancellationToken);
}
