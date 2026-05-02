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
- **Lucene.NET 4.8.0-beta00017** + `QueryParsers.Classic`.
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
│   └── Sho.Tests/         xUnit. 40 tests, ~2 s. Covers LineMatcher, extractors,
│                          QueryParser, both engines incl. multi-root.
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
- `IIndexedSearchEngine` adds `BuildIndexAsync`, `IsIndexBuiltAsync`, `DeleteIndexAsync`, `GetIndexMetadataAsync`.
- `SearchQuery` = `{ Text, CaseSensitive, WholeWord, ContextChars=80, MaxLineHits=5000 }`.
- `SearchResult` = `{ Files: FileSummary[], Lines: SearchHit[], TotalFilesScanned, FilesMatched, TotalHits, Elapsed, Error? }`.
- `IndexMetadata` = `{ BuiltUtc, DocumentCount, RootFolders }` — written as `meta.json` inside the index folder.
- `IndexProgress` = `{ FilesProcessed, FilesTotal, CurrentFile?, Stage?, ElapsedMs }`.

## Search semantics

- **Brute-force** searches every supported file under every root, scans content with `LineMatcher.FindHits`. Roots are deduped (`BruteForceSearchEngine.DedupeRoots`) so overlapping ancestors don't double-count.
- **Indexed** stores text in a Lucene index whose folder is named by `PathHasher.Hash(IReadOnlyList<string>)` (sorted + normalized + sha256). Same set of roots → same index. Different set → different folder.
- Query parsing in `IndexedSearchEngine.BuildLuceneQuery`:
  - If the query contains any of `+ - " ( ) * ? ~ ^ : \ [ ] { }` or the words `AND`/`OR`/`NOT`, it goes through Lucene's `QueryParser`.
  - Otherwise: 1 bare token → `WildcardQuery("*term*")`, multiple bare tokens → `PhraseQuery`.
  - Parser results are passed through `WildcardifySingleTerms` so `"single-word"` quoted searches still match inflected forms (e.g., `"Ніколаенк"` matches indexed `Ніколаенка`).
- After Lucene picks documents, `ExtractMatcherTerms(query)` extracts the literal substrings we used and re-runs `LineMatcher` on each so the Lines panel and the DOCX preview's `<mark>` highlights line up with the user's intent.

## Index storage

