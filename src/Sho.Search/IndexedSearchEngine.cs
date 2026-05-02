using System.Diagnostics;
using System.Text.RegularExpressions;
using Lucene.Net.Analysis;
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
using Sho.Search.Analysis;
using LuceneFSDirectory = Lucene.Net.Store.FSDirectory;
using IODirectory = System.IO.Directory;

namespace Sho.Search;

public sealed class IndexedSearchEngine : IIndexedSearchEngine
{
    private const LuceneVersion LV = LuceneVersion.LUCENE_48;
    /// <summary>
    /// Marker stored in meta.json so we can detect indexes built with an older
    /// analyzer (e.g. StandardAnalyzer) and prompt the user to rebuild.
    /// Bump whenever the analyzer pipeline changes in a way that invalidates
    /// previously-indexed tokens.
    /// </summary>
    public const string AnalyzerVersion = "uk-morfologik-2";
    private readonly ITextExtractorRegistry _registry;
    private readonly string _indexRoot;

    private static Analyzer CreateAnalyzer() => new LowerUkrainianAnalyzer(LV);

    public IndexedSearchEngine(ITextExtractorRegistry? registry = null, string? indexRoot = null)
    {
        _registry = registry ?? new TextExtractorRegistry();
        _indexRoot = indexRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "sho", "indexes");
        IODirectory.CreateDirectory(_indexRoot);
    }

    public string Name => "Indexed (Lucene)";

    private string GetIndexDir(IReadOnlyList<string> rootFolders) =>
        Path.Combine(_indexRoot, PathHasher.Hash(rootFolders));

    public Task<bool> IsIndexBuiltAsync(IReadOnlyList<string> rootFolders, CancellationToken cancellationToken)
    {
        var dir = GetIndexDir(rootFolders);
        var built = IODirectory.Exists(dir) && IODirectory.EnumerateFiles(dir).Any();
        return Task.FromResult(built);
    }

    public Task DeleteIndexAsync(IReadOnlyList<string> rootFolders, CancellationToken cancellationToken)
    {
        var dir = GetIndexDir(rootFolders);
        if (IODirectory.Exists(dir)) IODirectory.Delete(dir, true);
        return Task.CompletedTask;
    }

    public async Task BuildIndexAsync(
        IReadOnlyList<string> rootFolders,
        IProgress<IndexProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var indexDir = GetIndexDir(rootFolders);
        IODirectory.CreateDirectory(indexDir);

        progress?.Report(new IndexProgress(0, 0, null, "Scanning folders", sw.ElapsedMilliseconds));
        var roots = BruteForceSearchEngine.DedupeRoots(rootFolders);
        var docs = roots.SelectMany(r => FileScanner.Enumerate(r, _registry.SupportedExtensions))
                         .GroupBy(d => d.Path, StringComparer.OrdinalIgnoreCase)
                         .Select(g => g.First())
                         .ToList();
        int total = docs.Count;
        progress?.Report(new IndexProgress(0, total, null, "Indexing", sw.ElapsedMilliseconds));

        using var directory = LuceneFSDirectory.Open(indexDir);
        using var analyzer = CreateAnalyzer();
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
        WriteMetadata(indexDir, new IndexMetadata(DateTime.UtcNow, done, roots, AnalyzerVersion));
        progress?.Report(new IndexProgress(total, total, null, "Indexed", sw.ElapsedMilliseconds));
    }

    public async Task<int> UpdateDocumentsAsync(
        IReadOnlyList<string> rootFolders,
        IReadOnlyList<string> filesUpserted,
        IReadOnlyList<string> filesDeleted,
        IProgress<IndexProgress>? progress,
        CancellationToken cancellationToken)
    {
        var indexDir = GetIndexDir(rootFolders);
        if (!IODirectory.Exists(indexDir) || !IODirectory.EnumerateFiles(indexDir).Any())
            return 0;

        // Dedupe, normalize, and resolve conflicts: a path that appears in both
        // sets is treated as upsert if the file currently exists, else delete.
        var upsertSet = new HashSet<string>(filesUpserted ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var deleteSet = new HashSet<string>(filesDeleted ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var conflict in upsertSet.Intersect(deleteSet, StringComparer.OrdinalIgnoreCase).ToList())
        {
            if (File.Exists(conflict)) deleteSet.Remove(conflict);
            else upsertSet.Remove(conflict);
        }

        if (upsertSet.Count == 0 && deleteSet.Count == 0) return 0;

        var sw = Stopwatch.StartNew();
        progress?.Report(new IndexProgress(0, upsertSet.Count, null, "Updating index", sw.ElapsedMilliseconds));

        using var directory = LuceneFSDirectory.Open(indexDir);
        using var analyzer = CreateAnalyzer();
        var cfg = new IndexWriterConfig(LV, analyzer)
        {
            OpenMode = OpenMode.APPEND
        };
        using var writer = new IndexWriter(directory, cfg);

        int changed = 0;

        foreach (var path in deleteSet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.DeleteDocuments(new Term("path", path));
            changed++;
        }

        int done = 0;
        foreach (var path in upsertSet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new IndexProgress(done, upsertSet.Count, path, "Updating index", sw.ElapsedMilliseconds));

            // File may have been deleted between event and processing — treat as delete.
            if (!File.Exists(path))
            {
                writer.DeleteDocuments(new Term("path", path));
                changed++;
                done++;
                continue;
            }

            var extractor = _registry.Resolve(path);
            if (extractor == null) { done++; continue; }

            string text;
            try { text = await extractor.ExtractAsync(path, cancellationToken).ConfigureAwait(false); }
            catch { done++; continue; }

            var info = new FileInfo(path);
            var doc = new Document
            {
                new StringField("path", path, Field.Store.YES),
                new StringField("ext", info.Extension, Field.Store.YES),
                new Int64Field("size", info.Length, Field.Store.YES),
                new Int64Field("mtime", info.LastWriteTimeUtc.Ticks, Field.Store.YES),
                new TextField("content", text ?? string.Empty, Field.Store.YES)
            };
            writer.UpdateDocument(new Term("path", path), doc);
            changed++;
            done++;
        }

        progress?.Report(new IndexProgress(done, upsertSet.Count, null, "Committing", sw.ElapsedMilliseconds));
        writer.Commit();

        // Refresh meta.json with current document count and timestamp.
        try
        {
            var roots = BruteForceSearchEngine.DedupeRoots(rootFolders);
            WriteMetadata(indexDir, new IndexMetadata(DateTime.UtcNow, writer.NumDocs, roots, AnalyzerVersion));
        }
        catch { }

        progress?.Report(new IndexProgress(done, upsertSet.Count, null, "Updated", sw.ElapsedMilliseconds));
        return changed;
    }

    public async Task<int> SyncIndexAsync(
        IReadOnlyList<string> rootFolders,
        IProgress<IndexProgress>? progress,
        CancellationToken cancellationToken)
    {
        var indexDir = GetIndexDir(rootFolders);
        if (!IODirectory.Exists(indexDir) || !IODirectory.EnumerateFiles(indexDir).Any())
            return 0;

        var sw = Stopwatch.StartNew();
        progress?.Report(new IndexProgress(0, 0, null, "Scanning folders", sw.ElapsedMilliseconds));

        // 1. Snapshot the disk: every supported file under all roots, deduped.
        var roots = BruteForceSearchEngine.DedupeRoots(rootFolders);
        var diskFiles = roots
            .SelectMany(r => FileScanner.Enumerate(r, _registry.SupportedExtensions))
            .GroupBy(d => d.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToDictionary(d => d.Path, d => d.ModifiedUtc.Ticks, StringComparer.OrdinalIgnoreCase);

        progress?.Report(new IndexProgress(0, 0, null, "Reading index", sw.ElapsedMilliseconds));

        // 2. Snapshot the index: each live doc's path + stored mtime.
        var indexedFiles = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using (var directory = LuceneFSDirectory.Open(indexDir))
        using (var reader = DirectoryReader.Open(directory))
        {
            var liveDocs = Lucene.Net.Index.MultiFields.GetLiveDocs(reader);
            for (int i = 0; i < reader.MaxDoc; i++)
            {
                if (liveDocs != null && !liveDocs.Get(i)) continue;
                var doc = reader.Document(i);
                string p = doc.Get("path");
                if (string.IsNullOrEmpty(p)) continue;
                long mtime = long.TryParse(doc.Get("mtime"), out var t) ? t : 0;
                indexedFiles[p] = mtime;
            }
        }

        // 3. Diff: new+changed → upsert, gone → delete.
        var upsert = new List<string>();
        var delete = new List<string>();
        foreach (var kv in diskFiles)
        {
            if (!indexedFiles.TryGetValue(kv.Key, out var idxMtime))
                upsert.Add(kv.Key);
            else if (kv.Value != idxMtime)
                upsert.Add(kv.Key);
        }
        foreach (var p in indexedFiles.Keys)
        {
            if (!diskFiles.ContainsKey(p))
                delete.Add(p);
        }

        if (upsert.Count == 0 && delete.Count == 0)
        {
            progress?.Report(new IndexProgress(0, 0, null, "Index up to date", sw.ElapsedMilliseconds));
            return 0;
        }

        return await UpdateDocumentsAsync(rootFolders, upsert, delete, progress, cancellationToken).ConfigureAwait(false);
    }

    private static void WriteMetadata(string indexDir, IndexMetadata meta)
    {
        try
        {
            var path = Path.Combine(indexDir, "meta.json");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(meta));
        }
        catch { }
    }

    public Task<IndexMetadata?> GetIndexMetadataAsync(IReadOnlyList<string> rootFolders, CancellationToken cancellationToken)
    {
        var path = Path.Combine(GetIndexDir(rootFolders), "meta.json");
        if (!File.Exists(path)) return Task.FromResult<IndexMetadata?>(null);
        try
        {
            var json = File.ReadAllText(path);
            return Task.FromResult(System.Text.Json.JsonSerializer.Deserialize<IndexMetadata>(json));
        }
        catch { return Task.FromResult<IndexMetadata?>(null); }
    }

    public async Task<SearchResult> SearchAsync(
        IReadOnlyList<string> rootFolders,
        SearchQuery query,
        IProgress<IndexProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var indexDir = GetIndexDir(rootFolders);
        if (!IODirectory.Exists(indexDir) || !IODirectory.EnumerateFiles(indexDir).Any())
        {
            return new SearchResult
            {
                Elapsed = sw.Elapsed,
                Error = "Index is not built. Run 'Build index' first."
            };
        }

        progress?.Report(new IndexProgress(0, 0, null, "Querying index", sw.ElapsedMilliseconds));

        // Run all heavy work on a worker thread. Lucene's Search() and our per-doc
        // tokenize+highlight loop are synchronous and CPU-bound — leaving them on
        // the captured (UI) context after Task.Yield blocks the dispatcher and
        // queues every progress callback until the search finishes, making the
        // status bar look frozen until results appear.
        return await Task.Run(() =>
        {
            using var directory = LuceneFSDirectory.Open(indexDir);
            using var reader = DirectoryReader.Open(directory);
            var searcher = new IndexSearcher(reader);

            var luceneQuery = BuildLuceneQuery(query);
            progress?.Report(new IndexProgress(0, 0, null, "Searching", sw.ElapsedMilliseconds));
            var topDocs = searcher.Search(luceneQuery, n: Math.Max(50, query.MaxLineHits));

            // Highlighting input: ONE entry per user query token (whitespace-split,
            // operator-stripped). MorphologicalLineMatcher AND-s these per line so
            // "Ніколаєнка Юрія" only emits hits on lines where both names appear,
            // not every line that has any "Юрій" anywhere in a 2000-row spreadsheet.
            var queryTokens = SplitQueryTokens(query.Text);
            if (queryTokens.Count == 0) queryTokens = new List<string> { query.Text };

            var fileSummaries = new List<FileSummary>();
            var hits = new List<SearchHit>();
            int totalHits = 0;
            int processedDocs = 0;
            int totalDocs = topDocs.ScoreDocs.Length;
            progress?.Report(new IndexProgress(0, totalDocs, null, "Highlighting matches", sw.ElapsedMilliseconds));

            foreach (var sd in topDocs.ScoreDocs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var doc = searcher.Doc(sd.Doc);
                string path = doc.Get("path");
                string ext = doc.Get("ext") ?? Path.GetExtension(path);
                long size = long.TryParse(doc.Get("size"), out var s) ? s : 0;
                DateTime mtime = long.TryParse(doc.Get("mtime"), out var t) ? new DateTime(t, DateTimeKind.Utc) : DateTime.MinValue;
                string content = doc.Get("content") ?? string.Empty;

                // Report every doc up to 32, then every 8th — visible motion in the
                // bar without flooding the dispatcher on huge result sets.
                if (totalDocs <= 32 || (processedDocs & 0x7) == 0)
                    progress?.Report(new IndexProgress(processedDocs, totalDocs, path, "Highlighting", sw.ElapsedMilliseconds));

                // Cap matches per file so a 5000-row spreadsheet can't monopolize the
                // panel — past 200 hits in one file the user can refine their query.
                const int MaxHitsPerFile = 200;
                int fileHits = 0;
                foreach (var hit in MorphologicalLineMatcher.FindHits(path, content, queryTokens, query, CreateAnalyzer))
                {
                    hits.Add(hit);
                    fileHits++;
                    totalHits++;
                    if (fileHits >= MaxHitsPerFile) break;
                    if (totalHits >= query.MaxLineHits) break;
                }
                if (fileHits > 0)
                    fileSummaries.Add(new FileSummary(path, fileHits, size, mtime, ext));
                processedDocs++;
                if (totalHits >= query.MaxLineHits) break;
            }

            progress?.Report(new IndexProgress(totalDocs, totalDocs, null, "Done", sw.ElapsedMilliseconds));

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
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static Query BuildLuceneQuery(SearchQuery q)
    {
        if (HasQuerySyntax(q.Text))
        {
            try
            {
                var analyzer = CreateAnalyzer();
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

        var rawTerms = q.Text
            .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 0)
            .ToList();

        if (rawTerms.Count == 0)
            return new BooleanQuery();

        if (rawTerms.Count == 1)
            return BuildSingleTermHybridQuery("content", rawTerms[0]);

        // Multi-bare: route through QueryParser with AND default operator —
        // identical to the user-confirmed working `+a +b` syntax. The parser
        // analyzes each term (lemmatize + lowercase) and emits a BooleanQuery
        // of MUST TermQueries; WildcardifySingleTerms then augments each
        // TermQuery with wildcard + fuzzy OR-siblings.
        try
        {
            using var analyzer = CreateAnalyzer();
            var parser = new QueryParser(LV, "content", analyzer)
            {
                AllowLeadingWildcard = true,
                DefaultOperator = QueryParserBase.AND_OPERATOR,
            };
            return WildcardifySingleTerms(parser.Parse(q.Text));
        }
        catch
        {
            // Parser may choke on stopwords / short tokens; fallback to
            // hand-built AND of per-token hybrids (older path).
            var multi = new BooleanQuery();
            foreach (var t in rawTerms)
                multi.Add(BuildSingleTermHybridQuery("content", t), Occur.MUST);
            return multi;
        }
    }

    /// <summary>
    /// Single-term query: match the analyzed lemma OR a substring wildcard OR
    /// a fuzzy term (edit-distance ≤ 1). The lemma branch handles dictionary
    /// inflections ("будинком" → indexed lemma "будинок"); the wildcard branch
    /// handles partial inputs ("Ніколаєн" → *ніколаєн* matches "ніколаєнко");
    /// the fuzzy branch handles typos and minor casing/diacritic differences.
    /// Edits=1 keeps proper-noun searches strict — distance("ніколаєнка",
    /// "ніколаєва") is 2, so a different surname won't sneak in.
    /// </summary>
    private static Query BuildSingleTermHybridQuery(string field, string raw)
    {
        var bq = new BooleanQuery();
        var lower = raw.ToLowerInvariant();
        foreach (var lemma in AnalyzeToTokens(raw))
            bq.Add(new TermQuery(new Term(field, lemma)), Occur.SHOULD);
        bq.Add(new WildcardQuery(new Term(field, $"*{lower}*")), Occur.SHOULD);
        if (lower.Length >= 4)
            bq.Add(new FuzzyQuery(new Term(field, lower), 1), Occur.SHOULD);
        return bq;
    }

    /// <summary>
    /// Split a user-typed query into per-token "groups" for line-level
    /// AND-matching in <see cref="MorphologicalLineMatcher"/>. Strips Lucene
    /// operator chars but preserves the user's intent ("один токен — один
    /// конструкт пошуку").
    /// </summary>
    private static List<string> SplitQueryTokens(string queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText)) return new List<string>();
        var separators = new[] { ' ', '\t', '\r', '\n' };
        var stripChars = new[] { '+', '-', '"', '(', ')', '*', '?', '~', '^' };
        var result = new List<string>();
        foreach (var raw in queryText.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = raw.Trim(stripChars);
            if (trimmed.Length == 0) continue;
            // Skip Lucene boolean keywords — they're connectors, not search tokens.
            if (trimmed.Equals("AND", StringComparison.Ordinal) ||
                trimmed.Equals("OR", StringComparison.Ordinal) ||
                trimmed.Equals("NOT", StringComparison.Ordinal)) continue;
            result.Add(trimmed);
        }
        return result;
    }

    public static List<string> AnalyzeToTokens(string text)
    {
        using var a = CreateAnalyzer();
        using var ts = a.GetTokenStream("content", new System.IO.StringReader(text));
        var charAttr = ts.GetAttribute<Lucene.Net.Analysis.TokenAttributes.ICharTermAttribute>();
        ts.Reset();
        var list = new List<string>();
        while (ts.IncrementToken())
        {
            var tok = charAttr.ToString();
            if (!string.IsNullOrEmpty(tok)) list.Add(tok);
        }
        ts.End();
        return list;
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
    /// Recursively augment TermQuery / single-term PhraseQuery with a substring-wildcard
    /// sibling, so that partial inputs the analyzer cannot lemmatize still match. The
    /// lemma side of the OR keeps the morphology benefit ("Ніколаєнка" → indexed
    /// "ніколаєнко"); the wildcard side covers prefix/middle queries
    /// ("Ніколаєн" → matches *ніколаєн* in indexed lemmas).
    /// </summary>
    internal static Query WildcardifySingleTerms(Query q)
    {
        switch (q)
        {
            case TermQuery tq:
                return TermPlusWildcard(tq.Term);

            case PhraseQuery pq:
                var terms = pq.GetTerms();
                if (terms.Length == 1)
                    return TermPlusWildcard(terms[0]);
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

    private static Query TermPlusWildcard(Term term)
    {
        var or = new BooleanQuery();
        or.Add(new TermQuery(term), Occur.SHOULD);
        or.Add(new WildcardQuery(new Term(term.Field, $"*{term.Text}*")), Occur.SHOULD);
        if (term.Text.Length >= 4)
            or.Add(new FuzzyQuery(term, 1), Occur.SHOULD);
        return or;
    }
}
