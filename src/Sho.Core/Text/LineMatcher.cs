using System.Globalization;
using Sho.Core.Models;

namespace Sho.Core.Text;

public static class LineMatcher
{
    public static IEnumerable<SearchHit> FindHits(string filePath, string text, SearchQuery query)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query.Text))
            yield break;

        var comparison = query.CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        int lineNumber = 0;
        int lineStart = 0;
        int returned = 0;
        int n = text.Length;

        for (int i = 0; i <= n; i++)
        {
            bool eol = i == n || text[i] == '\n';
            if (!eol) continue;

            int lineEndExclusive = i;
            if (lineEndExclusive > lineStart && text[lineEndExclusive - 1] == '\r')
                lineEndExclusive--;

            lineNumber++;
            int len = lineEndExclusive - lineStart;
            if (len > 0)
            {
                int searchFrom = lineStart;
                int lineEnd = lineEndExclusive;
                while (searchFrom < lineEnd)
                {
                    int idx = text.IndexOf(query.Text, searchFrom, lineEnd - searchFrom, comparison);
                    if (idx < 0) break;
                    int matchInLine = idx - lineStart;

                    if (query.WholeWord && !IsWholeWord(text, lineStart, lineEnd, matchInLine, query.Text.Length))
                    {
                        searchFrom = idx + 1;
                        continue;
                    }

                    string lineText = text.Substring(lineStart, len);
                    string snippet = BuildSnippet(text, idx, query.Text.Length, query.ContextChars);

                    yield return new SearchHit(
                        FilePath: filePath,
                        LineNumber: lineNumber,
                        LineText: lineText,
                        MatchStart: matchInLine,
                        MatchLength: query.Text.Length,
                        Snippet: snippet);

                    returned++;
                    if (returned >= query.MaxLineHits) yield break;

                    searchFrom = idx + Math.Max(1, query.Text.Length);
                }
            }

            lineStart = i + 1;
        }
    }

    private static bool IsWholeWord(string text, int lineStart, int lineEnd, int matchInLine, int matchLen)
    {
        int abs = lineStart + matchInLine;
        bool leftOk = abs == lineStart || !IsWordChar(text[abs - 1]);
        bool rightOk = abs + matchLen == lineEnd || !IsWordChar(text[abs + matchLen]);
        return leftOk && rightOk;
    }

    private static bool IsWordChar(char c)
    {
        var cat = CharUnicodeInfo.GetUnicodeCategory(c);
        return char.IsLetterOrDigit(c)
            || cat == UnicodeCategory.ConnectorPunctuation
            || cat == UnicodeCategory.NonSpacingMark;
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
