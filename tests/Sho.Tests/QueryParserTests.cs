using Lucene.Net.Search;
using Sho.Core.Models;
using Sho.Search;

namespace Sho.Tests;

public class QueryParserTests
{
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
    public void Single_bare_term_uses_wildcard()
    {
        var q = IndexedSearchEngine.BuildLuceneQuery(new SearchQuery("Ніколаенко"));
        Assert.IsType<WildcardQuery>(q);
    }

    [Fact]
    public void Multi_bare_terms_use_phrase()
    {
        var q = IndexedSearchEngine.BuildLuceneQuery(new SearchQuery("Юрій Вікторович"));
        Assert.IsType<PhraseQuery>(q);
    }

    [Fact]
    public void Quoted_multiword_phrase_stays_phrase()
    {
        var q = IndexedSearchEngine.BuildLuceneQuery(new SearchQuery("\"hello world\""));
        Assert.IsType<PhraseQuery>(q);
    }

    [Fact]
    public void Quoted_single_word_becomes_wildcard()
    {
        var q = IndexedSearchEngine.BuildLuceneQuery(new SearchQuery("\"Ніколаєнк\""));
        var wq = Assert.IsType<WildcardQuery>(q);
        Assert.Equal("*ніколаєнк*", wq.Term.Text);
    }

    [Fact]
    public void Boolean_AND_terms_become_wildcard_clauses()
    {
        var q = IndexedSearchEngine.BuildLuceneQuery(new SearchQuery("Ніколаєнк AND Юрій"));
        var bq = Assert.IsType<BooleanQuery>(q);
        Assert.All(bq.Clauses, c => Assert.IsType<WildcardQuery>(c.Query));
    }

    [Fact]
    public void Plus_minus_terms_become_wildcard_clauses()
    {
        var q = IndexedSearchEngine.BuildLuceneQuery(new SearchQuery("+Ніколаєнк -тест"));
        var bq = Assert.IsType<BooleanQuery>(q);
        Assert.All(bq.Clauses, c => Assert.IsType<WildcardQuery>(c.Query));
    }

    [Fact]
    public void Field_query_uses_parser()
    {
        var q = IndexedSearchEngine.BuildLuceneQuery(new SearchQuery("ext:pdf"));
        Assert.NotNull(q);
    }
}
