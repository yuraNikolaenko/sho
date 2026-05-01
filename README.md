# sho

Швидкий пошук документів за вмістом для Windows. Дозволяє вибрати папку, опціонально побудувати повнотекстовий індекс і знаходити рядки з контекстом у TXT/MD/CSV/PDF/DOCX/XLSX/PPTX/DOC/XLS/RTF/HTML та інших текстових форматах.

## Швидкий старт

```powershell
dotnet build Sho.sln
dotnet run --project src\Sho.App\Sho.App.csproj
```

1. Натисни **Browse** і вибери папку.
2. (Опційно) **Build / rebuild index** — будує індекс на диску в `%LOCALAPPDATA%\sho\indexes\<hash>\`.
3. Введи запит у поле **Find** та натисни **Search** (або Enter).
4. Лівий список — файли з кількістю збігів. Правий — рядки з контекстом ±80 символів.
5. Подвійний клік відкриває документ у програмі за замовчуванням (PDF — у переглядачі PDF, DOCX — у Word і т.д.).

## Документація

- [docs/user-guide.md](docs/user-guide.md) — інструкція користувача
- [docs/developer-guide.md](docs/developer-guide.md) — посібник розробника
- [docs/ai-portability-prompt.md](docs/ai-portability-prompt.md) — стек-незалежний промт для AI для портування на інший стек

## Структура

```
Sho.sln
├── src/
│   ├── Sho.Core         — моделі, інтерфейси, токенізація рядків
│   ├── Sho.Extractors   — IFileTextExtractor + реалізації для всіх форматів
│   ├── Sho.Search       — BruteForce + Lucene.NET indexed engines
│   └── Sho.App          — WPF MVVM UI
├── tests/
│   └── Sho.Tests        — xUnit (LineMatcher, extractors, engines)
└── docs/                — три документи: user, developer, AI-portability
```

## Тести

```powershell
dotnet test tests\Sho.Tests\Sho.Tests.csproj
```

## Залежності

- .NET 9
- WPF + WinForms (FolderBrowserDialog, налаштовано в `Sho.App.csproj`)
- Lucene.NET 4.8
- PdfPig, DocumentFormat.OpenXml, NPOI (.xls + .doc OLE)
- UTF.Unknown, System.Text.Encoding.CodePages (CP1251/KOI8-U/etc)
- CommunityToolkit.Mvvm
