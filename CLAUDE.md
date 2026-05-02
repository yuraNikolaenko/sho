# CLAUDE.md

Context for AI sessions working on this repo. Read this first before code changes.

## What this app is

**Shozilla** — Windows desktop app for fast full-text search across user documents.

- Pick one or many folders (across drives), build an optional Lucene index, search.
- Two engines: **brute-force** (parallel scan, no setup) and **indexed** (Lucene.NET).
- Results split into a Files panel (one row per file with hits) and a Lines panel (every matched line with ±80 char snippet).
- Inline DOCX preview pane (WebView2) with highlighted matches and auto-scroll.
- Theme (dark/light), language (uk/en), query history persisted across sessions.

## Stack

- **.NET 9** — `net9.0` for libraries, `net9.0-windows` for the WPF app.
- **WPF** + **WindowsForms** (the App project sets `UseWindowsForms=true` for legacy interop / CodePages).
- **Wpf.Ui 4.0.2** — Fluent / Win11 Mica chrome (`FluentWindow`, `TitleBar`, `Card`, `SymbolIcon`, themed `Button`/`TextBox`/`ToggleSwitch`/`ToggleButton`).
- **CommunityToolkit.Mvvm 8.4.0** — `[ObservableProperty]`, `[RelayCommand]`.
- **Lucene.NET 4.8.0-beta00017** + `QueryParsers.Classic` + `Analysis.Morfologik` (Ukrainian lemmatization via bundled UA dict).
- **PdfPig** (PDF), **DocumentFormat.OpenXml** (DOCX/XLSX/PPTX), **NPOI 2.5.6** (XLS via HSSF, plus POIFS to read OLE for legacy DOC), custom RTF parser.
- **UTF.Unknown** + **System.Text.Encoding.CodePages** (CP1251/KOI8-U detection).
- **Mammoth 1.11.0** (DOCX → HTML for preview).
- **Microsoft.Web.WebView2 1.0.2792.45** (preview rendering).

## Solution layout

```
Sho.sln
├── src/
│   ├── Sho.Core/          POCO models, abstractions, line-matching, path hashing.
│   ├── Sho.Extractors/    IFileTextExtractor implementations + registry.
│   ├── Sho.Search/        BruteForceSearchEngine + IndexedSearchEngine + FileScanner.
│   └── Sho.App/           WPF MVVM UI. AssemblyName = "Shozilla", exe = Shozilla.exe.
├── tests/
│   └── Sho.Tests/         xUnit. 57 tests, ~7 s. Covers LineMatcher, extractors,
│                          QueryParser (incl. UA morphology + fuzzy strictness),
│                          both engines, incremental update, sync, multi-root.
└── docs/
    ├── user-guide.md
    ├── developer-guide.md
    └── ai-portability-prompt.md
```

## Build, run, test

```powershell
dotnet build Sho.sln
dotnet run --project src\Sho.App\Sho.App.csproj
dotnet test tests\Sho.Tests\Sho.Tests.csproj
```

