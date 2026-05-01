using Sho.Core.Models;
using Sho.Core.Text;

namespace Sho.Tests;

public class LineMatcherTests
{
    [Fact]
    public void Finds_match_with_correct_line_number_and_position()
    {
        var text = "first line\nsecond Ніколаенко line\nthird";
        var hits = LineMatcher.FindHits("f.txt", text, new SearchQuery("Ніколаенко")).ToList();

        Assert.Single(hits);
        Assert.Equal(2, hits[0].LineNumber);
        Assert.Equal("second Ніколаенко line", hits[0].LineText);
        Assert.Equal(7, hits[0].MatchStart);
        Assert.Equal("Ніколаенко".Length, hits[0].MatchLength);
    }

    [Fact]
    public void Case_insensitive_by_default()
    {
        var hits = LineMatcher.FindHits("f.txt", "Hello WORLD", new SearchQuery("world")).ToList();
        Assert.Single(hits);
    }

    [Fact]
    public void Case_sensitive_when_requested()
    {
        var hits = LineMatcher.FindHits("f.txt", "Hello WORLD", new SearchQuery("world", CaseSensitive: true)).ToList();
        Assert.Empty(hits);
    }

    [Fact]
    public void Returns_multiple_hits_per_line()
    {
        var hits = LineMatcher.FindHits("f.txt", "ab ab ab", new SearchQuery("ab")).ToList();
        Assert.Equal(3, hits.Count);
        Assert.All(hits, h => Assert.Equal(1, h.LineNumber));
    }

    [Fact]
    public void Whole_word_filter_works()
    {
        var hits = LineMatcher.FindHits("f.txt", "abracadabra abra ab", new SearchQuery("abra", WholeWord: true)).ToList();
        Assert.Single(hits);
        Assert.Equal("abracadabra abra ab", hits[0].LineText);
        Assert.Equal(12, hits[0].MatchStart);
    }

    [Fact]
    public void Snippet_includes_context()
    {
        var text = new string('x', 200) + "MATCH" + new string('y', 200);
        var hits = LineMatcher.FindHits("f.txt", text, new SearchQuery("MATCH", ContextChars: 50)).ToList();
        Assert.Single(hits);
        Assert.Contains("MATCH", hits[0].Snippet);
        Assert.True(hits[0].Snippet.Length <= 50 + 5 + 50);
    }

    [Fact]
    public void Empty_query_returns_no_hits()
    {
        var hits = LineMatcher.FindHits("f.txt", "anything", new SearchQuery("")).ToList();
        Assert.Empty(hits);
    }

    [Fact]
    public void Handles_crlf_line_endings()
    {
        var text = "one\r\ntwo MATCH\r\nthree";
        var hits = LineMatcher.FindHits("f.txt", text, new SearchQuery("MATCH")).ToList();
        Assert.Single(hits);
        Assert.Equal(2, hits[0].LineNumber);
        Assert.Equal("two MATCH", hits[0].LineText);
    }
}
