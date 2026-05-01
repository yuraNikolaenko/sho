# sho — посібник розробника

## Стек

- **.NET 9** (TFM `net9.0` для бібліотек, `net9.0-windows` для UI)
- **WPF** + **WindowsForms** (для `FolderBrowserDialog`)
- **CommunityToolkit.Mvvm** (`ObservableObject`, `[RelayCommand]`)
- **Lucene.NET 4.8** (індексний пошук)
- **PdfPig** (PDF), **DocumentFormat.OpenXml** (DOCX/XLSX/PPTX)
- **NPOI 2.5.6** (XLS через HSSF, OLE compound storage через POIFS для DOC)
- **UTF.Unknown** (детект кодування), **System.Text.Encoding.CodePages** (CP1251 та інші)

## Структура рішення

```
Sho.sln
├── src/
│   ├── Sho.Core/           — POCO, інтерфейси, утиліти. Без UI/IO-залежностей.
│   ├── Sho.Extractors/     — IFileTextExtractor + конкретні реалізації по форматах.
│   ├── Sho.Search/         — Дві реалізації ISearchEngine:
│   │                          BruteForceSearchEngine — паралельне сканування.
│   │                          IndexedSearchEngine    — Lucene.NET.
│   └── Sho.App/            — WPF MVVM. View ↔ MainViewModel.
└── tests/
    └── Sho.Tests/          — xUnit. LineMatcher, RTF, plain text, обидва engine.
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
- [`ISearchEngine`](../src/Sho.Core/Abstractions/ISearchEngine.cs) — `SearchAsync(root, query, progress, ct) → SearchResult`.
- [`IIndexedSearchEngine`](../src/Sho.Core/Abstractions/ISearchEngine.cs) — додає `BuildIndexAsync`, `IsIndexBuiltAsync`, `DeleteIndexAsync`.

## Моделі — [src/Sho.Core/Models/](../src/Sho.Core/Models/)

- [`SearchQuery`](../src/Sho.Core/Models/SearchQuery.cs) — `Text`, `CaseSensitive`, `WholeWord`, `ContextChars=80`, `MaxLineHits=5000`.
- [`SearchHit`](../src/Sho.Core/Models/SearchHit.cs) — один збіг: `FilePath`, `LineNumber`, `LineText`, `MatchStart`, `MatchLength`, `Snippet` (рядок з ±ContextChars контексту).
- [`FileSummary`](../src/Sho.Core/Models/FileSummary.cs) — рядок таблиці файлів: шлях, кількість збігів, розмір, mtime.
- [`SearchResult`](../src/Sho.Core/Models/SearchResult.cs) — агрегат: `Files`, `Lines`, `Elapsed`, `Error`.
- [`IndexProgress`](../src/Sho.Core/Models/IndexProgress.cs) — для `IProgress<T>` (frac, current file, stage).

## Алгоритм збігу рядків — [LineMatcher.cs](../src/Sho.Core/Text/LineMatcher.cs)

`FindHits(filePath, text, query)` йде по тексту посимвольно, тримаючи `lineStart`/`lineNumber`. Для кожного рядка викликає `text.IndexOf(query.Text, comparison)` у вікні `[lineStart, lineEnd)` доти, поки знаходить. Підтримує `\n` і `\r\n`.

Важливо: повертає `IEnumerable<SearchHit>` через `yield return`, тож не використовує `Span<char>` (CS4007).

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

1. [`FileScanner.Enumerate`](../src/Sho.Search/FileScanner.cs) рекурсивно віддає `DocumentRef` для всіх файлів із підтримуваними розширеннями.
2. `Parallel.ForEachAsync` (степінь = `Environment.ProcessorCount`) для кожного файлу:
   - резолвить екстрактор → `ExtractAsync` → `LineMatcher.FindHits`;
   - акумулює `ConcurrentBag<SearchHit>` і `ConcurrentDictionary<path, hitCount>`;
   - звітує прогрес кожні 32 файли.
3. У кінці сортує файли за `HitCount desc`, рядки за `(FilePath, LineNumber)`.

### Indexed — [IndexedSearchEngine.cs](../src/Sho.Search/IndexedSearchEngine.cs)

- Індекс зберігається в `%LOCALAPPDATA%\sho\indexes\<sha256-prefix-of-rootpath>\` ([PathHasher](../src/Sho.Core/IO/PathHasher.cs)).
- **BuildIndex:** для кожного файлу `IFileTextExtractor.ExtractAsync` → один Lucene `Document` із полями `path`, `ext`, `size`, `mtime`, `content` (стримний `TextField` для пошуку + `Store.YES` щоб дістати текст і зробити снипети без перечитування файлу).
- Аналізатор: `StandardAnalyzer (LUCENE_48)`.
- **Search:**
  - один термін → `WildcardQuery("*term*")` (працює як substring-пошук, прийнятно для імен).
  - кілька термінів → `PhraseQuery` (точний порядок).
  - Бере `topDocs.ScoreDocs`, для кожного дістає `content` зі store і прогонить через `LineMatcher.FindHits` → отримує точні позиції в рядках і снипети.

### Чому два двигуни?

- BruteForce — нульове налаштування, корисний для разових пошуків і fallback, якщо індекс зламано.
- Indexed — швидкість при повторних пошуках по одній і тій же папці.

## UI — [src/Sho.App/](../src/Sho.App/)

- [`MainViewModel`](../src/Sho.App/ViewModels/MainViewModel.cs) — `ObservableObject` із `[RelayCommand]` для `SelectFolder`, `BuildIndex`, `DeleteIndex`, `Search`, `Cancel`. Тримає `CancellationTokenSource` під час операції, оновлює `IsBusy`/`StatusText`/`ProgressFraction` через `IProgress<IndexProgress>`.
- [`MainWindow.xaml`](../src/Sho.App/MainWindow.xaml) — three-row layout: folder bar, search bar, options bar; результати в `Grid` з `GridSplitter` (DataGrid файлів | DataGrid рядків); статус-бар + прогрес-бар.
- [`MainWindow.xaml.cs`](../src/Sho.App/MainWindow.xaml.cs) — мінімум code-behind: тільки `MouseDoubleClick` і `ContextMenu` handlers, які делегують до VM.
- [`Converters.cs`](../src/Sho.App/Converters/Converters.cs) — bool→Visibility, path→FileName, bool→Brush, bool→текст статусу індексу.
- [`Usings.cs`](../src/Sho.App/Usings.cs) — глобальні аліаси (Application/MessageBox/Clipboard/Brushes/Binding) щоб уникнути `CS0104` між WPF і WinForms.

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

## Тести

```powershell
dotnet test tests\Sho.Tests\Sho.Tests.csproj
```

Покриті:
- `LineMatcher` — позиції збігів, регістр, whole-word, snippet, CRLF.
- `PlainTextExtractor` — UTF-8 BOM, CP1251.
- `RtfTextExtractor.StripRtf` — текст, unicode-escape, ігнор destinations.
- `BruteForceSearchEngine` і `IndexedSearchEngine` — реальні файли + Lucene-індекс у tmp-папці.

`InternalsVisibleTo` для `Sho.Tests` оголошено в [Sho.Extractors.csproj](../src/Sho.Extractors/Sho.Extractors.csproj).

## Як додати новий формат

1. Імплементуй `IFileTextExtractor` у `Sho.Extractors`.
2. Додай розширення в `PlainTextExtractor.Extensions` АБО створи новий екстрактор та додай у [`TextExtractorRegistry.CreateDefaults()`](../src/Sho.Extractors/TextExtractorRegistry.cs).
3. Додай розширення у `TextExtractorRegistry.DefaultExtensions` (це фільтр `FileScanner`).
4. Напиши тест.

## Як додати морфологію (наприклад, українську)

1. Підключи `Lucene.Net.Analysis.Morfologik` (`MorfologikAnalyzer`) або кастомний `Analyzer` з `HunspellStemFilter` + uk-словник.
2. Заміни `new StandardAnalyzer(LV)` в [IndexedSearchEngine.BuildIndexAsync](../src/Sho.Search/IndexedSearchEngine.cs) на новий аналізатор.
3. Перебудуй індекс (`Delete index` → `Build index`).

## Відомі обмеження

- Немає інкрементального оновлення індексу. Re-index — повна переіндексація. Можна додати через `IndexWriter.UpdateDocument(new Term("path", ...), doc)` + `FileSystemWatcher` або mtime-діф.
- `.doc` парсер — евристичний (UTF-16 runs з OLE-стріму). Може пропустити дуже дрібні слова та лишити дрібні артефакти метаданих у сніпетах. Достатньо для substring-пошуку, недостатньо для рендерингу.
- Немає OCR. Скановані PDF не індексуються.
- `WildcardQuery` із `*term*` не використовує prefix-індекс — повільний на дуже великих корпусах. Альтернатива — `NGramTokenizer`.
