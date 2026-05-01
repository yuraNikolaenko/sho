# Стек-незалежний промт для AI: портування `sho` на інший стек

Скопіюй усе нижче (від рядка `=== BEGIN PROMPT ===`) і додай до запиту до AI разом із цільовим стеком ("портуй на Electron + TypeScript", "на Python + PyQt", "на Go + Fyne" тощо).

---

```
=== BEGIN PROMPT ===
Ти — AI-помічник з портування десктопного застосунку «sho» з .NET 9/WPF на інший
стек/мову. Збережи функціональність 1:1, заміни лише імплементаційні залежності.
Нижче — нейтральний опис застосунку і його контракти.

ЦІЛЬОВИЙ СТЕК: <ВПИШИ_СЮДИ>      # напр., "Electron + TypeScript + React"
ЦІЛЬОВА ОС:    <ВПИШИ_СЮДИ>      # напр., "Windows + macOS"
БІБЛІОТЕКИ:    <за бажанням>     # напр., "MeiliSearch замість Lucene"

============================================================================
ЩО ЦЕ
============================================================================
Десктопна програма для пошуку текстових збігів у документах усередині
обраної папки (рекурсивно). Два режими: (1) перебірне сканування та
(2) пошук по заздалегідь побудованому повнотекстовому індексу.

============================================================================
СЦЕНАРІЇ
============================================================================
S1. Користувач натискає «Browse» → вибирає кореневу папку.
S2. (Опційно) натискає «Build index» → програма обходить усі підтримувані
    файли, дістає plain-text і складає у локальний повнотекстовий індекс
    у user-local cache (наприклад, %LOCALAPPDATA% / ~/.cache).
S3. У полі пошуку вводить запит ("Ніколаенко" або "Ніколаенко Юрій
    Вікторович") і натискає Enter.
S4. Програма повертає два списки одночасно:
    a) Files: унікальні файли, де є збіг, з кількістю збігів і метаданими
       (path, ext, size, mtime).
    b) Lines: усі окремі рядки з документів, що містять збіг, із
       контекстом ±N символів навколо збігу для розуміння контексту.
S5. Подвійний клік на елементі будь-якого списку → відкриває документ
    у програмі ОС за замовчуванням (Word/PDF reader/тощо).
S6. Контекстне меню: Open, Reveal in file manager, Copy path.
S7. Прогрес довгих операцій + кнопка Cancel.

============================================================================
КОНТРАКТИ ДОМЕНУ (мова-незалежно)
============================================================================
SearchQuery:
  text:           string  (непорожній)
  caseSensitive:  bool    (default false)
  wholeWord:      bool    (default false)
  contextChars:   int     (default 80)
  maxLineHits:    int     (default 5000)

SearchHit:
  filePath:    string
  lineNumber:  int (1-based)
  lineText:    string
  matchStart:  int (offset у lineText)
  matchLength: int
  snippet:     string (текст ±contextChars навколо збігу,
                       з \r\n\t заміненими на пробіли)

FileSummary:
  filePath, fileName, hitCount, sizeBytes, modifiedUtc, extension

SearchResult:
  files: FileSummary[]              # відсортовано за hitCount desc
  lines: SearchHit[]                # відсортовано за (path, lineNumber)
  totalFilesScanned, filesMatched, totalHits: int
  elapsed: duration
  error: string | null

IndexProgress:
  filesProcessed, filesTotal: int
  currentFile, stage: string | null

ITextExtractor (інтерфейс плагінів формату):
  canHandle(path) -> bool
  extractAsync(path, cancelToken) -> string   # plain-text без розмітки

ISearchEngine:
  name -> string
  searchAsync(rootFolder, query, progress, ct) -> SearchResult

IIndexedSearchEngine extends ISearchEngine:
  buildIndexAsync(rootFolder, progress, ct)
  isIndexBuiltAsync(rootFolder, ct) -> bool
  deleteIndexAsync(rootFolder, ct)

============================================================================
АЛГОРИТМ ЗБІГУ РЯДКА (детермінований, обов'язковий)
============================================================================
function findHits(text, query) -> Iterable<SearchHit>:
  comparison = caseSensitive ? CASE_SENSITIVE : CASE_INSENSITIVE
  розбий text на рядки за \n; CRLF: відрізай хвостовий \r
  для кожного рядка з номером n (1-based):
    позиція = початок_рядка
    while позиція < кінець_рядка:
      idx = indexOf(text, query.text, позиція, кінець_рядка, comparison)
      if idx < 0: break
      if wholeWord та сусідні символи — літери/цифри/_: позиція = idx + 1; continue
      yield SearchHit(
        lineNumber = n,
        lineText   = text[початок_рядка .. кінець_рядка],
        matchStart = idx - початок_рядка,
        matchLength = len(query.text),
        snippet = text[max(0,idx-N)..min(len,idx+len(q)+N)]
                  з \r\n\t -> пробіл,
      )
      повернено += 1; if повернено >= maxLineHits: stop
      позиція = idx + max(1, len(query.text))

============================================================================
ПІДТРИМУВАНІ ФОРМАТИ ТА ЕКСТРАКТОРИ
============================================================================
- Звичайний текст / код / log:
    .txt .md .csv .tsv .log .xml .json .yaml .yml .ini .cfg .conf .properties
    .env .cs .js .ts .py .rb .go .rs .java .kt .c .cpp .h .hpp .sql .sh
    .ps1 .bat .cmd .css .scss .less .tex
  Імплементація: читай байти, детектуй кодування (UTF-8 BOM, UTF-16,
  CP1251, KOI8-U, ISO-8859-*) — використовуй бібліотеку детекту chardet/uchardet
  або еквівалент. Обмежуй читання 64 МБ на файл.
- HTML: .html .htm .xhtml — видали <script>/<style>, потім всі теги,
  HTML-decode сутності.
- PDF: .pdf — текстовий шар (без OCR). У JS — pdfjs-dist; у Python —
  pdfminer.six; у Go — ledongthuc/pdf; тощо.
- DOCX: .docx — поточний XML у word/document.xml; брати <w:t> текст.
- XLSX: .xlsx — sharedStrings.xml + sheetN.xml; для cell з t="s" брати
  sst[index]; інакше cell.value. Розділяй клітинки \t, рядки \n.
- PPTX: .pptx — у кожному ppt/slides/slideN.xml брати <a:t> текст.
- DOC (legacy): .doc — OLE compound storage. Прочитати стрім "WordDocument"
  (fallback: "1Table"/"0Table"). Якщо немає бібліотеки HWPF, використати
  евристику: пройтись по байтах як UTF-16 LE, збирати run-и printable-
  символів довжиною ≥4, розділяти неперевідними байтами. Достатньо для
  substring-пошуку; повний парсинг FIB/CHPX не потрібен.
- XLS (legacy): .xls — OLE compound + парсер BIFF8. У більшості мов є
  бібліотека (Apache POI HSSF / SpreadsheetLight / xlrd / excelize ...).
  Ітерувати рядки/клітинки, склеювати через \t / \n.
- RTF: .rtf — власний state-parser. Обробка: '{' '}', '\\\\' → '\\',
  '\\u N ?' → char(N), '\\\\'XX → byte(XX) у CP1251 (або шрифтовій codepage),
  '\\par'/'\\line' → '\n', '\\tab' → '\t', '\\*' → ігнорувати весь блок.

============================================================================
ДВА ДВИГУНИ
============================================================================
BruteForceEngine:
  - рекурсивно перерахуй файли з фільтром розширень
    (пропускай $RECYCLE.BIN, System Volume Information, прихов. сис. папки)
  - паралельно (worker pool ≈ N_CPU) для кожного файлу:
      extractor = registry.resolve(path)  // null → skip
      text = extractor.extractAsync(...)
      hits = findHits(text, query)
  - агрегуй FileSummary та SearchHit, повертай SearchResult

IndexedEngine:
  - location: <user-local-cache>/<app>/indexes/<sha256_prefix(rootFolderPath)>/
  - buildIndex: повна переіндексація: створи індекс із полями
      path (string, indexed=NO, stored=YES)
      ext  (string, stored=YES)
      size (long,   stored=YES)
      mtime (long,  stored=YES)
      content (text, indexed=YES, stored=YES, analyzer=стандартний)
  - searchQuery → побудова запиту:
      Якщо в q.text є будь-який з: + - " ( ) * ? ~ ^ : \ [ ] { }
      або слова AND / OR / NOT → ПРОПУСТИТИ через рідний QueryParser
      бекенду (Lucene-syntax: phrase, boolean, wildcard, fuzzy, fields).
      1 термін → wildcard "*term*" (substring) через content
      2+ термінів → phrase query через content із slop=0
  - search: візьми top-N (>=maxLineHits), для кожного дістань content
    зі store, прогни через findHits → точні позиції і снипети
  - deleteIndex: рекурсивно видали папку індексу
  - isIndexBuilt: папка існує і не порожня

============================================================================
UI (фреймворк-незалежно)
============================================================================
Layout (зверху вниз):
  [TitleBar]: app title + theme toggle (Light/Dark) + language combo (UA/EN)
  [Folder]: text input + [Browse…] (відкриває діалог вибору папки)
  [Find]:   text input (Enter = Search) + [Search]
  Опції: ☐Use index  ☐Match case  ☐Whole word
         статус індексу: "Index: built" / "Index: not built"
         праворуч: [Build index] [Delete index] [Cancel]
  Розділена область:
    лівий список «Files (matches)»: File | Hits | Ext | Modified
    правий список «Lines (context)»: File | Line | Snippet
  Статус-бар: текст + прогрес-бар, видимий поки isBusy

Налаштування (persist у user-local JSON):
  theme:     "Light" | "Dark" (default Dark)
  language:  ISO code, дефолт "uk", альтернативи "en", + (свій список)
  lastFolder: string?

Локалізація:
  УСІ user-facing рядки в коді — через ключ ("Str.Search", "Str.Cancel", ...)
  з runtime-resource словника. Перемикання мови НЕ потребує перезапуску.

Прогрес під час BuildIndex / Search:
  - звіт ПЕРЕД обробкою файлу (не після батча!): currentFile, filesProcessed/Total, elapsedMs
  - UI heartbeat 500 мс: показує "(XXs on this file)" коли поточний файл
    обробляється довше 2 с — ключове, щоб користувач бачив, що великий PDF
    не "завис", а просто довго парситься
  - формат статусу: "{stage} {n}/{total} ({pct%}) · {f/s} · ETA {duration} · {filename} ({Xs on this file})"

Поведінка:
  - DoubleClick на елементі → відкрити файл у дефолтній програмі ОС.
  - Right-click → Open / Reveal in Explorer / Copy path.
  - Cancel → cancel поточної операції (cancelable token / abort signal).
  - На зміну folder → перевір чи побудовано індекс і онови індикатор.

============================================================================
ВИМОГИ НЕ-ФУНКЦ.
============================================================================
- Не блокувати UI thread. Усе IO/CPU — асинхронно/у воркерах.
- Прогрес — кожні N (15–32) файлів, не на кожен.
- На пошкоджених файлах — ловити виняток, пропускати, продовжувати.
- Юнікод-поведінка ідентична: Ordinal/OrdinalIgnoreCase порівняння,
  без культурно-специфічних правил.
- Тести: дублюй покриття з docs/developer-guide.md (LineMatcher,
  RTF, PlainText, обидва двигуни на тимчасових файлах).

============================================================================
ВИХІДНІ АРТЕФАКТИ
============================================================================
1. Структура проєкту під цільовий стек (відповідник Sho.Core / Extractors /
   Search / App / Tests).
2. Реалізація всіх контрактів вище.
3. Юніт-тести з тими ж кейсами.
4. README українською: швидкий старт + посилання на user-guide / developer-guide.
5. Список заміни залежностей (.NET → цільовий стек) у вигляді таблиці.

Не вигадуй фічі поза цим документом. Якщо обраний стек не має прямого
аналога (напр., FolderBrowserDialog), використай ідіоматичний еквівалент і
поясни вибір у коментарі/PR-описі.
=== END PROMPT ===
```

---

## Як використовувати

1. Скопіюй блок промпту вище.
2. Вкажи в ньому цільовий стек і ОС.
3. Передай новій LLM-сесії разом із (опційно) цим репо як референсом.
4. Очікуваний вихід: новий проект із тими ж контрактами та поведінкою.

## Чому контракти, а не код

Прив'язка до коду на C# заблокувала б портування. Документ описує
*алгоритм*, *моделі* і *поведінку UI* — все, чого достатньо, щоб
реалізувати застосунок ідіоматично у цільовій мові, не повторюючи
.NET-специфічні рішення.
