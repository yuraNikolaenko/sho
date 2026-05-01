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
}
