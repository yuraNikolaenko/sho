namespace Sho.Core.Abstractions;

public interface ITextExtractorRegistry
{
    IFileTextExtractor? Resolve(string filePath);

    IReadOnlyCollection<string> SupportedExtensions { get; }
}
