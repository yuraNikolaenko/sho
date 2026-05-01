using Sho.Core.Models;

namespace Sho.Core.Abstractions;

public interface ISearchEngine
{
    string Name { get; }

    Task<SearchResult> SearchAsync(
        string rootFolder,
        SearchQuery query,
        IProgress<IndexProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IIndexedSearchEngine : ISearchEngine
{
    Task BuildIndexAsync(
        string rootFolder,
        IProgress<IndexProgress>? progress,
        CancellationToken cancellationToken);

    Task<bool> IsIndexBuiltAsync(string rootFolder, CancellationToken cancellationToken);

    Task DeleteIndexAsync(string rootFolder, CancellationToken cancellationToken);
}
