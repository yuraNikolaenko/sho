# DONE — Shozilla

Архів виконаних задач, згрупованих за датою. Свіже зверху. Переноситься з [TODO.md](TODO.md) у кінці дня.

## 2026-05-02

### Пошук — морфологія + точність

- **Українська морфологія для індексованого пошуку.** `UkrainianMorfologikAnalyzer` (`Lucene.Net.Analysis.Morfologik 4.8.0-beta00017`) лематизує і запит, і документи: "будинком" / "будинки" знаходяться запитом "будинок". Single-term і кожен токен multi-bare дають гібридний запит `BooleanQuery [TermQuery(лема) SHOULD, WildcardQuery(*raw*) SHOULD, FuzzyQuery(raw) SHOULD]`. `MorphologicalLineMatcher` робить лема-аваре підсвічування у raw-тексті (лема + raw-substring + Levenshtein-фолбек).
  - Файли: `src/Sho.Search/IndexedSearchEngine.cs`, `src/Sho.Search/MorphologicalLineMatcher.cs`, `src/Sho.Core/Models/IndexMetadata.cs` (`AnalyzerVersion`).
- **Case-sensitivity фікс UA-морфології.** Morfologik повертає лемми у регістрі словника (Title-Case для власних назв). Lucene multi-term queries (Wildcard / Fuzzy) case-sensitive — `*ніколаєнко*` не матчив `Ніколаєнко`. `LowerUkrainianAnalyzer` через `AnalyzerWrapper` додає `LowerCaseFilter` після Morfologik. Усі терми canonical lowercase. `AnalyzerVersion` бампнуто на `uk-morfologik-2`.
  - Файли: `src/Sho.Search/Analysis/LowerUkrainianAnalyzer.cs`.
- **Multi-bare через QueryParser замість строгого PhraseQuery.** "Ніколаєнка Юрія" знаходило файл з "Ніколаєнко Юрій" лише через QueryParser-шлях (`+a +b`). Тепер bare-multi теж проганяється через `QueryParser` з `AND_OPERATOR`. `WildcardifySingleTerms` обгортає кожну `TermQuery` у гібрид (TermQuery + WildcardQuery + FuzzyQuery).
- **Group-AND семантика у `MorphologicalLineMatcher`.** Раніше при multi-token хіти емітилися на КОЖЕН токен незалежно — XLSX з 2563 рядків з "Юрій" видавав 2563 хіти, навіть якщо "Ніколаєнка" була лише в одному рядку. Тепер matcher групує query-токени, для кожного рядка перевіряє, що ВСІ групи знайшли збіг, і тільки тоді емітить.
- **Fuzzy 2 → 1 для жорсткості.** "Ніколаєнка" знаходила "Ніколаєва" (Levenshtein=2). Знизив `FuzzyMaxEdits` у `BuildSingleTermHybridQuery`, `TermPlusWildcard` і `MorphologicalLineMatcher` до 1. Лема-гілка все одно покриває словникові інфлекції; fuzzy лишається фолбеком на опечатки на 1 символ.

### Інкрементальне індексування

- **API `UpdateDocumentsAsync` у `IIndexedSearchEngine`.** Інкрементальні апсерти/видалення документів за path-ключем; meta.json оновлюється з `writer.NumDocs`.
- **`SyncIndexAsync` + кнопка "Оновити індекс".** Diff диск ↔ індекс (нові → upsert, зниклі → delete, змінені mtime → re-extract). Для випадків, коли watcher був вимкнений або застосунок не працював.
- **`IndexWatcherService`.** `FileSystemWatcher` по кожному кореню, дебаунс 1.5с, `SemaphoreSlim` серіалізує батчі, обробляє Created/Changed/Deleted/Renamed з фільтром за extensions.
- **Інтеграція watcher у ViewModel + UI.** Checkbox "Авто-оновлення індексу" з тултипом, збереження в `AppSettings.AutoUpdateIndex` (default=true), Build/Delete index стопить і перезапускає watcher.
  - Файли: `src/Sho.App/Services/IndexWatcherService.cs`, `src/Sho.Core/Abstractions/ISearchEngine.cs`, `src/Sho.App/ViewModels/MainViewModel.cs`.

### UI / UX