`%LOCALAPPDATA%\sho\indexes\<sha256-prefix-of-roots>\` — Lucene segment files + `meta.json`.

## Settings storage

`%LOCALAPPDATA%\sho\settings.json` — see `Sho.App.Services.AppSettings`:
```
{ Theme, Language, LastFolder?, LastFolders[], ShowPreview, QueryHistory[] }
```

## Preview pipeline

1. `Sho.App.Services.DocxPreviewService.Render(filePath, terms, darkTheme)` writes a complete HTML page to `%LOCALAPPDATA%\sho\preview\<hash>.html` and returns the path.
2. The HTML is body produced by Mammoth + a self-contained `<script>` that walks text nodes and wraps matched terms in `<mark id="m0..N">`, then `scrollIntoView` to `#m0`.
3. `MainWindow` registers a virtual host map: `https://sho-preview/` → preview directory. Nav uses `https://sho-preview/<hash>.html?t=<utc-ticks>` (timestamp busts WebView2's cache so re-rendering with new highlights always reloads).
4. `NavigationCompleted` ignores `OperationCanceled` and `ConnectionAborted` (they fire on rapid row clicks where one nav cancels another) and only surfaces real errors as red text over the WebView.

## Critical gotchas

These cost real time to debug — keep them in mind:

1. **Drive-letter paths on Windows.** `Directory.EnumerateDirectories("C:")` returns the children of the *current directory on C:*, not the root. Always use `"C:\"` (with trailing separator) for drive roots. `FolderPickerWindow.BuildRoots` keeps the trailing slash; `FolderTreeNode.Load` defensively appends one if it sees a 2-char `X:` path.

2. **`RowDefinition` / `ColumnDefinition` don't inherit DataContext.** They're `Freezable`, not `FrameworkElement`. Bindings like `<RowDefinition Height="{Binding Foo, ...}"/>` silently never resolve. The preview-off row collapse is managed from code-behind (`UpdatePreviewLayout`) for this reason.

3. **WebView2 + `file://`.** Direct `file:///C:/...` URLs are blocked in WebView2. Use `CoreWebView2.SetVirtualHostNameToFolderMapping("sho-preview", dir, Allow)` and navigate to `https://sho-preview/<file>`.

4. **`NavigateToString` size limit (~2 MB).** Mammoth output for a DOCX with images can exceed this; the page silently fails to render. We always write to a file and navigate via the virtual host instead.

5. **WPF/WinForms type conflicts.** Both are referenced by Sho.App, so `Application`, `MessageBox`, `Clipboard`, `Brushes`, `Binding`, `HorizontalAlignment`, `Color`, `Size` are ambiguous. `Sho.App/Usings.cs` aliases the WPF versions globally; in code that hits ones not aliased (e.g., `Color`, `Size`, `HorizontalAlignment` in `AppIconFactory`), use fully-qualified `System.Windows.*`.

6. **Wpf.Ui Card centers content when child is short.** When the inner DataGrid has 0 items, the section header TextBlock floats vertically. Results-area frames use plain `<Border>` + `<DockPanel LastChildFill="True">` instead of `<ui:Card>` to anchor headers at top regardless of content height. The Settings card stays as `<ui:Card>` because all its children are auto-sized.

7. **Wpf.Ui style override loses theming.** `<Style TargetType="ui:Button">` without `BasedOn={StaticResource {x:Type ui:Button}}` replaces the entire Fluent style and you get plain WPF chrome. Always BasedOn when overriding Wpf.Ui control styles.

8. **High-contrast results.** App-level `ResultsBackground` / `ResultsForeground` brushes are mutated in code on theme switch (`UpdateContrastBrushes`) — pure black/white in dark, white/black in light. DataGrid uses `{DynamicResource ...}` so they re-pick on every change.

9. **Encoding code pages.** CP1251/KOI8-U don't load in .NET Core by default. `Sho.Extractors/EncodingBootstrapper.cs` registers `CodePagesEncodingProvider` via `[ModuleInitializer]` so any consumer of `Encoding.GetEncoding(1251)` works without manual setup.

10. **NPOI 2.7+ dropped HWPF.** Legacy `.doc` extraction uses NPOI POIFS to read the OLE compound, then a heuristic UTF-16 LE printable-run scanner over the `WordDocument` stream — good enough for substring search, no formatting fidelity. Pinned NPOI 2.5.6 because POIFS API there matches our usage.

11. **Mammoth 1.7 alpha-only.** Public stable that NuGet resolves to is 1.8.0 (with vuln) → 1.11.0 (clean). Pin 1.11.0.

12. **Lucene CodePagesEncodingProvider also needed at runtime** when reading Cyrillic content (already wired by EncodingBootstrapper).

## Conventions

- **Brevity in commits.** Subject ≤72 chars, focused on the *why* (root cause, not just "fix bug"). Trailing `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>`.
- **No emojis in code or commits** unless the user explicitly asks. (User guide does use occasional emoji for clarity, that's fine.)
- **Tests first when feasible.** Adding a search-engine knob → add a test under `tests/Sho.Tests`.
- **Localize all user-visible strings.** Add to both `Strings.uk.xaml` and `Strings.en.xaml`. Bindings use `{DynamicResource Str.Foo}` so language toggle is hot.
- **No `.ico` file in repo** — `AppIconFactory` renders the icon programmatically from a `SymbolIcon` and assigns to both `Window.Icon` (taskbar / Alt-Tab) and `TitleBar.Icon` (favicon). If the user asks for a desktop-shortcut icon, an .ico will need to be generated.

## Where things live

| Concern | File(s) |
|---|---|
| Line/snippet matching | `src/Sho.Core/Text/LineMatcher.cs` |
| Index path hashing | `src/Sho.Core/IO/PathHasher.cs` |
| Brute-force search | `src/Sho.Search/BruteForceSearchEngine.cs` |
| Lucene search + index | `src/Sho.Search/IndexedSearchEngine.cs` |
| Recursive file enumerator | `src/Sho.Search/FileScanner.cs` |
| Format extractors | `src/Sho.Extractors/*.cs` (Plain, Pdf, Docx, Xlsx, Pptx, Rtf, Html, Doc, Xls) |
| Encoding registration | `src/Sho.Extractors/EncodingBootstrapper.cs` |
| MVVM ViewModel | `src/Sho.App/ViewModels/MainViewModel.cs` |
| Main UI | `src/Sho.App/MainWindow.xaml` + `.cs` |
| Folder tree picker | `src/Sho.App/Views/FolderPickerWindow.xaml*` + `FolderTreeNode.cs` |
| DOCX preview HTML builder | `src/Sho.App/Services/DocxPreviewService.cs` |
| App icon (programmatic) | `src/Sho.App/Services/AppIconFactory.cs` |
| OS file-icon cache | `src/Sho.App/Services/FileIconCache.cs` |
| Settings persistence | `src/Sho.App/Services/SettingsService.cs` |
| Localization service | `src/Sho.App/Services/LocalizationService.cs` |
| Resource dictionaries | `src/Sho.App/Resources/Strings.{uk,en}.xaml` |
| Value converters | `src/Sho.App/Converters/Converters.cs` |
| WPF/WinForms aliases | `src/Sho.App/Usings.cs` |

## Things consciously NOT done (potential follow-ups)

- **Ukrainian/Russian morphology in indexed search.** Currently substring-only. Adding `MorfologikAnalyzer` or Hunspell would need a fresh index.
- **Incremental re-indexing.** `BuildIndex` is always full-recreate (`OpenMode.CREATE`). A `FileSystemWatcher` + `IndexWriter.UpdateDocument` path is sketched in dev guide.
- **OCR.** Scanned PDFs not indexed.
- **`.doc` formatting.** UTF-16 strings extraction only, not full HWPF.
- **Real `.ico` for desktop shortcuts.** App's runtime icon is programmatic.
- **Preview for non-DOCX.** PDF would need PdfPig-rendered images or WebView2 PDF viewer; deferred.
