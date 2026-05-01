using System.Diagnostics;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;
using Sho.Core.Abstractions;
using Sho.Core.IO;
using Sho.Core.Models;
using Sho.Core.Text;
using Sho.Extractors;
using LuceneFSDirectory = Lucene.Net.Store.FSDirectory;
using IODirectory = System.IO.Directory;

namespace Sho.Search;

public sealed class IndexedSearchEngine : IIndexedSearchEngine
{
    private const LuceneVersion LV = LuceneVersion.LUCENE_48;
    private readonly ITextExtractorRegistry _registry;
    private readonly string _indexRoot;

    public IndexedSearchEngine(ITextExtractorRegistry? registry = null, string? indexRoot = null)
    {
        _registry = registry ?? new TextExtractorRegistry();
        _indexRoot = indexRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "sho", "indexes");
        IODirectory.CreateDirectory(_indexRoot);
    }

    public string Name => "Indexed (Lucene)";

    private string GetIndexDir(string rootFolder) =>
        Path.Combine(_indexRoot, PathHasher.Hash(rootFolder));

    public Task<bool> IsIndexBuiltAsync(string rootFolder, CancellationToken cancellationToken)
    {
        var dir = GetIndexDir(rootFolder);
        var built = IODirectory.Exists(dir) && IODirectory.EnumerateFiles(dir).Any();
        return Task.FromResult(built);
    }

    public Task DeleteIndexAsync(string rootFolder, CancellationToken cancellationToken)
    {
        var dir = GetIndexDir(rootFolder);
        if (IODirectory.Exists(dir)) IODirectory.Delete(dir, true);
        return Task.CompletedTask;
    }

    public async Task BuildIndexAsync(
        string rootFolder,
        IProgress<IndexProgress>? progress,
        CancellationToken cancellationToken)
    {
        var indexDir = GetIndexDir(rootFolder);
        IODirectory.CreateDirectory(indexDir);

        var docs = FileScanner.Enumerate(rootFolder, _registry.SupportedExtensions).ToList();
        int total = docs.Count;
        progress?.Report(new IndexProgress(0, total, null, "Indexing"));

        using var directory = LuceneFSDirectory.Open(indexDir);
        using var analyzer = new StandardAnalyzer(LV);
        var cfg = new IndexWriterConfig(LV, analyzer)
        {
            OpenMode = OpenMode.CREATE
        };
        using var writer = new IndexWriter(directory, cfg);

        int done = 0;
        foreach (var d in docs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extractor = _registry.Resolve(d.Path);
            if (extractor == null) { done++; continue; }

            string text;
            try { text = await extractor.ExtractAsync(d.Path, cancellationToken).ConfigureAwait(false); }
            catch { done++; continue; }

            var doc = new Document
            {
                new StringField("path", d.Path, Field.Store.YES),
                new StringField("ext", d.Extension, Field.Store.YES),
                new Int64Field("size", d.SizeBytes, Field.Store.YES),
                new Int64Field("mtime", d.ModifiedUtc.Ticks, Field.Store.YES),
                new TextField("content", text ?? string.Empty, Field.Store.YES)
            };
            writer.AddDocument(doc);

            done++;
            if ((done & 15) == 0)
                progress?.Report(new IndexProgress(done, total, d.Path, "Indexing"));
        }

        writer.Commit();
        progress?.Report(new IndexProgress(total, total, null, "Indexed"));
    }

    public async Task<SearchResult> SearchAsync(
        string rootFolder,
        SearchQuery query,
        IProgress<IndexProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var indexDir = GetIndexDir(rootFolder);
        if (!IODirectory.Exists(indexDir) || !IODirectory.EnumerateFiles(indexDir).Any())
        {
            return new SearchResult
            {
                Elapsed = sw.Elapsed,
                Error = "Index is not built. Run 'Build index' first."
            };
        }

        progress?.Report(new IndexProgress(0, 0, null, "Searching"));

        await Task.Yield();
        using var directory = LuceneFSDirectory.Open(indexDir);
        using var reader = DirectoryReader.Open(directory);
        var searcher = new IndexSearcher(reader);

        var luceneQuery = BuildLuceneQuery(query);
        var topDocs = searcher.Search(luceneQuery, n: Math.Max(50, query.MaxLineHits));

        var fileSummaries = new List<FileSummary>();
        var hits = new List<SearchHit>();
        int totalHits = 0;

        foreach (var sd in topDocs.ScoreDocs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var doc = searcher.Doc(sd.Doc);
            string path = doc.Get("path");
            string ext = doc.Get("ext") ?? Path.GetExtension(path);
            long size = long.TryParse(doc.Get("size"), out var s) ? s : 0;
            DateTime mtime = long.TryParse(doc.Get("mtime"), out var t) ? new DateTime(t, DateTimeKind.Utc) : DateTime.MinValue;
            string content = doc.Get("content") ?? string.Empty;

            int fileHits = 0;
            foreach (var hit in LineMatcher.FindHits(path, content, query))
            {
                hits.Add(hit);
                fileHits++;
                totalHits++;
                if (totalHits >= query.MaxLineHits) break;
            }
            if (fileHits > 0)
                fileSummaries.Add(new FileSummary(path, fileHits, size, mtime, ext));
            if (totalHits >= query.MaxLineHits) break;
        }

        progress?.Report(new IndexProgress(1, 1, null, "Done"));

        return new SearchResult
        {
            Files = fileSummaries
                .OrderByDescending(f => f.HitCount)
                .ThenBy(f => f.FilePath)
                .ToList(),
            Lines = hits,
            TotalFilesScanned = topDocs.TotalHits,
            FilesMatched = fileSummaries.Count,
            TotalHits = totalHits,
            Elapsed = sw.Elapsed
        };
    }

    private static Query BuildLuceneQuery(SearchQuery q)
    {
        var terms = q.Text
            .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => q.CaseSensitive ? t : t.ToLowerInvariant())
            .Where(t => t.Length > 0)
            .ToList();

        if (terms.Count == 0)
            return new BooleanQuery();

        if (terms.Count == 1)
        {
            string term = terms[0];
            return new WildcardQuery(new Term("content", $"*{term}*"));
        }

        var phrase = new PhraseQuery { Slop = 0 };
        foreach (var t in terms) phrase.Add(new Term("content", t));
        return phrase;
    }
}