Single-file publish (Win-x64):
```powershell
dotnet publish src\Sho.App\Sho.App.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## Key contracts (Sho.Core)

- `IFileTextExtractor` — `CanHandle(path) → bool`, `ExtractAsync(path, ct) → string` (plain text, no markup).
- `ITextExtractorRegistry` — resolves extractor by extension, exposes the supported-extension set.
- `ISearchEngine.SearchAsync(IReadOnlyList<string> rootFolders, SearchQuery, IProgress<IndexProgress>?, CancellationToken) → SearchResult`.
- `IIndexedSearchEngine` adds `BuildIndexAsync`, `IsIndexBuiltAsync`, `DeleteIndexAsync`, `GetIndexMetadataAsync`, `UpdateDocumentsAsync` (explicit upsert/delete lists), `SyncIndexAsync` (diff disk vs. index, auto-derive lists).
- `SearchQuery` = `{ Text, CaseSensitive, WholeWord, ContextChars=80, MaxLineHits=5000 }`.
- `SearchResult` = `{ Files: FileSummary[], Lines: SearchHit[], TotalFilesScanned, FilesMatched, TotalHits, Elapsed, Error? }`.
- `IndexMetadata` = `{ BuiltUtc, DocumentCount, RootFolders, AnalyzerVersion }` — written as `meta.json` inside the index folder. `AnalyzerVersion` mismatch with `IndexedSearchEngine.AnalyzerVersion` triggers a "rebuild for UA morphology" hint in the status bar.
- `IndexProgress` = `{ FilesProcessed, FilesTotal, CurrentFile?, Stage?, ElapsedMs }`.

## Search semantics

- **Brute-force** searches every supported file under every root, scans content with `LineMatcher.FindHits`. Roots are deduped (`BruteForceSearchEngine.DedupeRoots`) so overlapping ancestors don't double-count.
- **Indexed** stores text in a Lucene index whose folder is named by `PathHasher.Hash(IReadOnlyList<string>)` (sorted + normalized + sha256). Same set of roots → same index. Different set → different folder. Indexing/parsing analyzer is `LowerUkrainianAnalyzer` (an `AnalyzerWrapper` over `UkrainianMorfologikAnalyzer` with a final `LowerCaseFilter` so dict-cased lemmas land lowercase) — tokens are lemmatized at index AND query time, so "будинок" matches indexed "будинком" / "будинки".
- Query parsing in `IndexedSearchEngine.BuildLuceneQuery`:
  - If the query contains any of `+ - " ( ) * ? ~ ^ : \ [ ] { }` or the words `AND`/`OR`/`NOT`, it goes through Lucene's `QueryParser` (which feeds through the UA analyzer).
  - 1 bare token / single-term parsed result → hybrid `BooleanQuery [TermQuery(lemma) SHOULD, WildcardQuery("*raw*") SHOULD, FuzzyQuery(raw, edits=1) SHOULD]`. Three branches: lemma covers UA-dict inflections; wildcard covers partial inputs; fuzzy covers typos / minor edits. **Fuzzy=1 is intentional** — fuzzy=2 was lenient enough to bridge "ніколаєнка" → "ніколаєва" (Levenshtein 2). The lemma branch already handles real inflections via the UA dict, so fuzzy is just an opt-in safety net for edits ≥ 4 chars. Users who want lenient search type `~2` explicitly per query.
  - 2+ bare tokens → routed through `QueryParser` with `AND_OPERATOR` and run through `WildcardifySingleTerms` (same hybrid wrap). No positional adjacency — quote the phrase to get `PhraseQuery`.
- After Lucene picks documents, the Lines panel and DOCX preview highlights are produced by `MorphologicalLineMatcher`. It takes the user's space-split query tokens, builds a per-token "group" (lemma set + raw + length), tokenizes the doc with the same analyzer, and **only emits hits on lines where every group fired**. Without this group-AND step, a query like "Ніколаєнка Юрія" against a 2500-row roster lit up every line with "Юрій" because the matcher saw the two query terms as independent. Per-token match within a group is still flexible: lemma equality, raw substring, or Levenshtein ≤ 1 (matches the FuzzyQuery threshold).
- Brute-force search remains substring-only (no morphology); `LineMatcher.FindHits` is used there.

## Index storage

`%LOCALAPPDATA%\sho\indexes\<sha256-prefix-of-roots>\` — Lucene segment files + `meta.json`.

## Settings storage

`%LOCALAPPDATA%\sho\settings.json` — see `Sho.App.Services.AppSettings`:
```
{ Theme, Language, LastFolder?, LastFolders[], ShowPreview, QueryHistory[],
  AutoUpdateIndex, CompactMode }
