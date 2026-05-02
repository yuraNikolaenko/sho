using Lucene.Net.Analysis;
using Lucene.Net.Analysis.TokenAttributes;
using Sho.Core.Models;

namespace Sho.Search;

/// <summary>
/// Lemma-aware variant of <see cref="Sho.Core.Text.LineMatcher"/>. Tokenizes the
/// document text with the same analyzer that built the index, and emits hits
/// only on lines where EVERY query token has at least one match — so a
/// multi-token query like "Ніколаєнка Юрія" doesn't light up every "Юрій"
/// row in a 2000-row spreadsheet just because the surname appears once
/// elsewhere.
///
/// Per-token matching is still flexible: a query token matches a doc token
/// when (1) the analyzed lemmas are equal (covers UA-dict morphology), or
/// (2) the query token is a substring of the doc surface form (covers
/// partial inputs), or (3) Levenshtein distance ≤ 2 with both ≥ 4 chars
/// (covers proper nouns the UA dict doesn't lemmatize, e.g. surnames).
/// </summary>
internal static class MorphologicalLineMatcher
{
    // Strict edit-distance: "ніколаєнка" → "ніколаєнку" is 1 edit (legitimate
    // inflection, but UA dict already covers it via lemma); "ніколаєнка" →
    // "ніколаєва" is 2 edits (different surname, must NOT match).
    private const int FuzzyMaxEdits = 1;
    private const int FuzzyMinLen = 4;

    /// <summary>
    /// One per query token (whitespace-separated, operator-stripped). Each group
    /// is a separate AND-constraint: a line must have ≥ 1 doc-token matching
    /// every group to be considered a hit.
    /// </summary>
    private sealed class TokenGroup
    {
        public string Raw;          // user's raw token, lowercased
        public HashSet<string> Lemmas; // analyzed lemma forms (case-insensitive)
        public TokenGroup(string raw, HashSet<string> lemmas) { Raw = raw; Lemmas = lemmas; }
    }

