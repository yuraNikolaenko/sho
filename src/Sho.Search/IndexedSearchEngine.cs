using System.Diagnostics;
using System.Text.RegularExpressions;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
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
        var sw = Stopwatch.StartNew();
        var indexDir = GetIndexDir(rootFolder);
        IODirectory.CreateDirectory(indexDir);

        progress?.Report(new IndexProgress(0, 0, null, "Scanning folder", sw.ElapsedMilliseconds));
        var docs = FileScanner.Enumerate(rootFolder, _registry.SupportedExtensions).ToList();
        int total = docs.Count;
        progress?.Report(new IndexProgress(0, total, null, "Indexing", sw.ElapsedMilliseconds));

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
            progress?.Report(new IndexProgress(done, total, d.Path, "Indexing", sw.ElapsedMilliseconds));

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
        }

        progress?.Report(new IndexProgress(done, total, null, "Committing index", sw.ElapsedMilliseconds));
        writer.Commit();
        progress?.Report(new IndexProgress(total, total, null, "Indexed", sw.ElapsedMilliseconds));
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

        var matcherTerms = ExtractMatcherTerms(luceneQuery).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (matcherTerms.Count == 0) matcherTerms.Add(query.Text);

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
            foreach (var term in matcherTerms)
            {
                var subQuery = new SearchQuery(term, query.CaseSensitive, query.WholeWord, query.ContextChars, query.MaxLineHits);
                foreach (var hit in LineMatcher.FindHits(path, content, subQuery))
                {
                    hits.Add(hit);
                    fileHits++;
                    totalHits++;
                    if (totalHits >= query.MaxLineHits) break;
                }
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

    internal static Query BuildLuceneQuery(SearchQuery q)
    {
        if (HasQuerySyntax(q.Text))
        {
            try
            {
                var analyzer = new StandardAnalyzer(LV);
                var parser = new QueryParser(LV, "content", analyzer)
                {
                    AllowLeadingWildcard = true,
                    DefaultOperator = QueryParserBase.AND_OPERATOR,
                };
                return WildcardifySingleTerms(parser.Parse(q.Text));
            }
            catch
            {
                // fall through to literal
            }
        }

        var terms = q.Text
            .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => q.CaseSensitive ? t : t.ToLowerInvariant())
            .Where(t => t.Length > 0)
            .ToList();

        if (terms.Count == 0)
            return new BooleanQuery();

        if (terms.Count == 1)
            return new WildcardQuery(new Term("content", $"*{terms[0]}*"));

        var phrase = new PhraseQuery { Slop = 0 };
        foreach (var t in terms) phrase.Add(new Term("content", t));
        return phrase;
    }

    private static readonly Regex BoolKeywords = new(@"\b(AND|OR|NOT)\b", RegexOptions.Compiled);
    private static readonly char[] OperatorChars = { '+', '-', '"', '(', ')', '*', '?', '~', '^', ':', '\\', '[', ']', '{', '}' };

    internal static bool HasQuerySyntax(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (s.IndexOfAny(OperatorChars) >= 0) return true;
        if (BoolKeywords.IsMatch(s)) return true;
        return false;
    }

    /// <summary>
    /// Extract the literal substrings to highlight in the document text.
    /// LineMatcher does substring matching, so wildcards/quotes/operators must be stripped.
    /// </summary>
    internal static IEnumerable<string> ExtractMatcherTerms(Query q)
    {
        switch (q)
        {
            case TermQuery tq:
                if (!string.IsNullOrEmpty(tq.Term.Text)) yield return tq.Term.Text;
                break;
            case WildcardQuery wq:
                var t = wq.Term.Text.Trim('*', '?');
                if (!string.IsNullOrEmpty(t)) yield return t;
                break;
            case PrefixQuery prq:
                if (!string.IsNullOrEmpty(prq.Prefix.Text)) yield return prq.Prefix.Text;
                break;
            case FuzzyQuery fq:
                if (!string.IsNullOrEmpty(fq.Term.Text)) yield return fq.Term.Text;
                break;
            case PhraseQuery pq:
                var terms = pq.GetTerms();
                if (terms.Length == 1)
                {
                    if (!string.IsNullOrEmpty(terms[0].Text)) yield return terms[0].Text;
                }
                else
                {
                    var phrase = string.Join(" ", terms.Select(x => x.Text).Where(x => !string.IsNullOrEmpty(x)));
                    if (!string.IsNullOrEmpty(phrase)) yield return phrase;
                }
                break;
            case BooleanQuery bq:
                foreach (var c in bq.Clauses)
                {
                    if (c.Occur == Occur.MUST_NOT) continue;
                    foreach (var sub in ExtractMatcherTerms(c.Query)) yield return sub;
                }
                break;
        }
    }

    /// <summary>
    /// Recursively replace TermQuery and single-term PhraseQuery with WildcardQuery(*term*).
    /// Multi-term PhraseQuery, FuzzyQuery, WildcardQuery, etc. are kept as-is so explicit
    /// user intent is preserved. Fixes UX where typing "single-word" returns no hits because
    /// the indexed token is the full inflected form (e.g., search "Ніколаєнк" should match
    /// indexed "Ніколаєнка" / "Ніколаєнку" / etc.).
    /// </summary>
    internal static Query WildcardifySingleTerms(Query q)
    {
        switch (q)
        {
            case TermQuery tq:
                return new WildcardQuery(new Term(tq.Term.Field, $"*{tq.Term.Text}*"));

            case PhraseQuery pq:
                var terms = pq.GetTerms();
                if (terms.Length == 1)
                    return new WildcardQuery(new Term(terms[0].Field, $"*{terms[0].Text}*"));
                return pq;

            case BooleanQuery bq:
                var nb = new BooleanQuery();
                foreach (var c in bq.Clauses)
                    nb.Add(WildcardifySingleTerms(c.Query), c.Occur);
                return nb;

            default:
                return q;
        }
    }
}
