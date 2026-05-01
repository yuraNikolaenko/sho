using Sho.Core.Abstractions;

namespace Sho.Extractors;

public sealed class TextExtractorRegistry : ITextExtractorRegistry
{
    private readonly IReadOnlyList<IFileTextExtractor> _extractors;
    private readonly HashSet<string> _supportedExtensions;

    public TextExtractorRegistry(IEnumerable<IFileTextExtractor>? extractors = null)
    {
        _extractors = (extractors ?? CreateDefaults()).ToList();
        _supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ext in DefaultExtensions) _supportedExtensions.Add(ext);
    }

    public IReadOnlyCollection<string> SupportedExtensions => _supportedExtensions;

    public IFileTextExtractor? Resolve(string filePath)
    {
        foreach (var e in _extractors)
            if (e.CanHandle(filePath)) return e;
        return null;
    }

    public static IEnumerable<IFileTextExtractor> CreateDefaults()
    {
        yield return new PlainTextExtractor();
        yield return new HtmlTextExtractor();
        yield return new PdfTextExtractor();
        yield return new DocxTextExtractor();
        yield return new XlsxTextExtractor();
        yield return new PptxTextExtractor();
        yield return new RtfTextExtractor();
        yield return new DocTextExtractor();
        yield return new XlsTextExtractor();
    }

    public static readonly IReadOnlyList<string> DefaultExtensions = new[]
    {
        ".txt", ".md", ".csv", ".tsv", ".log", ".xml", ".json", ".yaml", ".yml",
        ".ini", ".cfg", ".conf", ".properties", ".env",
        ".cs", ".js", ".ts", ".py", ".rb", ".go", ".rs", ".java", ".kt",
        ".c", ".cpp", ".h", ".hpp", ".sql", ".sh", ".ps1", ".bat", ".cmd",
        ".html", ".htm", ".xhtml", ".css", ".scss", ".less", ".tex",
        ".pdf",
        ".doc", ".docx", ".xls", ".xlsx", ".pptx",
        ".rtf"
    };
}