    public static IEnumerable<SearchHit> FindHits(
        string filePath,
        string text,
        IReadOnlyList<string> queryTokens,
        SearchQuery query,
        Func<Analyzer> analyzerFactory)
    {
        if (string.IsNullOrEmpty(text) || queryTokens.Count == 0) yield break;

        // Build a TokenGroup per user query token. AnalyzeToTokens runs through
        // the same analyzer pipeline as the index, so the lemma set matches what
        // we'd find indexed.
        var groups = new List<TokenGroup>(queryTokens.Count);
        foreach (var qt in queryTokens)
        {
            if (string.IsNullOrWhiteSpace(qt)) continue;
            var raw = qt.ToLowerInvariant();
            var lemmas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var l in IndexedSearchEngine.AnalyzeToTokens(qt))
                    if (!string.IsNullOrEmpty(l)) lemmas.Add(l);
            }
            catch { /* analyzer hiccup — fall back to raw */ }
            if (lemmas.Count == 0) lemmas.Add(raw);
            groups.Add(new TokenGroup(raw, lemmas));
        }
        if (groups.Count == 0) yield break;
        int groupCount = groups.Count;

        // Pre-compute line starts so we can group token offsets by line.
        var lineStarts = new List<int>(text.Length / 40 + 1) { 0 };
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') lineStarts.Add(i + 1);

        using var analyzer = analyzerFactory();
        using var ts = analyzer.GetTokenStream("content", new StringReader(text));
        var charAttr = ts.GetAttribute<ICharTermAttribute>();
        var offsetAttr = ts.GetAttribute<IOffsetAttribute>();
        ts.Reset();

        // Buffer pending hits for the current line; flush when the line changes
        // (only if every group fired). StandardTokenizer emits offsets in
        // monotonic order, so once we see a token from a later line we know the
        // current line is complete.
        int currentLine = -1;
        var pendingHits = new List<(int Start, int End)>();
        var groupsHit = new bool[groupCount];
        int groupsFired = 0;
        int totalEmitted = 0;

        while (ts.IncrementToken())
        {
            int start = offsetAttr.StartOffset;
            int end = offsetAttr.EndOffset;
            int len = end - start;
            if (start < 0 || end > text.Length || end <= start) continue;

            int lineIdx = LineIndexFor(lineStarts, start);
            if (lineIdx != currentLine)
            {
                // Flush completed line
                if (currentLine >= 0 && groupsFired == groupCount)
                {
                    foreach (var ph in pendingHits)
                    {
                        yield return BuildHit(filePath, text, lineStarts, currentLine, ph.Start, ph.End, query.ContextChars);
                        totalEmitted++;
                        if (totalEmitted >= query.MaxLineHits) yield break;
                    }
                }
                pendingHits.Clear();
                Array.Clear(groupsHit, 0, groupsHit.Length);
                groupsFired = 0;
                currentLine = lineIdx;
            }

            string lemma = charAttr.ToString();
            string? rawTokenLowerCached = null;
            int matchedGroup = -1;
            for (int g = 0; g < groupCount; g++)
            {
                var grp = groups[g];
                if (grp.Lemmas.Contains(lemma)) { matchedGroup = g; break; }

                if (rawTokenLowerCached == null) rawTokenLowerCached = text.Substring(start, len).ToLowerInvariant();
                if (grp.Raw.Length > 0 && rawTokenLowerCached.Contains(grp.Raw, StringComparison.Ordinal))
                { matchedGroup = g; break; }

                if (len >= FuzzyMinLen && grp.Raw.Length >= FuzzyMinLen
                    && LevenshteinAtMost(rawTokenLowerCached, grp.Raw, FuzzyMaxEdits))
                { matchedGroup = g; break; }
            }

            if (matchedGroup < 0) continue;

            pendingHits.Add((start, end));
            if (!groupsHit[matchedGroup])
            {
                groupsHit[matchedGroup] = true;
                groupsFired++;
            }
        }
        ts.End();

        // Flush trailing line
        if (currentLine >= 0 && groupsFired == groupCount)
        {
            foreach (var ph in pendingHits)
            {
                yield return BuildHit(filePath, text, lineStarts, currentLine, ph.Start, ph.End, query.ContextChars);
                totalEmitted++;
                if (totalEmitted >= query.MaxLineHits) yield break;
            }
        }
    }

    private static SearchHit BuildHit(string filePath, string text, List<int> lineStarts,
        int lineIdx, int start, int end, int contextChars)
    {
        int lineNumber = lineIdx + 1;
        int lineStart = lineStarts[lineIdx];
        int lineEndExclusive = lineIdx + 1 < lineStarts.Count
            ? lineStarts[lineIdx + 1] - 1
            : text.Length;
        if (lineEndExclusive > lineStart && text[lineEndExclusive - 1] == '\r') lineEndExclusive--;

        int matchInLine = start - lineStart;
        int matchLen = end - start;
        string lineText = text.Substring(lineStart, lineEndExclusive - lineStart);
        string snippet = BuildSnippet(text, start, matchLen, contextChars);

        return new SearchHit(
            FilePath: filePath,
            LineNumber: lineNumber,
            LineText: lineText,
            MatchStart: matchInLine,
            MatchLength: matchLen,
            Snippet: snippet);
    }

    /// <summary>
    /// True when the Levenshtein distance between <paramref name="a"/> and
    /// <paramref name="b"/> is ≤ <paramref name="maxEdits"/>. Two-row DP with
    /// length-diff prefilter and per-row early exit so it stays cheap for the
    /// typical case where most tokens are far from any query term.
    /// </summary>
    private static bool LevenshteinAtMost(string a, string b, int maxEdits)
    {
        int la = a.Length, lb = b.Length;
        if (Math.Abs(la - lb) > maxEdits) return false;
        if (la == 0) return lb <= maxEdits;
        if (lb == 0) return la <= maxEdits;

        int[] prev = new int[lb + 1];
        int[] curr = new int[lb + 1];
        for (int j = 0; j <= lb; j++) prev[j] = j;

        for (int i = 1; i <= la; i++)
        {
            curr[0] = i;
            int rowMin = curr[0];
            char ca = a[i - 1];
            for (int j = 1; j <= lb; j++)
            {
                int cost = ca == b[j - 1] ? 0 : 1;
                int v = curr[j - 1] + 1;
                int u = prev[j] + 1;
                int d = prev[j - 1] + cost;
                if (u < v) v = u;
                if (d < v) v = d;
                curr[j] = v;
                if (v < rowMin) rowMin = v;
            }
            if (rowMin > maxEdits) return false;
            (prev, curr) = (curr, prev);
        }
        return prev[lb] <= maxEdits;
    }

    private static int LineIndexFor(List<int> lineStarts, int offset)
    {
        int lo = 0, hi = lineStarts.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (lineStarts[mid] <= offset) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    private static string BuildSnippet(string text, int matchAbs, int matchLen, int ctx)
    {
        int from = Math.Max(0, matchAbs - ctx);
        int to = Math.Min(text.Length, matchAbs + matchLen + ctx);
        var chars = new char[to - from];
        for (int j = 0; j < chars.Length; j++)
        {
            char c = text[from + j];
            chars[j] = c == '\r' || c == '\n' || c == '\t' ? ' ' : c;
        }
        return new string(chars);
    }
}
