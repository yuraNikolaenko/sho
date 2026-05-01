namespace Sho.Core.Models;

public sealed class SearchResult
{
    public IReadOnlyList<FileSummary> Files { get; init; } = Array.Empty<FileSummary>();
    public IReadOnlyList<SearchHit> Lines { get; init; } = Array.Empty<SearchHit>();
    public int TotalFilesScanned { get; init; }
    public int FilesMatched { get; init; }
    public int TotalHits { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string? Error { get; init; }
}