```

## Preview pipeline

1. `Sho.App.Services.DocxPreviewService.Render(filePath, terms, darkTheme, scrollToMarkIndex)` writes a complete HTML page to `%LOCALAPPDATA%\sho\preview\<hash>.html` and returns the path.
2. The HTML is body produced by Mammoth + a self-contained `<script>` that walks text nodes, wraps matched terms in `<mark id="m0..N">` (numbered in document order), and scrolls to `#m{markIndex}` (default 0). Match expansion: starts only at word boundaries and extends to the end of the word, so a stem like "ніколаєн" highlights all of "Ніколаєнка" / "НІКОЛАЄНКА".
3. `MainWindow` registers a virtual host map: `https://sho-preview/` → preview directory. Nav uses `https://sho-preview/<hash>.html?t=<utc-ticks>` (timestamp busts WebView2's cache so re-rendering with new highlights always reloads).
4. `NavigationCompleted` ignores `OperationCanceled` and `ConnectionAborted` (they fire on rapid row clicks where one nav cancels another) and only surfaces real errors as red text over the WebView.
5. **Selection drives preview** via `MainViewModel`:
   - SelectedFile changes → load that file, mark index = 0.
   - SelectedHit changes → load hit's file (auto-selecting in Files panel) AND scroll to that hit's mark — `markIndex` = position of the hit among hits of its file (Lines is sorted by file order then line number, marks numbered in same order, so positions align).
   - `_suppressSelectionSync` flag prevents recursive setter loops between SelectedFile ↔ SelectedHit.
6. **Highlight terms** = user's literal split + `IndexedSearchEngine.AnalyzeToTokens(query)` lemmas + truncated stems (lemma minus 2 chars, only for ≥6-char lemmas). Stems bridge UA case-endings the simple substring search would miss.

## Critical gotchas

These cost real time to debug — keep them in mind:

1. **Drive-letter paths on Windows.** `Directory.EnumerateDirectories("C:")` returns the children of the *current directory on C:*, not the root. Always use `"C:\"` (with trailing separator) for drive roots. `FolderPickerWindow.BuildRoots` keeps the trailing slash; `FolderTreeNode.Load` defensively appends one if it sees a 2-char `X:` path.

2. **`RowDefinition` / `ColumnDefinition` don't inherit DataContext.** They're `Freezable`, not `FrameworkElement`. Bindings like `<RowDefinition Height="{Binding Foo, ...}"/>` silently never resolve. The preview-off row collapse is managed from code-behind (`UpdatePreviewLayout`) for this reason.

3. **WebView2 + `file://`.** Direct `file:///C:/...` URLs are blocked in WebView2. Use `CoreWebView2.SetVirtualHostNameToFolderMapping("sho-preview", dir, Allow)` and navigate to `https://sho-preview/<file>`.

4. **`NavigateToString` size limit (~2 MB).** Mammoth output for a DOCX with images can exceed this; the page silently fails to render. We always write to a file and navigate via the virtual host instead.

5. **WPF/WinForms type conflicts.** Both are referenced by Sho.App, so `Application`, `MessageBox`, `Clipboard`, `Brushes`, `Binding`, `HorizontalAlignment`, `Color`, `Size` are ambiguous. `Sho.App/Usings.cs` aliases the WPF versions globally; in code that hits ones not aliased (e.g., `Color`, `Size`, `HorizontalAlignment` in `AppIconFactory`), use fully-qualified `System.Windows.*`.

6. **Wpf.Ui Card centers content when child is short.** When the inner DataGrid has 0 items, the section header TextBlock floats vertically. Results-area frames use plain `<Border>` + `<DockPanel LastChildFill="True">` instead of `<ui:Card>` to anchor headers at top regardless of content height. The Settings card stays as `<ui:Card>` because all its children are auto-sized.

7. **Wpf.Ui style override loses theming.** `<Style TargetType="ui:Button">` without `BasedOn={StaticResource {x:Type ui:Button}}` replaces the entire Fluent style and you get plain WPF chrome. Always BasedOn when overriding Wpf.Ui control styles.

8. **DataGrid background = `SolidBackgroundFillColorBaseBrush`.** Same brush as the column header so rows, headers and the ScrollViewer track read as one surface. Old `ResultsBackground` / `ResultsForeground` keys still exist in App.xaml + `UpdateContrastBrushes` mutates them on theme switch, but the styles no longer reference them — kept dormant for backwards compat with any future override.

9. **Encoding code pages.** CP1251/KOI8-U don't load in .NET Core by default. `Sho.Extractors/EncodingBootstrapper.cs` registers `CodePagesEncodingProvider` via `[ModuleInitializer]` so any consumer of `Encoding.GetEncoding(1251)` works without manual setup.

10. **NPOI 2.7+ dropped HWPF.** Legacy `.doc` extraction uses NPOI POIFS to read the OLE compound, then a heuristic UTF-16 LE printable-run scanner over the `WordDocument` stream — good enough for substring search, no formatting fidelity. Pinned NPOI 2.5.6 because POIFS API there matches our usage.

11. **Mammoth 1.7 alpha-only.** Public stable that NuGet resolves to is 1.8.0 (with vuln) → 1.11.0 (clean). Pin 1.11.0.

12. **Lucene CodePagesEncodingProvider also needed at runtime** when reading Cyrillic content (already wired by EncodingBootstrapper).

13. **`UkrainianMorfologikAnalyzer` is sealed.** Cannot subclass to add a final `LowerCaseFilter` (its dict-cased lemmas otherwise break case-sensitive Wildcard / Fuzzy queries). Wrap via `Lucene.Net.Analysis.AnalyzerWrapper` instead — see `LowerUkrainianAnalyzer`.

14. **`Progress<T>` callbacks marshal back to the captured SynchronizationContext.** If the heavy work runs on the UI thread (after `await Task.Yield()` continuations resume on UI), every progress report queues behind the work and the status bar looks frozen until the search finishes. `IndexedSearchEngine.SearchAsync` wraps Lucene + per-doc loop in `Task.Run` so reports actually flush to the UI mid-search.

15. **Bulk-update `ObservableCollection` carefully.** Adding 5000 hits one by one fires CollectionChanged 5000× — DataGrid recomputes layout each time and the dispatcher locks long enough for Windows to flag "(Not Responding)" in the title. Use `BulkObservableCollection<T>.ReplaceAll` (single Reset notification) for search results.

16. **Lucene `FuzzyQuery` defaults.** We use `maxEdits=1` everywhere (was 2 originally). Fuzzy=2 is too lenient for proper-noun search — Levenshtein("ніколаєнка","ніколаєва") = 2 → false positives. Lemma branch + dict already covers real UA inflections.

## Conventions

- **Brevity in commits.** Subject ≤72 chars, focused on the *why* (root cause, not just "fix bug"). Trailing `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>`.
- **No emojis in code or commits** unless the user explicitly asks. (User guide does use occasional emoji for clarity, that's fine.)
- **Tests first when feasible.** Adding a search-engine knob → add a test under `tests/Sho.Tests`.
- **Localize all user-visible strings.** Add to both `Strings.uk.xaml` and `Strings.en.xaml`. Bindings use `{DynamicResource Str.Foo}` so language toggle is hot.
- **App icon** — runtime icon (`Window.Icon` / `TitleBar.Icon`) is rendered programmatically by `AppIconFactory` from a `SymbolIcon`. A multi-resolution `app.ico` (16/24/32/48/64/128/256, PNG-payload) is committed at `src/Sho.App/Resources/app.ico` and embedded as the Win32 ApplicationIcon (so desktop shortcuts and Explorer thumbnails show the icon without launching). To regenerate: `Shozilla.exe --write-icon src\Sho.App\Resources\app.ico` (same SymbolIcon render pipeline).

## Where things live

| Concern | File(s) |
|---|---|
| Substring line/snippet matching (brute-force) | `src/Sho.Core/Text/LineMatcher.cs` |
| Lemma-aware line/snippet matching (indexed, group-AND) | `src/Sho.Search/MorphologicalLineMatcher.cs` |
| UA analyzer with lowercase lemmas | `src/Sho.Search/Analysis/LowerUkrainianAnalyzer.cs` |
| Index path hashing | `src/Sho.Core/IO/PathHasher.cs` |
| Brute-force search | `src/Sho.Search/BruteForceSearchEngine.cs` |
| Lucene search + index + UA analyzer + sync | `src/Sho.Search/IndexedSearchEngine.cs` |
| Recursive file enumerator | `src/Sho.Search/FileScanner.cs` |
| FileSystemWatcher / incremental update | `src/Sho.App/Services/IndexWatcherService.cs` |
| Format extractors | `src/Sho.Extractors/*.cs` (Plain, Pdf, Docx, Xlsx, Pptx, Rtf, Html, Doc, Xls) |
| Encoding registration | `src/Sho.Extractors/EncodingBootstrapper.cs` |
| MVVM ViewModel | `src/Sho.App/ViewModels/MainViewModel.cs` |
| Bulk-replace ObservableCollection | `src/Sho.App/ViewModels/BulkObservableCollection.cs` |
| Main UI | `src/Sho.App/MainWindow.xaml` + `.cs` |
| Folder tree picker | `src/Sho.App/Views/FolderPickerWindow.xaml*` + `FolderTreeNode.cs` |
| DOCX preview HTML builder | `src/Sho.App/Services/DocxPreviewService.cs` |
| App icon (programmatic + .ico writer) | `src/Sho.App/Services/AppIconFactory.cs` |
| OS file-icon cache | `src/Sho.App/Services/FileIconCache.cs` |
| Settings persistence | `src/Sho.App/Services/SettingsService.cs` |
| Localization service | `src/Sho.App/Services/LocalizationService.cs` |
| Resource dictionaries | `src/Sho.App/Resources/Strings.{uk,en}.xaml` |
| Value converters | `src/Sho.App/Converters/Converters.cs` |
| WPF/WinForms aliases | `src/Sho.App/Usings.cs` |
| Bundled Win32 .ico | `src/Sho.App/Resources/app.ico` |

## Things consciously NOT done (potential follow-ups)

- **OCR.** Scanned PDFs not indexed.
- **`.doc` formatting.** UTF-16 strings extraction only, not full HWPF.
- **Preview for non-DOCX.** PDF would need PdfPig-rendered images or WebView2 PDF viewer; deferred.
