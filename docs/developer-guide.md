# Shozilla — посібник розробника

## Стек

- **.NET 9** (TFM `net9.0` для бібліотек, `net9.0-windows` для UI)
- **WPF** + **WindowsForms** (включений у Sho.App для деяких діалогів та CodePages інтеропу)
- **WPF-UI 4.0.2** (Lepoco) — Fluent / Windows 11 chrome (`FluentWindow`, `TitleBar`, `Card`, `SymbolIcon`, themed `Button` / `TextBox` / `ToggleSwitch` / `ToggleButton`)
- **CommunityToolkit.Mvvm 8.4.0** (`ObservableObject`, `[RelayCommand]`)
- **Lucene.NET 4.8.0-beta00017** + `QueryParsers.Classic` (індексний пошук + парсер запитів)
- **PdfPig**, **DocumentFormat.OpenXml**, **NPOI 2.5.6** (XLS via HSSF, OLE compound storage via POIFS for DOC)
- **UTF.Unknown** (детект кодування), **System.Text.Encoding.CodePages** (CP1251 та інші)
- **Mammoth 1.11.0** (DOCX → HTML для прев'ю)
- **Microsoft.Web.WebView2 1.0.2792.45** (WPF host + рендер прев'ю)

## Структура рішення

```
Sho.sln
├── src/
│   ├── Sho.Core/           — POCO, інтерфейси, утиліти. Без UI/IO-залежностей.
│   ├── Sho.Extractors/     — IFileTextExtractor + конкретні реалізації по форматах.
│   ├── Sho.Search/         — Дві реалізації ISearchEngine:
│   │                          BruteForceSearchEngine — паралельне сканування.
│   │                          IndexedSearchEngine    — Lucene.NET.
│   └── Sho.App/            — WPF MVVM (FluentWindow). AssemblyName=Shozilla.
└── tests/
    └── Sho.Tests/          — xUnit. 40 тестів: LineMatcher, RTF, plain text,
                              QueryParser, обидва двигуни, multi-root.
```

Залежності проектів:
```
Sho.Core ←─── Sho.Extractors ←─── Sho.Search ←─── Sho.App
                  ↑                    ↑              ↑
                  └────── Sho.Tests ───┘              ┘
```

## Ключові абстракції — [src/Sho.Core/Abstractions/](../src/Sho.Core/Abstractions/)

- [`IFileTextExtractor`](../src/Sho.Core/Abstractions/IFileTextExtractor.cs) — `CanHandle(path) → bool` + `ExtractAsync(path, ct) → string`. Повертає plain-text без розмітки.
- [`ITextExtractorRegistry`](../src/Sho.Core/Abstractions/ITextExtractorRegistry.cs) — резолвить екстрактор за розширенням і повертає список підтримуваних розширень.
- [`ISearchEngine`](../src/Sho.Core/Abstractions/ISearchEngine.cs) — `SearchAsync(IReadOnlyList<string> rootFolders, query, progress, ct) → SearchResult`. Multi-root з самого API.
- [`IIndexedSearchEngine`](../src/Sho.Core/Abstractions/ISearchEngine.cs) — додає `BuildIndexAsync`, `IsIndexBuiltAsync`, `DeleteIndexAsync`, `GetIndexMetadataAsync`.

## Моделі — [src/Sho.Core/Models/](../src/Sho.Core/Models/)

- [`SearchQuery`](../src/Sho.Core/Models/SearchQuery.cs) — `Text`, `CaseSensitive`, `WholeWord`, `ContextChars=80`, `MaxLineHits=5000`.
- [`SearchHit`](../src/Sho.Core/Models/SearchHit.cs) — один збіг: `FilePath`, `LineNumber`, `LineText`, `MatchStart`, `MatchLength`, `Snippet` (рядок з ±ContextChars контексту).
- [`FileSummary`](../src/Sho.Core/Models/FileSummary.cs) — рядок таблиці файлів: шлях, кількість збігів, розмір, mtime.
- [`SearchResult`](../src/Sho.Core/Models/SearchResult.cs) — агрегат: `Files`, `Lines`, `Elapsed`, `Error`.
- [`IndexProgress`](../src/Sho.Core/Models/IndexProgress.cs) — для `IProgress<T>` (filesProcessed/Total, currentFile, stage, elapsedMs). Computed `FilesPerSecond`, `Eta`.
- [`IndexMetadata`](../src/Sho.Core/Models/IndexMetadata.cs) — `BuiltUtc`, `DocumentCount`, `RootFolders`. Серіалізується в `meta.json` у папці індексу.

## Алгоритм збігу рядків — [LineMatcher.cs](../src/Sho.Core/Text/LineMatcher.cs)

`FindHits(filePath, text, query)` йде по тексту посимвольно, тримаючи `lineStart`/`lineNumber`. Для кожного рядка викликає `text.IndexOf(query.Text, comparison)` у вікні `[lineStart, lineEnd)` доти, поки знаходить. Підтримує `\n` і `\r\n`.

Важливо: повертає `IEnumerable<SearchHit>` через `yield return`, тож не використовує `Span<char>` (CS4007).

## Хеш кореневих папок — [PathHasher.cs](../src/Sho.Core/IO/PathHasher.cs)

`Hash(string path)` і `Hash(IEnumerable<string> paths)` — нормалізує (FullPath + lowercase + trim trailing slash), сортує, склеює через `|`, SHA256, перші 8 байт у hex. Гарантує: однаковий **набір** папок → один індекс. Зміна набору → інша папка індексу.

## Екстрактори — [src/Sho.Extractors/](../src/Sho.Extractors/)

| Файл                                                                       | Формати          | Як працює                                                |
|---------------------------------------------------------------------------|------------------|-----------------------------------------------------------|
| [PlainTextExtractor.cs](../src/Sho.Extractors/PlainTextExtractor.cs)      | text/code/log    | Читає байти, детектить кодування через UTF.Unknown, декодує. Обмежує читання 64 МБ. |
| [PdfTextExtractor.cs](../src/Sho.Extractors/PdfTextExtractor.cs)          | `.pdf`           | `PdfPig.PdfDocument.Open` → `page.Text` (склеює в `Task.Run` для CPU-роботи).        |
| [DocxTextExtractor.cs](../src/Sho.Extractors/DocxTextExtractor.cs)        | `.docx`          | `WordprocessingDocument` → `Body.Descendants<Paragraph>` → `Text`.                    |
| [XlsxTextExtractor.cs](../src/Sho.Extractors/XlsxTextExtractor.cs)        | `.xlsx`          | `SpreadsheetDocument` + `SharedStringTable` → клітини через `\t`/`\n`.               |
| [PptxTextExtractor.cs](../src/Sho.Extractors/PptxTextExtractor.cs)        | `.pptx`          | `PresentationDocument` → `SlideParts.Slide.Descendants<A.Text>`.                       |
| [HtmlTextExtractor.cs](../src/Sho.Extractors/HtmlTextExtractor.cs)        | `.html` `.htm`   | Регексом видаляє `<script>`/`<style>`, потім всі теги, `WebUtility.HtmlDecode`.        |
| [RtfTextExtractor.cs](../src/Sho.Extractors/RtfTextExtractor.cs)          | `.rtf`           | Власний parser (state-machine, без WinForms): обробляє `{`, `}`, `\\`, `\u N ?`, `\\'XX`, `\\par`/`\\line`/`\\tab`, ігнорує `\\*`-destinations. |
| [DocTextExtractor.cs](../src/Sho.Extractors/DocTextExtractor.cs)          | `.doc`           | NPOI POIFS відкриває OLE compound, читає стрім `WordDocument`. Евристика: збирає run-и UTF-16 LE printable-символів довжиною ≥4 (substring search не потребує точного парсингу WordDocument FIB/CHPX). |
| [XlsTextExtractor.cs](../src/Sho.Extractors/XlsTextExtractor.cs)          | `.xls`           | NPOI `HSSFWorkbook` + ітерація по `Row.Cells`; типи: String / Numeric / Boolean / Formula (cached value).            |
| [TextExtractorRegistry.cs](../src/Sho.Extractors/TextExtractorRegistry.cs)| —                | Резолвить за розширенням.                                                            |
| [EncodingBootstrapper.cs](../src/Sho.Extractors/EncodingBootstrapper.cs)  | —                | `[ModuleInitializer]` реєструє `CodePagesEncodingProvider` (потрібно для CP1251).    |

## Двигуни пошуку — [src/Sho.Search/](../src/Sho.Search/)

### BruteForce — [BruteForceSearchEngine.cs](../src/Sho.Search/BruteForceSearchEngine.cs)

1. `DedupeRoots` — нормалізує набір коренів і прибирає дочірні (якщо в наборі є `C:\Docs` і `C:\Docs\Tax`, другий ігнорується).
2. [`FileScanner.Enumerate`](../src/Sho.Search/FileScanner.cs) рекурсивно віддає `DocumentRef` для всіх файлів із підтримуваними розширеннями для **кожного** кореня; результати дедуплікуються по path (на випадок overlapping коренів попри DedupeRoots).
3. `Parallel.ForEachAsync` (степінь = `Environment.ProcessorCount`) для кожного файлу:
   - резолвить екстрактор → `ExtractAsync` → `LineMatcher.FindHits`;
   - акумулює `ConcurrentBag<SearchHit>` і `ConcurrentDictionary<path, hitCount>`;
   - звітує прогрес **перед** початком обробки файлу (не batch-ом — щоб heartbeat у VM показував поточний файл миттєво).
4. У кінці сортує файли за `HitCount desc`, рядки за `(FilePath, LineNumber)`.

### Indexed — [IndexedSearchEngine.cs](../src/Sho.Search/IndexedSearchEngine.cs)

- Індекс зберігається в `%LOCALAPPDATA%\sho\indexes\<sha256-prefix-of-roots>\`.
- **BuildIndex:** дедуп коренів → enumerate → для кожного файлу `IFileTextExtractor.ExtractAsync` → один Lucene `Document` із полями `path`, `ext`, `size`, `mtime`, `content` (`TextField` для пошуку + `Store.YES` щоб дістати текст для снипетів). Після Commit пише `meta.json` із `IndexMetadata`.
- Аналізатор: `StandardAnalyzer (LUCENE_48)`.
- **BuildLuceneQuery:**
  - Якщо в `query.Text` є оператори `+ - " ( ) * ? ~ ^ : \ [ ] { }` або `AND`/`OR`/`NOT` — пускаємо через `QueryParser` (Lucene-classic) із `AllowLeadingWildcard=true`, `DefaultOperator=AND`.
  - Інакше: 1 термін → `WildcardQuery("*term*")`, 2+ → `PhraseQuery`.
  - Результат QueryParser проходить через **`WildcardifySingleTerms`** — рекурсивно замінює `TermQuery`/`PhraseQuery(1-term)` на `WildcardQuery(*term*)`. Це фіксує UX: `"Ніколаенк"` (з лапками) тепер знаходить `Ніколаенка` так само як bare `Ніколаенк`.
- **Search:**
  - Бере `topDocs.ScoreDocs`, для кожного дістає `content` зі store.
  - Через `ExtractMatcherTerms(query)` витягає чисті підрядки з parsed Lucene query (TermQuery, WildcardQuery без `*`/`?`, PrefixQuery, FuzzyQuery, single-term PhraseQuery, BooleanQuery без MUST_NOT).
  - Прогонить content через `LineMatcher.FindHits` для кожного терма → отримує точні позиції в рядках і снипети, які відповідають саме знайденому в Lucene.

### Чому два двигуни?

- BruteForce — нульове налаштування, корисний для разових пошуків і fallback, якщо індекс зламано.
- Indexed — швидкість при повторних пошуках по одній і тій же папці.

## UI — [src/Sho.App/](../src/Sho.App/)

### Layout

`MainWindow.xaml` — `ui:FluentWindow` із Mica backdrop, `ExtendsContentIntoTitleBar=True`. Тіло — Grid із 4 рядками:

```
TitleBar (із SymbolIcon SearchInfo24 як favicon)
Settings ui:Card із 7-колонковим Grid:
  | Inputs (Find row + Folders row) | sep | Checkboxes | sep | Index buttons | sep | Toolbar (Theme/Lang/Preview) |
Results Grid: Files | splitter | RightColumn (Lines / splitter / Preview)
StatusBar: progress (тонкий 3px над текстом) + StatusText | divider | IndexInfoText
```

Results-фрейми — звичайні `<Border>` (а не `<ui:Card>`) із Fluent-кольорами `CardBackground` / `CardBorderBrush`. Причина: `ui:Card`-template центрує content коли DataGrid має 0 рядків — TextBlock-заголовок плавав посередині. `Border` із `<DockPanel LastChildFill="True">` гарантовано прибиває заголовок до верху незалежно від наповненості.

### Прев'ю DOCX

[DocxPreviewService.cs](../src/Sho.App/Services/DocxPreviewService.cs):
1. Mammoth конвертує DOCX → HTML body (кешується в `Dictionary<filePath, (mtime, html)>`).
2. Кожен виклик `Render` обертає тіло в HTML-сторінку з theme-залежним CSS (B/W фон/текст у Dark, навпаки в Light), і вбудовує `<script>` що проходить text-нодами і обертає кожен термін у `<mark id="m0..N">`.
3. HTML записується у `%LOCALAPPDATA%\sho\preview\<hash>.html` — повертається шлях.

[MainWindow.xaml.cs](../src/Sho.App/MainWindow.xaml.cs):
- На `Loaded` створює WebView2 environment, ініціює CoreWebView2.
- **VirtualHost mapping**: `https://sho-preview/` → preview directory. Прямий `file://` блокується WebView2 за замовчуванням, тож VirtualHost — рекомендований шлях.
- Навігація: `https://sho-preview/<hash>.html?t=<utc-ticks>` — timestamp обходить кеш.
- `NavigationCompleted` ловить помилки, але ігнорує `OperationCanceled` / `ConnectionAborted` (швидке клацання → попередня навігація скасовується).
- При фейлі WebView2 init (відсутній Edge runtime) PreviewError TextBlock накриває WebView2 з посиланням на runtime download.

### Settings persistence

[SettingsService.cs](../src/Sho.App/Services/SettingsService.cs) серіалізує `AppSettings` у `%LOCALAPPDATA%\sho\settings.json`:
```
{ Theme, Language, LastFolder?, LastFolders[], ShowPreview, QueryHistory[] }
```
Зберігається на: зміну теми, мови, ShowPreview-toggle, FolderPaths-collection, QueryHistory-collection.

### Localization

[LocalizationService.cs](../src/Sho.App/Services/LocalizationService.cs) — підключає [Resources/Strings.uk.xaml](../src/Sho.App/Resources/Strings.uk.xaml) або `.en.xaml` у `Application.Resources.MergedDictionaries`. Усі рядки в XAML — `{DynamicResource Str.Foo}`, тож swap міняє інтерфейс миттєво без рестарту.

### Folder picker

[Views/FolderPickerWindow.xaml](../src/Sho.App/Views/FolderPickerWindow.xaml) — окремий `ui:FluentWindow` 520×640. Дерево на основі `Views/FolderTreeNode.cs` (lazy-load дочірніх папок при першому розкритті, placeholder-патерн).

Поведінка:
- Опен з порожнім вибором → всі диски згорнуті.
- Опен із попереднім вибором → розкривається тільки гілка до вибраної папки. Сусіди лишаються згорнутими.
- Чекбокси — незалежні (кожна позначена папка = окремий search-root). OK-handler дедуплікує overlapping ancestors.
- "Зняти всі" — швидке очищення.

### Тулбар (Theme / Language / Preview)

Усі три — `ui:Button` (BasedOn `{x:Type ui:Button}` щоб успадкувати Fluent-стиль) із MinWidth=110:
- Theme: Sun24 / Moon24, click → `ApplicationThemeManager.Apply` + `UpdateContrastBrushes` для DataGrid B/W ресурсів.
- Language: глобус + `UA`/`EN`, click → `LocalizationService.Apply`.
- Preview: Eye24 / EyeOff24 + Appearance Primary/Secondary, click → toggles `ShowPreview` + `UpdatePreviewLayout` (керує row heights програмно бо RowDefinition не успадковує DataContext).

### Іконка

[AppIconFactory.cs](../src/Sho.App/Services/AppIconFactory.cs) — `RenderTargetBitmap(64×64)` із `SymbolIcon(SearchInfo24)` помаранчевим `#FF5722` на темному фоні `#1A1A1A`. Призначається обом вікнам як `Window.Icon`. TitleBar.Icon — окремий `<ui:SymbolIcon>` у XAML. Файлу `.ico` немає — все рантайм.

### File icons у таблиці

[FileIconCache.cs](../src/Sho.App/Services/FileIconCache.cs) — `SHGetFileInfo(SHGFI_USEFILEATTRIBUTES | SHGFI_SMALLICON)` повертає OS-зареєстровану іконку для розширення. Кешується по розширенню. Конвертер `PathToIconConverter` біндить у DataGridTemplateColumn.

### MVVM

[MainViewModel.cs](../src/Sho.App/ViewModels/MainViewModel.cs):
- Стан: `FolderPaths`, `QueryText`, прапорці, `Files`/`Lines` колекції, `SelectedFile`, `SelectedHit`, `ShowPreview`, `PreviewFilePath`, `IsBusy`, `StatusText`, `IndexInfoText`, `QueryHistory`.
- Команди: `BuildIndexAsync`, `DeleteIndexAsync`, `SearchAsync`, `Cancel`, `ClearFileFilter`.
- `RememberQuery` додає вдалий пошук у `QueryHistory` (deup, cap=50). Code-behind перехоплює `CollectionChanged` і записує в settings.
- `RefreshIndexStateAsync` бере `IndexExists` + `IndexMetadata` і збирає `IndexInfoText` (`Index · 2025-04-30 14:32 · 12,453 files` тощо).
- `DispatcherTimer` 500мс heartbeat для статусу під час busy — показує `(45s on this file)` навіть коли engine не репортить.

## Збірка та запуск

```powershell
dotnet restore
dotnet build Sho.sln
dotnet run --project src\Sho.App\Sho.App.csproj
```

Single-file публікація:
```powershell
dotnet publish src\Sho.App\Sho.App.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Exe = `Shozilla.exe`.

## Тести

```powershell
dotnet test tests\Sho.Tests\Sho.Tests.csproj
```

40 тестів, ~2 с. Покриті:
- `LineMatcher` — позиції збігів, регістр, whole-word, snippet, CRLF.
- `PlainTextExtractor` — UTF-8 BOM, CP1251.
- `RtfTextExtractor.StripRtf` — текст, unicode-escape, ігнор destinations.
- `DocTextExtractor.ExtractRuns` — UTF-16 LE run extraction, threshold filtering.
- `XlsTextExtractor` — реальний XLS зі строками + числами + Unicode.
- `IndexedSearchEngine` query parser — операторні детекти, wildcardification quotied single-term, AND/OR clauses.
- `BruteForceSearchEngine` + `IndexedSearchEngine` — реальні файли + Lucene-індекс у tmp-папці; multi-root з overlapping ancestors дедуплікується.

`InternalsVisibleTo` для `Sho.Tests` оголошено в [Sho.Extractors.csproj](../src/Sho.Extractors/Sho.Extractors.csproj) і [Sho.Search.csproj](../src/Sho.Search/Sho.Search.csproj).

## Як додати новий формат

1. Імплементуй `IFileTextExtractor` у `Sho.Extractors`.
2. Додай розширення в `PlainTextExtractor.Extensions` АБО створи новий екстрактор та додай у [`TextExtractorRegistry.CreateDefaults()`](../src/Sho.Extractors/TextExtractorRegistry.cs).
3. Додай розширення у `TextExtractorRegistry.DefaultExtensions` (це фільтр `FileScanner`).
4. Напиши тест.

## Як додати морфологію (наприклад, українську)

1. Підключи `Lucene.Net.Analysis.Morfologik` (`MorfologikAnalyzer`) або кастомний `Analyzer` з `HunspellStemFilter` + uk-словник.
2. Заміни `new StandardAnalyzer(LV)` в [IndexedSearchEngine.BuildIndexAsync](../src/Sho.Search/IndexedSearchEngine.cs) на новий аналізатор.
3. Перебудуй індекс (`Delete index` → `Build index`).

## Як додати нову мову

1. Скопіюй [Strings.uk.xaml](../src/Sho.App/Resources/Strings.uk.xaml) → `Strings.<lang>.xaml`, переклади значення.
2. Додай код у [`LocalizationService.AvailableLanguages`](../src/Sho.App/Services/LocalizationService.cs) і у `LanguageButton_Click` (наразі toggle uk↔en — переробити на cycle через список).
3. Жодних code-змін у форматуванні строк не треба — `{DynamicResource}` робить hot-swap.

## Critical gotchas

Дивись також [CLAUDE.md](../CLAUDE.md) — там зібраний повний список. Найголовніші:

1. **`"C:"` ≠ `"C:\"`** — без бекслеша означає CWD на диску, не корінь.
2. **`RowDefinition` / `ColumnDefinition`** не успадковують DataContext (Freezable). Bind на Height не спрацює — керуй з code-behind.
3. **WebView2 + `file://`** — заблоковано. Використовуй `SetVirtualHostNameToFolderMapping`.
4. **NavigateToString ~2МБ ліміт** — для DOCX з картинками не вистачає. Пиши у файл і нав до virtual-host URL.
5. **`<Style TargetType="ui:Button">` без `BasedOn={x:Type ui:Button}`** перезаписує Fluent-стиль повністю → плейн WPF.

## Відомі обмеження

- Немає інкрементального оновлення індексу. Re-index — повна переіндексація. Можна додати через `IndexWriter.UpdateDocument(new Term("path", ...), doc)` + `FileSystemWatcher` або mtime-діф.
- `.doc` парсер — евристичний (UTF-16 runs з OLE-стріму). Може пропустити дрібні слова та лишити дрібні артефакти метаданих у сніпетах. Достатньо для substring-пошуку, недостатньо для рендерингу.
- Немає OCR. Скановані PDF не індексуються.
- `WildcardQuery` із `*term*` не використовує prefix-індекс — повільний на дуже великих корпусах. Альтернатива — `NGramTokenizer`.
- Прев'ю тільки для `.docx`. Для PDF знадобиться рендеринг через PdfPig pages → image, або вбудований PDF-viewer WebView2.