- **Фільтр результатів за підрядком у абсолютному шляху.** `ICollectionView.Filter` на Files і Lines, AND-логіка по токенам, case-insensitive, дебаунс 200мс. Лічильники "filtered/total" у статусбарі.
- **Toggle "Тільки з обраного файла" у заголовку Lines.** Звужує панель рядків до обраного файла; коли вимкнено — клік по файлу автопрокручує таблицю Lines до першого хіта цього файлу.
- **Sort Lines у порядку Files.** Спершу Files сортуються по ModifiedUtc asc, потім Lines додаються відсортовано по `(fileIndex, LineNumber)`.
- **Preview-логіка перероблена.** Клік по файлу → preview відкриває цей файл, скрол на mark 0. Клік по рядку → preview позиціонується саме на цей рядок (`scrollIntoView(#m{N})`). `markIdx` обчислюється як індекс хіта серед хітів його файла.
- **Lema-аваре підсвічування у preview.** Окрім сирого запиту, у preview передаються аналізовані лемми + стеми (lemma[:-2] для термів ≥6 символів). JS у preview шукає на межі слова і розтягує match до кінця слова → "ніколаєн" підсвічує всю "НІКОЛАЄНКА".
- **Bulk-replace для DataGrid-ів.** `BulkObservableCollection<T>` з методом `ReplaceAll` — один `Reset` замість N окремих `CollectionChanged` для тисяч хітів. Усуває "(не відповідає)" у заголовку при великих результатах.
- **Search у background thread.** `SearchAsync` тепер обгортає Lucene-call і per-doc цикл у `Task.Run`. Раніше after-`Task.Yield` continuation поверталася на UI-context, що блокувало диспетчер і прогрес-репорти не показувались до завершення пошуку.
- **Прогрес у статусбарі.** `IndexedSearchEngine.SearchAsync` репортить "Querying index" → "Searching" → "Highlighting X/Y · file" (раз на 8 файлів) → "Done". Per-file cap 200 хітів — щоб одна табліца на 5000 рядків не з'їдала весь буфер.
- **Уніфікація висот контролів у frame налаштувань.** Усі лейбли через `SharedSizeGroup="LabelCol"` (MinWidth=100), усі поля Height=32, усі кнопки `SharedSizeGroup="ButtonCol"` MinWidth=160. Кнопки у блоках індексування і теми/мови/preview також 32px.
- **Фон таблиць результатів = SolidBackgroundFillColorBaseBrush.** DataGrid Background, RowBackground=Transparent, ScrollViewer track зливаються з заголовками. Прибрано стару high-contrast логіку (ResultsBackground/Foreground brushes mutated on theme switch).
- **Зменшено висоту TitleBar до 28px** + іконка 14pt у заголовку.
- **Зменшено висоту чекбокса у заголовку Lines.** Висота `Height=18, FontSize=12` — однакова з заголовком Files.

### Compact mode + Layout fixes

- **Компактний режим.** Toolbar розділено: завжди-видимий рядок пошуку (Find + ComboBox + chevron-toggle + Search button) + collapsible Grid (Папки, Фільтр, чекбокси, кнопки індексу, теми/мови). `CompactMode` зберігається в `AppSettings`. Перемикається chevron-кнопкою.
- **Фікс препу: висота preview-панелі зберігається між toggle off/on.** Поле `_savedPreviewRowHeight` запам'ятовує `PreviewRow.Height` ПЕРЕД схованням; при повторному ввімкненні відновлюється — без скиду до `3*`.
- **Фікс: вибір з історії пошуку очищав поле і пошук не запускався.** `RememberQuery` видаляв-і-вставляв поточний рядок у `QueryHistory`, що змушувало ComboBox SelectedItem стати null і двосторонній Text-біндинг стирав `QueryText`. Фікс: знімаю снапшот `queryText = QueryText` ДО `RememberQuery`, проганяю пошук через локальну змінну.
- **Фікс: накладання folders/filter рядків після перебудови toolbar.** Border з FolderSummary мав застарілий `Grid.Row="2"`; виправив на `Grid.Row="0"`.
- **Window resize фікс.** Додано явний `shell:WindowChrome` з `ResizeBorderThickness=8` + `ResizeMode=CanResizeWithGrip` + `MinHeight/MinWidth`. FluentWindow з extended title-bar мав тонку зону ресайзу по вертикалі, важко вловлювалася курсором.

### .ico + іконка

- **Real .ico для desktop-shortcuts.** `AppIconFactory.WriteIcoFile` рендерить SymbolIcon у 7 PNG-розмірах (16/24/32/48/64/128/256), збирає у multi-resolution ICO. CLI флаг `Shozilla.exe --write-icon <path>` для регенерації. `<ApplicationIcon>Resources\app.ico</ApplicationIcon>` у csproj.
- **Прозорий фон + більша лупа.** Background → Transparent (раніше темно-сіре заповнення давало "чорний квадрат" у композиції з таскбаром), FontSize 0.7 → 0.95.

### AI-режим (заглушка)

- **Кнопка "AI режим"** у блоці Theme/Language/Preview. Клік відкриває MessageBox: "AI-режим ще не реалізований. Буде підключено у наступній сесії — Qwen2.5-1.5B локально, без хмари." Готує UI-точку для майбутньої LlamaSharp-інтеграції (AI-1, AI-2 у TODO).

### Інфраструктура

- **TODO.md / DONE.md** для трекінгу між сесіями.
- **Тести.** 57 тестів зелених (раніше 40). Нові: 5 на `UpdateDocumentsAsync`, 2 на `SyncIndexAsync`, 4 на UA-морфологію (інфлекції, proper nouns, version, діагностика), 1 на multi-bare AND-семантику matcher-а, 1 на fuzzy=1 жорсткість (Ніколаєнка не знаходить Ніколаєва), 1 на user-фрагмент з УПРОПИСНИМИ ВЛАСНИМИ НАЗВАМИ.
