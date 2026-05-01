using System.Collections.Concurrent;
using System.Diagnostics;
using Sho.Core.Abstractions;
using Sho.Core.Models;
using Sho.Core.Text;
using Sho.Extractors;

namespace Sho.Search;

public sealed class BruteForceSearchEngine : ISearchEngine
{
    private readonly ITextExtractorRegistry _registry;

    public BruteForceSearchEngine(ITextExtractorRegistry? registry = null)
    {
        _registry = registry ?? new TextExtractorRegistry();
    }

    public string Name => "Brute-force";

    public async Task<SearchResult> SearchAsync(
        string rootFolder,
        SearchQuery query,
        IProgress<IndexProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var docs = FileScanner.Enumerate(rootFolder, _registry.SupportedExtensions).ToList();
        int total = docs.Count;
        int done = 0;
        progress?.Report(new IndexProgress(0, total, null, "Scanning"));

        var fileMap = new ConcurrentDictionary<string, int>();
        var hits = new ConcurrentBag<SearchHit>();
        int totalHits = 0;
        int hitCap = query.MaxLineHits;

        var parOpts = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount)
        };

        try
        {
            await Parallel.ForEachAsync(docs, parOpts, async (doc, ct) =>
            {
                if (Volatile.Read(ref totalHits) >= hitCap) return;
                var extractor = _registry.Resolve(doc.Path);
                if (extractor == null) { Interlocked.Increment(ref done); return; }

                string text;
                try { text = await extractor.ExtractAsync(doc.Path, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch { Interlocked.Increment(ref done); return; }

                int fileHits = 0;
                foreach (var hit in LineMatcher.FindHits(doc.Path, text, query))
                {
                    hits.Add(hit);
                    fileHits++;
                    int now = Interlocked.Increment(ref totalHits);
                    if (now >= hitCap) break;
                }
                if (fileHits > 0) fileMap[doc.Path] = fileHits;

                int processed = Interlocked.Increment(ref done);
                if ((processed & 31) == 0)
                    progress?.Report(new IndexProgress(processed, total, doc.Path, "Scanning"));
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new SearchResult
            {
                Files = Array.Empty<FileSummary>(),
                Lines = Array.Empty<SearchHit>(),
                TotalFilesScanned = done,
                FilesMatched = fileMap.Count,
                TotalHits = totalHits,
                Elapsed = sw.Elapsed,
                Error = "Cancelled"
            };
        }

        progress?.Report(new IndexProgress(total, total, null, "Done"));

        var docByPath = docs.ToDictionary(d => d.Path, StringComparer.OrdinalIgnoreCase);
        var summaries = fileMap
            .Select(kv =>
            {
                docByPath.TryGetValue(kv.Key, out var d);
                return new FileSummary(
                    kv.Key,
                    kv.Value,
                    d?.SizeBytes ?? 0,
                    d?.ModifiedUtc ?? DateTime.MinValue,
                    d?.Extension ?? Path.GetExtension(kv.Key));
            })
            .OrderByDescending(s => s.HitCount)
            .ThenBy(s => s.FilePath)
            .ToList();

        var lines = hits
            .OrderBy(h => h.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.LineNumber)
            .ToList();

        return new SearchResult
        {
            Files = summaries,
            Lines = lines,
            TotalFilesScanned = total,
            FilesMatched = summaries.Count,
            TotalHits = totalHits,
            Elapsed = sw.Elapsed
        };
    }
}
