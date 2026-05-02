using Lucene.Net.Search;
using Sho.Core.Models;
using Sho.Search;
using Xunit.Abstractions;

namespace Sho.Tests;

public class QueryParserTests
{
    private readonly ITestOutputHelper _output;
    public QueryParserTests(ITestOutputHelper output) { _output = output; }

    [Theory]
    [InlineData("Ніколаенко", false)]
    [InlineData("Юрій Вікторович", false)]
    [InlineData("\"phrase\"", true)]
    [InlineData("a AND b", true)]
    [InlineData("term1 OR term2", true)]
    [InlineData("+req -bad", true)]
    [InlineData("term*", true)]
    [InlineData("ext:pdf", true)]
    [InlineData("term~2", true)]
    [InlineData("a NOT b", true)]
    public void Detects_query_syntax(string text, bool expected)
    {
        Assert.Equal(expected, IndexedSearchEngine.HasQuerySyntax(text));
    }

    [Fact]
    public void Single_bare_term_uses_lemma_or_wildcard_hybrid()
    {
        var q = IndexedSearchEngine.BuildLuceneQuery(new SearchQuery("Ніколаенко"));
        var bq = Assert.IsType<BooleanQuery>(q);
        Assert.Contains(bq.Clauses, c => c.Query is WildcardQuery wq && wq.Term.Text.Contains("ніколаенко"));
        Assert.Contains(bq.Clauses, c => c.Query is TermQuery);
    }

    [Fact]
    public void Multi_bare_terms_use_and_of_hybrids()
    {
        // Multi-bare is no longer a strict PhraseQuery — it's an AND of per-token
        // hybrids (lemma OR wildcard OR fuzzy) so inflected proper nouns
        // ("ніколаєнка юрія") still match indexed "Ніколаєнко Юрій".
        var q = IndexedSearchEngine.BuildLuceneQuery(new SearchQuery("Юрій Вікторович"));
        var bq = Assert.IsType<BooleanQuery>(q);
        Assert.Equal(2, bq.Clauses.Count);
        Assert.All(bq.Clauses, c =>
        {
            Assert.Equal(Occur.MUST, c.Occur);
            var inner = Assert.IsType<BooleanQuery>(c.Query);
            Assert.Contains(inner.Clauses, ic => ic.Query is WildcardQuery);
        });
    }

    [Fact]
    public void Quoted_multiword_phrase_stays_phrase()
    {
        var q = IndexedSearchEngine.BuildLuceneQuery(new SearchQuery("\"hello world\""));
        Assert.IsType<PhraseQuery>(q);
    }

    [Fact]
    public void Quoted_single_word_becomes_lemma_or_wildcard_hybrid()
    {
        var q = IndexedSearchEngine.BuildLuceneQuery(new SearchQuery("\"Ніколаєнк\""));
        var bq = Assert.IsType<BooleanQuery>(q);
        Assert.Contains(bq.Clauses, c => c.Query is WildcardQuery wq && wq.Term.Text == "*ніколаєнк*");
        Assert.Contains(bq.Clauses, c => c.Query is TermQuery tq && tq.Term.Text == "ніколаєнк");
    }

    [Fact]
    public void Boolean_AND_terms_become_hybrid_clauses()
    {
        var q = IndexedSearchEngine.BuildLuceneQuery(new SearchQuery("Ніколаєнк AND Юрій"));
        var bq = Assert.IsType<BooleanQuery>(q);
        // Each top-level clause is a BooleanQuery (lemma OR wildcard) for one term.
        Assert.All(bq.Clauses, c =>
        {
            var inner = Assert.IsType<BooleanQuery>(c.Query);
            Assert.Contains(inner.Clauses, ic => ic.Query is WildcardQuery);
            Assert.Contains(inner.Clauses, ic => ic.Query is TermQuery);
        });
    }

    [Fact]
    public void Plus_minus_terms_become_hybrid_clauses()
    {
        var q = IndexedSearchEngine.BuildLuceneQuery(new SearchQuery("+Ніколаєнк -тест"));
        var bq = Assert.IsType<BooleanQuery>(q);
        Assert.All(bq.Clauses, c =>
        {
            var inner = Assert.IsType<BooleanQuery>(c.Query);
            Assert.Contains(inner.Clauses, ic => ic.Query is WildcardQuery);
            Assert.Contains(inner.Clauses, ic => ic.Query is TermQuery);
        });
    }

    [Fact]
    public void Field_query_uses_parser()
    {
        var q = IndexedSearchEngine.BuildLuceneQuery(new SearchQuery("ext:pdf"));
        Assert.NotNull(q);
    }

    [Fact]
    public void UA_morfologik_lemma_diagnostic()
    {
        // Diagnostic: capture what the bundled UA dictionary actually emits for
        // the inflections we test against. Failing intentionally to surface tokens.
        var inputs = new[]
        {
            "будинок", "будинком", "будинки", "стіл", "столи", "столу",
            // Proper nouns + surrounding tokens to surface stopword/dict behavior.
            "ніколаєнко", "Ніколаєнко", "ніколаєнка",
            "юрій", "Юрій", "юрія",
            "Ніколаєнко Юрій", "ніколаєнко юрій", "ніколаєнка юрія",
            // ALL CAPS forms — real-world military orders use uppercase surnames.
            "НІКОЛАЄНКА", "НІКОЛАЄНКО", "ЮРІЯ", "ВІКТОРОВИЧА",
            "НІКОЛАЄНКА Юрія Вікторовича",
        };
        var report = string.Join(" | ", inputs.Select(i =>
            $"{i} → [{string.Join(",", IndexedSearchEngine.AnalyzeToTokens(i))}]"));
        // Real assertion: same input returns the same set of lemmas across calls.
        foreach (var i in inputs)
        {
            var a = IndexedSearchEngine.AnalyzeToTokens(i);
            var b = IndexedSearchEngine.AnalyzeToTokens(i);
            Assert.Equal(a, b);
        }
        // Surface the report so morphology tests can be tuned.
        _output.WriteLine(report);
    }
}
