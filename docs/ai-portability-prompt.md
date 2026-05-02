# Стек-незалежний промт для AI: портування Shozilla на інший стек

Скопіюй усе нижче (від рядка `=== BEGIN PROMPT ===`) і додай до запиту до AI разом із цільовим стеком ("портуй на Electron + TypeScript", "на Python + PyQt", "на Go + Fyne" тощо).

---

```
=== BEGIN PROMPT ===
Ти — AI-помічник з портування десктопного застосунку «Shozilla» з .NET 9/WPF на інший
стек/мову. Збережи функціональність 1:1, заміни лише імплементаційні залежності.
Нижче — нейтральний опис застосунку і його контракти.

ЦІЛЬОВИЙ СТЕК: <ВПИШИ_СЮДИ>      # напр., "Electron + TypeScript + React"
ЦІЛЬОВА ОС:    <ВПИШИ_СЮДИ>      # напр., "Windows + macOS"
БІБЛІОТЕКИ:    <за бажанням>     # напр., "MeiliSearch замість Lucene"

============================================================================
ЩО ЦЕ
============================================================================
Десктопна програма для пошуку текстових збігів у документах усередині
обраних папок (рекурсивно, на одному або кількох дисках). Два режими:
(1) перебірне сканування та (2) пошук по заздалегідь побудованому
повнотекстовому індексу. Інлайн-прев'ю DOCX-файлів із підсвічуванням
збігів і авто-скролом до знахідки.

============================================================================
СЦЕНАРІЇ
============================================================================
S1. Користувач натискає «Choose folders» → відкривається діалог із деревом
    усієї файлової системи (диски в корені, кожна папка з чекбоксом, lazy
    expansion). Користувач позначає одну або кілька папок (можна на різних
    дисках) → OK.
S2. (Опційно) натискає «Build index» → програма обходить усі підтримувані
    файли в наборі коренів, дістає plain-text і складає у локальний
    повнотекстовий індекс у user-local cache (наприклад, %LOCALAPPDATA% /
    ~/.cache). Поряд із індексом пишеться meta.json із датою збірки і
    кількістю проіндексованих документів.
S3. У полі пошуку (editable combobox із історією попередніх запитів)
    вводить запит ("Ніколаенко" або "Ніколаенко Юрій Вікторович") і
    натискає Enter. Запит дописується в історію і зберігається між
    сесіями.
S4. Програма повертає два списки одночасно:
    a) Files: унікальні файли, де є збіг, з кількістю збігів і метаданими
       (path, ext, size, mtime). Іконка з системного registry для розширення.
    b) Lines: усі окремі рядки з документів, що містять збіг, із
       контекстом ±N символів навколо збігу для розуміння контексту.
S5. Якщо вибраний файл / рядок належить .docx — у третій панелі праворуч
    знизу рендериться сам документ (DOCX→HTML→WebView) із підсвіченими
    збігами і авто-скролом до першого. Toggle "Preview" вмикає/вимикає
    панель.
S6. Подвійний клік на елементі будь-якого списку → відкриває документ
    у програмі ОС за замовчуванням (Word/PDF reader/тощо).
S7. Контекстне меню: Open, Reveal in file manager, Copy path.
S8. Прогрес довгих операцій (індексація, brute-force scan) із
    file-per-second, ETA, і "(Xs on this file)" — кожні 0.5 с heartbeat
    оновлює лічильник коли один файл обробляється довго.
S9. Кнопка Cancel перериває поточну операцію.

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
  elapsedMs: int                  # від початку операції

IndexMetadata:
  builtUtc:        timestamp
  documentCount:   int
  rootFolders:     string[]

ITextExtractor (інтерфейс плагінів формату):
  canHandle(path) -> bool
  extractAsync(path, cancelToken) -> string   # plain-text без розмітки

ISearchEngine:
  name -> string
  searchAsync(rootFolders[], query, progress, ct) -> SearchResult

IIndexedSearchEngine extends ISearchEngine:
  buildIndexAsync(rootFolders[], progress, ct)
  isIndexBuiltAsync(rootFolders[], ct) -> bool
  deleteIndexAsync(rootFolders[], ct)
  getIndexMetadataAsync(rootFolders[], ct) -> IndexMetadata | null

DocxPreviewService:
  isSupported(path) -> bool
  render(path, highlightTerms, darkTheme) -> resourcePath
    # повертає шлях до згенерованого HTML файлу (або URI, якщо рендер
    # in-memory). HTML містить вбудований CSS + JS для підсвічування
    # збігів і scrollIntoView. Кеш по path+mtime.

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
ХЕШ НАБОРУ КОРЕНЕВИХ ПАПОК (для location індексу)
============================================================================
function hashRoots(paths) -> string:
  normalized = paths
    .map(p => fullpath(p).trimEnd(['\\','/']).toLowerCase())
    .filter(nonEmpty).distinct().sort()
  joined = normalized.join('|')
  return sha256(joined).hexFirst8Bytes  # 16 hex chars

# Однаковий набір коренів (незалежно від порядку) → один індекс.

============================================================================
МУЛЬТИ-КОРЕНЕВИЙ DEDUPE
============================================================================
function dedupeRoots(paths) -> string[]:
  fullpaths = paths.map(p => fullpath(p).trimEnd(['\\','/']))
  unique = fullpaths.distinct().sort(by length asc)
  result = []
  for r in unique:
    covered = result.any(k => r != k && r.startsWith(k + sep))
    if !covered: result.push(r)
  return result
# Якщо позначені і "C:\Docs", і "C:\Docs\Tax", другий ігнорується.

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
    .pdf .doc .docx .xls .xlsx .pptx .rtf
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
  - dedupeRoots(rootFolders)
  - для кожного кореня рекурсивно перерахуй файли з фільтром розширень
    (пропускай $RECYCLE.BIN, System Volume Information, прихов. сис. папки)
  - дедуплікуй за path (на випадок overlapping ancestors)
  - паралельно (worker pool ≈ N_CPU) для кожного файлу:
      extractor = registry.resolve(path)  // null → skip
      text = extractor.extractAsync(...)
      hits = findHits(text, query)
  - агрегуй FileSummary та SearchHit, повертай SearchResult
  - звітуй прогрес ПЕРЕД обробкою файлу (не batch-ом)

IndexedEngine:
  - location: <user-local-cache>/<app>/indexes/<hashRoots(rootFolders)>/
  - buildIndex: повна переіндексація: створи індекс із полями
      path (string, indexed=NO, stored=YES)
      ext  (string, stored=YES)
      size (long,   stored=YES)
      mtime (long,  stored=YES)
      content (text, indexed=YES, stored=YES, analyzer=стандартний)
    Після Commit → пиши meta.json із builtUtc, documentCount, rootFolders.
  - searchQuery → побудова запиту:
      Якщо в q.text є будь-який з: + - " ( ) * ? ~ ^ : \ [ ] { }
      або слова AND / OR / NOT → ПРОПУСТИТИ через рідний QueryParser
      бекенду (Lucene-syntax: phrase, boolean, wildcard, fuzzy, fields).
      Пост-процес: TermQuery / single-term PhraseQuery → wildcard *term*
      (так "single-word" квотовані пошуки знаходять інфлектовані форми).
      1 термін → wildcard "*term*" (substring) через content
      2+ термінів → phrase query через content із slop=0
  - search: візьми top-N (>=maxLineHits), для кожного дістань content
    зі store; витягни чисті matcher-терми з parsed query (TermQuery,
    WildcardQuery без */?, PrefixQuery, FuzzyQuery, single-term Phrase,
    BooleanQuery без MUST_NOT) і прогни кожен через findHits → точні
    позиції в рядках і снипети, що відповідають оригінальному наміру.
  - deleteIndex: рекурсивно видали папку індексу
  - isIndexBuilt: папка існує і не порожня

============================================================================
DOCX PREVIEW
============================================================================
Конвертер DOCX → HTML (Mammoth.js / mammoth.NET / python-docx2html /
pandoc), тіло обертай у повну сторінку з:
  - theme-aware CSS (dark = white text on black, light = black on white)
  - <mark> для підсвічування
  - inline <script> що проходить text-нодами (TreeWalker), знаходить
    кожен term (case-insensitive), обертає у <mark id="m0..N"> і робить
    scrollIntoView({block:'center'}) до #m0
Запис у файл (не in-memory string), бо WebView ліміт ~2 МБ для
NavigateToString. Кеш HTML body по filePath + mtime.

WebView повинен мати спосіб обходу обмеження локальних файлів:
- Edge WebView2: SetVirtualHostNameToFolderMapping("preview-host",
  cacheDir, Allow), потім нав на https://preview-host/<file>?t=<ts>.
- Electron: file:// працює напряму (інша security model).
- WebKit / GTK: webkit_web_view_load_uri із file:// — працює, перевірити
  CORS для лок. ресурсів.

============================================================================
UI (фреймворк-незалежно)
============================================================================
Layout (зверху вниз):
  [TitleBar]: app icon (favicon) + назва "Shozilla"
  [Settings panel] — 4 вертикальні підпанелі, розділені тонкими лініями:
    [1] Inputs (verticaly):
        [Find]:   editable combobox із dropdown історії → [Search]
        [Folders]: read-only summary "N folders" → [Choose folders…]
    [2] Checkboxes: ☐Use index ☐Match case ☐Whole word
    [3] Index commands: [Build index] [Delete index] [Cancel]
    [4] Toolbar: [Theme toggle] [Language toggle] [Preview toggle]
  [Results area]: Files panel | (Lines panel / Preview panel)
    лівий список «Files (matches)»: icon | File | Hits | Path | Modified
    правий-верхній «Lines (context)»: File | Line | Snippet
    правий-нижній «DOCX preview»: WebView (тільки для .docx)
  [Status bar]:
    тонкий progress (3px, лише при busy)
    [search stats — текст] | [Index info — стан + дата + кількість файлів]

Поведінка:
  - Click на файлі ліворуч → у Lines рядки цього файлу стають bold
    (інші лишаються видимими, не фільтруються).
  - Click на рядку у Lines → preview показує DOCX зі скролом до знайденого.
  - DoubleClick → відкрити файл у дефолтній програмі ОС.
  - Right-click → Open / Reveal in Explorer / Copy path.
  - Cancel → cancel поточної операції (cancelable token / abort signal).
  - На зміну folders → перевір чи побудовано індекс і онови індикатор.
  - Splitter між Files і Right column, splitter між Lines і Preview.
  - Preview off → Preview row collapsed (height 0); Lines panel розтягуєт
    ься на повну праву висоту.

Folder picker діалог:
  - TreeView, корені = всі диски системи (на Windows DriveInfo;
    на Unix `/` як єдиний корінь).
  - Lazy load дочірніх папок при першому розкритті.
  - Кожна папка має чекбокс, незалежний (не пропагує до descendants).
  - При відкритті: якщо є попередньо вибрані папки — розкривається
    тільки гілка до них; решта згорнуто. Без вибору — все згорнуто.
  - OK: збираємо всі checked, дедуплікуємо descendants чиї ancestors
    теж checked (см. dedupeRoots алгоритм).
  - "Clear all" — швидке зняття галочок.

Налаштування (persist у user-local JSON):
  theme:         "Light" | "Dark" (default Dark)
  language:      ISO code, дефолт "uk", альтернативи "en", + (свій список)
  lastFolder:    string?           # legacy single-folder, optional
  lastFolders:   string[]          # current multi-folder
  showPreview:   bool (default true)
  queryHistory:  string[]          # max 50, dedupe, новіші зверху

Локалізація:
  УСІ user-facing рядки в коді — через ключ ("Str.Search", "Str.Cancel", ...)
  з runtime-resource словника. Перемикання мови НЕ потребує перезапуску.

Прогрес під час BuildIndex / Search:
  - звіт ПЕРЕД обробкою файлу (не після батча!): currentFile, filesProcessed/Total, elapsedMs
  - UI heartbeat 500 мс: показує "(XXs on this file)" коли поточний файл
    обробляється довше 2 с — ключове, щоб користувач бачив, що великий PDF
    не "завис", а просто довго парситься
  - формат статусу: "{stage} {n}/{total} ({pct%}) · {f/s} · ETA {duration} · {filename} ({Xs on this file})"

============================================================================
ВИМОГИ НЕ-ФУНКЦ.
============================================================================
- Не блокувати UI thread. Усе IO/CPU — асинхронно/у воркерах.
- Прогрес — кожні N (15–32) файлів, не на кожен; але CurrentFile
  оновлюється на КОЖЕН файл (heartbeat у VM покаже "Xs on this file").
- На пошкоджених файлах — ловити виняток, пропускати, продовжувати.
- Юнікод-поведінка ідентична: Ordinal/OrdinalIgnoreCase порівняння,
  без культурно-специфічних правил.
- Тести: дублюй покриття з docs/developer-guide.md (LineMatcher,
  RTF, PlainText, обидва двигуни на тимчасових файлах, multi-root dedupe).

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
аналога (напр., FolderBrowserDialog, WebView2, Mammoth.NET), використай
ідіоматичний еквівалент і поясни вибір у коментарі/PR-описі.
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
