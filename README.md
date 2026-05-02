# Shozilla

Швидкий пошук документів за вмістом для Windows. Вибираєш одну або кілька папок (з різних дисків), опціонально будуєш повнотекстовий індекс — і знаходиш рядки з контекстом у TXT/MD/CSV/PDF/DOCX/XLSX/PPTX/DOC/XLS/RTF/HTML та інших текстових форматах. Інлайн прев'ю DOCX з підсвічуванням збігів.

## Швидкий старт

```powershell
dotnet build Sho.sln
dotnet run --project src\Sho.App\Sho.App.csproj
```

1. **Choose folders…** — обери одну або кілька папок (можна на різних дисках) у дереві з чекбоксами.
2. (Опційно) **Build index** — будує Lucene-індекс на диску в `%LOCALAPPDATA%\sho\indexes\<hash>\`.
3. Введи запит у поле **Find** (історія пошуків — у випадаючому списку) → Enter або **Search**.
4. Ліворуч — список файлів з кількістю збігів. Праворуч зверху — рядки з контекстом ±80 символів. Праворуч знизу — DOCX-прев'ю (для `.docx`).
5. Подвійний клік відкриває документ у програмі за замовчуванням.

## Документація

- [docs/user-guide.md](docs/user-guide.md) — інструкція користувача
- [docs/developer-guide.md](docs/developer-guide.md) — посібник розробника
- [docs/ai-portability-prompt.md](docs/ai-portability-prompt.md) — стек-незалежний промт для AI для портування на інший стек
- [CLAUDE.md](CLAUDE.md) — контекст для майбутніх AI-сесій (архітектура, gotcha-list)

## Структура

```
Sho.sln
├── src/
│   ├── Sho.Core         — моделі, інтерфейси, токенізація рядків, хеш папок
│   ├── Sho.Extractors   — IFileTextExtractor + реалізації для всіх форматів
│   ├── Sho.Search       — BruteForce + Lucene.NET indexed engines
│   └── Sho.App          — WPF MVVM UI, exe = Shozilla.exe
├── tests/
│   └── Sho.Tests        — 40 xUnit тестів
└── docs/
```

## Тести

```powershell
dotnet test tests\Sho.Tests\Sho.Tests.csproj
```

## Залежності

- .NET 9 (`net9.0` для бібліотек, `net9.0-windows` для UI)
- WPF + WinForms (FolderBrowserDialog у деяких місцях, інтероп для CodePages)
- **Wpf.Ui 4.0.2** — Fluent / Win11 Mica chrome
- **CommunityToolkit.Mvvm 8.4.0** — MVVM source generators
- **Lucene.NET 4.8** — повнотекстовий індекс + QueryParser
- **PdfPig**, **DocumentFormat.OpenXml**, **NPOI 2.5.6** (XLS + DOC OLE), **UTF.Unknown**, **System.Text.Encoding.CodePages**
- **Mammoth 1.11.0** — DOCX → HTML для прев'ю
- **Microsoft.Web.WebView2** — рендеринг прев'ю

## Ключові можливості

- Мульти-папка / мульти-диск пошук, дедуп вкладених коренів
- Два режими пошуку: brute-force (без індексу) та Lucene
- Повнотекстовий індекс із метаданими (дата збірки, кількість документів)
- Lucene query syntax: phrase, AND/OR/NOT, +/-, wildcard, fuzzy, fields
- Інлайн DOCX-прев'ю через WebView2 + Mammoth, з `<mark>`-підсвічуванням і авто-скролом до знахідки
- Тема Light/Dark, мова UA/EN — гаряче перемикаються
- Історія пошуків (50 останніх), зберігається між сесіями
- Прогрес індексації з ETA, files/sec і "час на поточному файлі"
- Авто-розпізнавання кодування: UTF-8 BOM, UTF-16, CP1251, KOI8-U
