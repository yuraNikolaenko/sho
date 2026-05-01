using System.Text;
using Sho.Core.Models;
using Sho.Search;

namespace Sho.Tests;

public class SearchEngineTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _indexDir;

    public SearchEngineTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "sho-st-" + Guid.NewGuid().ToString("N"));
        _indexDir = Path.Combine(_tmpDir, "_index");
        Directory.CreateDirectory(_tmpDir);
        Directory.CreateDirectory(_indexDir);

        File.WriteAllText(Path.Combine(_tmpDir, "a.txt"),
            "первая строка\nдокумент Ніколаенко Юрій Вікторович\nконец",
            new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(_tmpDir, "b.md"),
            "another doc\nNothing matching here\nthird line",
            new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(_tmpDir, "c.txt"),
            "Ніколаенко\nstill Ніколаенко\nplain",
            new UTF8Encoding(true));
        Directory.CreateDirectory(Path.Combine(_tmpDir, "sub"));
        File.WriteAllText(Path.Combine(_tmpDir, "sub", "d.txt"),
            "nested Ніколаенко\nend",
            new UTF8Encoding(true));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, true); } catch { }
    }

    [Fact]
    public async Task Brute_force_finds_matches_in_subdirs()
    {
        var engine = new BruteForceSearchEngine();
        var result = await engine.SearchAsync(_tmpDir, new SearchQuery("Ніколаенко"), null, CancellationToken.None);
        Assert.Null(result.Error);
        Assert.Equal(3, result.FilesMatched);
        Assert.Equal(4, result.TotalHits);
        Assert.Contains(result.Files, f => f.FilePath.EndsWith("d.txt"));
        Assert.Contains(result.Lines, l => l.LineText.Contains("Ніколаенко"));
    }

    [Fact]
    public async Task Brute_force_phrase_search()
    {
        var engine = new BruteForceSearchEngine();
        var result = await engine.SearchAsync(_tmpDir, new SearchQuery("Ніколаенко Юрій"), null, CancellationToken.None);
        Assert.Equal(1, result.FilesMatched);
        Assert.Single(result.Lines);
    }

    [Fact]
    public async Task Indexed_build_and_search()
    {
        var engine = new IndexedSearchEngine(indexRoot: _indexDir);
        await engine.BuildIndexAsync(_tmpDir, null, CancellationToken.None);
        Assert.True(await engine.IsIndexBuiltAsync(_tmpDir, CancellationToken.None));

        var result = await engine.SearchAsync(_tmpDir, new SearchQuery("Ніколаенко"), null, CancellationToken.None);
        Assert.Null(result.Error);
        Assert.True(result.FilesMatched >= 3, $"expected ≥3, got {result.FilesMatched}");
        Assert.Contains(result.Files, f => f.FilePath.EndsWith("d.txt"));
    }

    [Fact]
    public async Task Indexed_quoted_single_word_finds_inflected_form()
    {
        var engine = new IndexedSearchEngine(indexRoot: _indexDir);
        await engine.BuildIndexAsync(_tmpDir, null, CancellationToken.None);

        var bare = await engine.SearchAsync(_tmpDir, new SearchQuery("Ніколаенк"), null, CancellationToken.None);
        var quoted = await engine.SearchAsync(_tmpDir, new SearchQuery("\"Ніколаенк\""), null, CancellationToken.None);

        Assert.True(quoted.FilesMatched > 0, "quoted partial term should match indexed inflected forms");
        Assert.Equal(bare.FilesMatched, quoted.FilesMatched);
        Assert.Equal(bare.TotalHits, quoted.TotalHits);
    }

    [Fact]
    public async Task Indexed_search_without_index_returns_error()
    {
        var freshIndex = Path.Combine(_tmpDir, "_index2");
        Directory.CreateDirectory(freshIndex);
        var engine = new IndexedSearchEngine(indexRoot: freshIndex);
        var result = await engine.SearchAsync(_tmpDir, new SearchQuery("Ніколаенко"), null, CancellationToken.None);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Files);
    }
}
