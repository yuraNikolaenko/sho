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
        var result = await engine.SearchAsync(new[] { _tmpDir }, new SearchQuery("Ніколаенко"), null, CancellationToken.None);
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
        var result = await engine.SearchAsync(new[] { _tmpDir }, new SearchQuery("Ніколаенко Юрій"), null, CancellationToken.None);
        Assert.Equal(1, result.FilesMatched);
        Assert.Single(result.Lines);
    }

    [Fact]
    public async Task Indexed_build_and_search()
    {
        var engine = new IndexedSearchEngine(indexRoot: _indexDir);
        await engine.BuildIndexAsync(new[] { _tmpDir }, null, CancellationToken.None);
        Assert.True(await engine.IsIndexBuiltAsync(new[] { _tmpDir }, CancellationToken.None));

        var result = await engine.SearchAsync(new[] { _tmpDir }, new SearchQuery("Ніколаенко"), null, CancellationToken.None);
        Assert.Null(result.Error);
        Assert.True(result.FilesMatched >= 3, $"expected ≥3, got {result.FilesMatched}");
        Assert.Contains(result.Files, f => f.FilePath.EndsWith("d.txt"));
    }

    [Fact]
    public async Task Indexed_quoted_single_word_finds_inflected_form()
    {
        var engine = new IndexedSearchEngine(indexRoot: _indexDir);
        await engine.BuildIndexAsync(new[] { _tmpDir }, null, CancellationToken.None);

        var bare = await engine.SearchAsync(new[] { _tmpDir }, new SearchQuery("Ніколаенк"), null, CancellationToken.None);
        var quoted = await engine.SearchAsync(new[] { _tmpDir }, new SearchQuery("\"Ніколаенк\""), null, CancellationToken.None);

        Assert.True(quoted.FilesMatched > 0, "quoted partial term should match indexed inflected forms");
        Assert.Equal(bare.FilesMatched, quoted.FilesMatched);
        Assert.Equal(bare.TotalHits, quoted.TotalHits);
    }

    [Fact]
    public async Task Multiple_roots_dedupe_and_aggregate()
    {
        var other = Path.Combine(Path.GetTempPath(), "sho-st-other-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        try
        {
            File.WriteAllText(Path.Combine(other, "x.txt"),
                "lone Ніколаенко match",
                new UTF8Encoding(true));

            var engine = new BruteForceSearchEngine();
            var result = await engine.SearchAsync(
                new[] { _tmpDir, Path.Combine(_tmpDir, "sub"), other },
                new SearchQuery("Ніколаенко"),
                null,
                CancellationToken.None);

            Assert.Null(result.Error);
            // 3 hits in _tmpDir tree (a, c, sub/d) + 1 in other = 4 files / 5 hits
            Assert.Equal(4, result.FilesMatched);
            Assert.Contains(result.Files, f => f.FilePath.EndsWith("x.txt"));
            Assert.Contains(result.Files, f => f.FilePath.EndsWith("d.txt"));
            // sub/d.txt should appear ONCE despite both _tmpDir and _tmpDir/sub being passed
            Assert.Equal(1, result.Files.Count(f => f.FilePath.EndsWith("d.txt")));
        }
        finally { try { Directory.Delete(other, true); } catch { } }
    }

    [Fact]
    public async Task Update_documents_upsert_adds_new_file()
    {
        var engine = new IndexedSearchEngine(indexRoot: _indexDir);
        await engine.BuildIndexAsync(new[] { _tmpDir }, null, CancellationToken.None);

        var newFile = Path.Combine(_tmpDir, "new.txt");
        File.WriteAllText(newFile, "fresh Ніколаенко content", new UTF8Encoding(true));

        int changed = await engine.UpdateDocumentsAsync(
            new[] { _tmpDir },
            new[] { newFile },
            Array.Empty<string>(),
            null,
            CancellationToken.None);

        Assert.Equal(1, changed);
        var result = await engine.SearchAsync(new[] { _tmpDir }, new SearchQuery("Ніколаенко"), null, CancellationToken.None);
        Assert.Contains(result.Files, f => f.FilePath.EndsWith("new.txt"));
    }

    [Fact]
    public async Task Update_documents_upsert_replaces_modified_file()
    {
        var engine = new IndexedSearchEngine(indexRoot: _indexDir);
        await engine.BuildIndexAsync(new[] { _tmpDir }, null, CancellationToken.None);

        var bPath = Path.Combine(_tmpDir, "b.md");
        File.WriteAllText(bPath, "now-with Ніколаенко inside", new UTF8Encoding(true));

        await engine.UpdateDocumentsAsync(
            new[] { _tmpDir },
            new[] { bPath },
            Array.Empty<string>(),
            null,
            CancellationToken.None);

        var result = await engine.SearchAsync(new[] { _tmpDir }, new SearchQuery("Ніколаенко"), null, CancellationToken.None);
        Assert.Contains(result.Files, f => f.FilePath.EndsWith("b.md"));
        // No duplicate doc for b.md (UpdateDocument by path key replaces, not appends).
        Assert.Equal(1, result.Files.Count(f => f.FilePath.EndsWith("b.md")));
    }

    [Fact]
    public async Task Update_documents_delete_removes_file()
    {
        var engine = new IndexedSearchEngine(indexRoot: _indexDir);
        await engine.BuildIndexAsync(new[] { _tmpDir }, null, CancellationToken.None);

        var cPath = Path.Combine(_tmpDir, "c.txt");
        File.Delete(cPath);

        await engine.UpdateDocumentsAsync(
            new[] { _tmpDir },
            Array.Empty<string>(),
            new[] { cPath },
            null,
            CancellationToken.None);

        var result = await engine.SearchAsync(new[] { _tmpDir }, new SearchQuery("Ніколаенко"), null, CancellationToken.None);
        Assert.DoesNotContain(result.Files, f => f.FilePath.EndsWith("c.txt"));
        // a.txt and sub/d.txt remain.
        Assert.True(result.FilesMatched >= 2);
    }

    [Fact]
    public async Task Update_documents_upsert_of_missing_file_acts_as_delete()
    {
        var engine = new IndexedSearchEngine(indexRoot: _indexDir);
        await engine.BuildIndexAsync(new[] { _tmpDir }, null, CancellationToken.None);

        var aPath = Path.Combine(_tmpDir, "a.txt");
        File.Delete(aPath);

        // Caller passed it as upsert, but file is gone — should still be removed.
        await engine.UpdateDocumentsAsync(
            new[] { _tmpDir },
            new[] { aPath },
            Array.Empty<string>(),
            null,
            CancellationToken.None);

        var result = await engine.SearchAsync(new[] { _tmpDir }, new SearchQuery("Ніколаенко"), null, CancellationToken.None);
        Assert.DoesNotContain(result.Files, f => f.FilePath.EndsWith("a.txt"));
    }

    [Fact]
    public async Task Sync_index_picks_up_added_modified_and_removed_files()
    {
        var dir = Path.Combine(_tmpDir, "sync");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "stay.txt"), "Ніколаєнко стабільний", new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(dir, "drop.txt"), "Ніколаєнко зайвий", new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(dir, "edit.txt"), "одне без імені", new UTF8Encoding(true));

        var idxDir = Path.Combine(_tmpDir, "_index_sync");
        Directory.CreateDirectory(idxDir);
        var engine = new IndexedSearchEngine(indexRoot: idxDir);
        await engine.BuildIndexAsync(new[] { dir }, null, CancellationToken.None);

        // Mutate the filesystem after the index was built.
        File.Delete(Path.Combine(dir, "drop.txt"));
        File.WriteAllText(Path.Combine(dir, "edit.txt"), "Ніколаєнко тепер тут", new UTF8Encoding(true));
        // mtime must change for sync to detect the edit
        File.SetLastWriteTimeUtc(Path.Combine(dir, "edit.txt"), DateTime.UtcNow.AddSeconds(5));
        File.WriteAllText(Path.Combine(dir, "fresh.txt"), "Ніколаєнко новий", new UTF8Encoding(true));

        int changed = await engine.SyncIndexAsync(new[] { dir }, null, CancellationToken.None);
        Assert.True(changed >= 3, $"sync should touch ≥3 docs (drop+edit+fresh), got {changed}");

        var result = await engine.SearchAsync(new[] { dir }, new SearchQuery("Ніколаєнко"), null, CancellationToken.None);
        Assert.Contains(result.Files, f => f.FilePath.EndsWith("stay.txt"));
        Assert.Contains(result.Files, f => f.FilePath.EndsWith("edit.txt"));
        Assert.Contains(result.Files, f => f.FilePath.EndsWith("fresh.txt"));
        Assert.DoesNotContain(result.Files, f => f.FilePath.EndsWith("drop.txt"));
    }

    [Fact]
    public async Task Sync_index_noop_when_no_changes()
    {
        var dir = Path.Combine(_tmpDir, "sync_noop");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "Ніколаєнко", new UTF8Encoding(true));

        var idxDir = Path.Combine(_tmpDir, "_index_sync_noop");
        Directory.CreateDirectory(idxDir);
        var engine = new IndexedSearchEngine(indexRoot: idxDir);
        await engine.BuildIndexAsync(new[] { dir }, null, CancellationToken.None);

        int changed = await engine.SyncIndexAsync(new[] { dir }, null, CancellationToken.None);
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task Update_documents_noop_when_index_missing()
    {
        var freshIndex = Path.Combine(_tmpDir, "_index_x");
        Directory.CreateDirectory(freshIndex);
        var engine = new IndexedSearchEngine(indexRoot: freshIndex);

        int changed = await engine.UpdateDocumentsAsync(
            new[] { _tmpDir },
            new[] { Path.Combine(_tmpDir, "a.txt") },
            Array.Empty<string>(),
            null,
            CancellationToken.None);

        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task Indexed_uk_morphology_finds_inflected_forms()
    {
        // "будинок" (nom.sg) ↔ "будинком" (instr.sg) ↔ "будинки" (nom.pl) — same lemma in UA dict.
        var morphRoot = Path.Combine(_tmpDir, "morph");
        Directory.CreateDirectory(morphRoot);
        File.WriteAllText(Path.Combine(morphRoot, "n.txt"), "великий будинок стояв", new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(morphRoot, "i.txt"), "перед будинком площа", new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(morphRoot, "p.txt"), "усі будинки міста", new UTF8Encoding(true));

        var idxDir = Path.Combine(_tmpDir, "_index_morph");
        Directory.CreateDirectory(idxDir);
        var engine = new IndexedSearchEngine(indexRoot: idxDir);
        await engine.BuildIndexAsync(new[] { morphRoot }, null, CancellationToken.None);

        // Query a different inflection than what each file contains.
        var byNom = await engine.SearchAsync(new[] { morphRoot }, new SearchQuery("будинок"), null, CancellationToken.None);
        Assert.True(byNom.FilesMatched >= 2,
            $"querying 'будинок' should match files containing 'будинком' and/or 'будинки' via UA lemmatization, got {byNom.FilesMatched}");
    }

    [Fact]
    public async Task Indexed_finds_nominative_proper_noun_phrase()
    {
        // The exact query the user typed: "ніколаєнко юрій" (lowercase nominative)
        // against indexed "Ніколаєнко Юрій" (Title Case nominative).
        var dir = Path.Combine(_tmpDir, "names_nom");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ord.txt"),
            "Наказ. Ніколаєнко Юрій призначений командиром.",
            new UTF8Encoding(true));

        var idxDir = Path.Combine(_tmpDir, "_index_names_nom");
        Directory.CreateDirectory(idxDir);
        var engine = new IndexedSearchEngine(indexRoot: idxDir);
        await engine.BuildIndexAsync(new[] { dir }, null, CancellationToken.None);

        var result = await engine.SearchAsync(
            new[] { dir },
            new SearchQuery("ніколаєнко юрій"),
            null,
            CancellationToken.None);

        Assert.Null(result.Error);
        Assert.True(result.FilesMatched >= 1,
            $"querying nominative 'ніколаєнко юрій' should find file containing 'Ніколаєнко Юрій', got {result.FilesMatched}");
        Assert.True(result.TotalHits >= 1,
            "must produce at least one highlighted hit");
    }

    [Fact]
    public async Task Indexed_multi_bare_intersection_of_large_sets()
    {
        // Mimic user's scale: many docs with one name, fewer with another, one
        // with both. Single-name queries return their respective counts;
        // multi-name AND should return at least the overlap (≥ 1 here).
        var dir = Path.Combine(_tmpDir, "scale");
        Directory.CreateDirectory(dir);

        // 30 docs containing "Юрія" only.
        for (int i = 0; i < 30; i++)
            File.WriteAllText(Path.Combine(dir, $"jur_{i}.txt"),
                $"справа №{i}: Юрія Петренка призначено бригадиром.",
                new UTF8Encoding(true));

        // 10 docs containing "Ніколаєнка" only.
        for (int i = 0; i < 10; i++)
            File.WriteAllText(Path.Combine(dir, $"nik_{i}.txt"),
                $"справа №{i}: Ніколаєнка Сергія направлено на курси.",
                new UTF8Encoding(true));

        // 1 doc with both — exactly the user's fragment.
        File.WriteAllText(Path.Combine(dir, "both.txt"),
            "солдата НІКОЛАЄНКА Юрія Вікторовича, вогнеметника взводу.",
            new UTF8Encoding(true));

        var idxDir = Path.Combine(_tmpDir, "_index_scale");
        Directory.CreateDirectory(idxDir);
        var engine = new IndexedSearchEngine(indexRoot: idxDir);
        await engine.BuildIndexAsync(new[] { dir }, null, CancellationToken.None);

        var jur = await engine.SearchAsync(new[] { dir }, new SearchQuery("Юрія"), null, CancellationToken.None);
        var nik = await engine.SearchAsync(new[] { dir }, new SearchQuery("Ніколаєнка"), null, CancellationToken.None);
        var both = await engine.SearchAsync(new[] { dir }, new SearchQuery("Ніколаєнка Юрія"), null, CancellationToken.None);

        Assert.True(jur.FilesMatched >= 31, $"'Юрія' alone: expected ≥31, got {jur.FilesMatched}");
        Assert.True(nik.FilesMatched >= 11, $"'Ніколаєнка' alone: expected ≥11, got {nik.FilesMatched}");
        Assert.True(both.FilesMatched >= 1, $"'Ніколаєнка Юрія' AND: expected ≥1 (the both.txt doc), got {both.FilesMatched}. " +
                                            $"jur={jur.FilesMatched}, nik={nik.FilesMatched}");
    }

    [Fact]
    public async Task Indexed_multi_token_emits_hits_only_on_lines_with_all_tokens()
    {
        // User's pain: an investigation spreadsheet with 2500+ rows, "Юрій"
        // appears in many rows (different people), "Ніколаєнка" appears once.
        // Multi-bare query "Ніколаєнка Юрія" should highlight ONLY the row
        // with both, not every "Юрій"-containing row.
        var dir = Path.Combine(_tmpDir, "many_yurii");
        Directory.CreateDirectory(dir);

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 50; i++)
            sb.AppendLine($"Юрій Петренко {i}");
        sb.AppendLine("солдата НІКОЛАЄНКА Юрія Вікторовича вогнеметника");
        for (int i = 0; i < 50; i++)
            sb.AppendLine($"Юрій Шевченко {i}");
        File.WriteAllText(Path.Combine(dir, "investigation.txt"), sb.ToString(), new UTF8Encoding(true));

        var idxDir = Path.Combine(_tmpDir, "_index_yurii");
        Directory.CreateDirectory(idxDir);
        var engine = new IndexedSearchEngine(indexRoot: idxDir);
        await engine.BuildIndexAsync(new[] { dir }, null, CancellationToken.None);

        var single = await engine.SearchAsync(new[] { dir }, new SearchQuery("Юрій"), null, CancellationToken.None);
        var multi = await engine.SearchAsync(new[] { dir }, new SearchQuery("Ніколаєнка Юрія"), null, CancellationToken.None);

        // Sanity: single-token "Юрій" finds it many times.
        Assert.True(single.TotalHits >= 50, $"single 'Юрій' should hit many times, got {single.TotalHits}");

        // Critical: multi-token AND emits hits only on the row with BOTH names.
        Assert.True(multi.FilesMatched >= 1, $"multi: file should match, got {multi.FilesMatched}");
        Assert.True(multi.TotalHits <= 5,
            $"multi 'Ніколаєнка Юрія' should emit ≤5 hits (just the one matching line), got {multi.TotalHits}. " +
            $"Single 'Юрій' got {single.TotalHits} for comparison.");
    }

    [Fact]
    public async Task Indexed_query_does_not_match_different_surname_via_fuzzy()
    {
        // Levenshtein("ніколаєнка", "ніколаєва") = 2 (sub 'н'→'в' + del 'к' + keep 'а').
        // Old fuzzy=2 incorrectly bridged these; fuzzy=1 must NOT.
        var dir = Path.Combine(_tmpDir, "diff_surname");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "target.txt"), "Ніколаєнка Юрія тут", new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(dir, "other.txt"), "Ніколаєва Олена не тут", new UTF8Encoding(true));

        var idxDir = Path.Combine(_tmpDir, "_index_diff_surname");
        Directory.CreateDirectory(idxDir);
        var engine = new IndexedSearchEngine(indexRoot: idxDir);
        await engine.BuildIndexAsync(new[] { dir }, null, CancellationToken.None);

        var result = await engine.SearchAsync(
            new[] { dir },
            new SearchQuery("Ніколаєнка"),
            null,
            CancellationToken.None);

        Assert.Contains(result.Files, f => f.FilePath.EndsWith("target.txt"));
        Assert.DoesNotContain(result.Files, f => f.FilePath.EndsWith("other.txt"));
    }

    [Fact]
    public async Task Indexed_finds_user_real_document_excerpt()
    {
        // User's actual document fragment: surname in ALL CAPS, names in Title Case.
        var dir = Path.Combine(_tmpDir, "real");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "order.txt"),
            "солдата НІКОЛАЄНКА Юрія Вікторовича, вогнеметника взводу " +
            "радіаційного, хімічного, біологічного захисту військової частини " +
            "А4689, до м. Миколаїв",
            new UTF8Encoding(true));

        var idxDir = Path.Combine(_tmpDir, "_index_real");
        Directory.CreateDirectory(idxDir);
        var engine = new IndexedSearchEngine(indexRoot: idxDir);
        await engine.BuildIndexAsync(new[] { dir }, null, CancellationToken.None);

        var result = await engine.SearchAsync(
            new[] { dir },
            new SearchQuery("Ніколаєнка Юрія"),
            null,
            CancellationToken.None);

        Assert.Null(result.Error);
        Assert.True(result.FilesMatched >= 1,
            $"querying 'Ніколаєнка Юрія' should find file with 'НІКОЛАЄНКА Юрія Вікторовича', got {result.FilesMatched}");
        Assert.True(result.TotalHits >= 1, "must produce highlighted hits");
    }

    [Fact]
    public async Task Indexed_finds_capitalized_inflected_phrase()
    {
        // User's exact reported scenario: query "Ніколаєнка Юрія" (both Title-Case
        // genitive) should find file with "Ніколаєнко Юрій" (nominative).
        var dir = Path.Combine(_tmpDir, "names_caps");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ord.txt"),
            "У наказі: Ніколаєнко Юрій звільнений у запас.",
            new UTF8Encoding(true));

        var idxDir = Path.Combine(_tmpDir, "_index_caps");
        Directory.CreateDirectory(idxDir);
        var engine = new IndexedSearchEngine(indexRoot: idxDir);
        await engine.BuildIndexAsync(new[] { dir }, null, CancellationToken.None);

        var result = await engine.SearchAsync(
            new[] { dir },
            new SearchQuery("Ніколаєнка Юрія"),
            null,
            CancellationToken.None);

        Assert.Null(result.Error);
        Assert.True(result.FilesMatched >= 1,
            $"querying 'Ніколаєнка Юрія' should find file with 'Ніколаєнко Юрій', got {result.FilesMatched}");
    }

    [Fact]
    public async Task Indexed_finds_inflected_proper_noun_phrase()
    {
        // The UA Morfologik dict doesn't know surnames, so "Ніколаєнко" lemmatizes
        // to itself and "Ніколаєнка" (genitive) lemmatizes to itself — the two are
        // not equal. Same for "Юрій" / "Юрія". Strict lemmatized PhraseQuery would
        // miss this. The fuzzy fallback (edit-distance 2) is what makes it match.
        var dir = Path.Combine(_tmpDir, "names");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ref.txt"),
            "Звертаємось до пана Ніколаєнко Юрій з проханням",
            new UTF8Encoding(true));

        var idxDir = Path.Combine(_tmpDir, "_index_names");
        Directory.CreateDirectory(idxDir);
        var engine = new IndexedSearchEngine(indexRoot: idxDir);
        await engine.BuildIndexAsync(new[] { dir }, null, CancellationToken.None);

        var result = await engine.SearchAsync(
            new[] { dir },
            new SearchQuery("ніколаєнка юрія"),
            null,
            CancellationToken.None);

        Assert.Null(result.Error);
        Assert.True(result.FilesMatched >= 1,
            $"querying inflected 'ніколаєнка юрія' should find file containing 'Ніколаєнко Юрій', got {result.FilesMatched}");
        Assert.True(result.TotalHits >= 1,
            "must produce at least one highlighted hit so the Lines panel isn't empty");
    }

    [Fact]
    public async Task Indexed_meta_records_analyzer_version()
    {
        var engine = new IndexedSearchEngine(indexRoot: _indexDir);
        await engine.BuildIndexAsync(new[] { _tmpDir }, null, CancellationToken.None);

        var meta = await engine.GetIndexMetadataAsync(new[] { _tmpDir }, CancellationToken.None);
        Assert.NotNull(meta);
        Assert.Equal(IndexedSearchEngine.AnalyzerVersion, meta!.AnalyzerVersion);
    }

    [Fact]
    public async Task Indexed_search_without_index_returns_error()
    {
        var freshIndex = Path.Combine(_tmpDir, "_index2");
        Directory.CreateDirectory(freshIndex);
        var engine = new IndexedSearchEngine(indexRoot: freshIndex);
        var result = await engine.SearchAsync(new[] { _tmpDir }, new SearchQuery("Ніколаенко"), null, CancellationToken.None);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Files);
    }
}
