namespace Sho.Core.Abstractions;

public interface IFileTextExtractor
{
    bool CanHandle(string filePath);

    Task<string> ExtractAsync(string filePath, CancellationToken cancellationToken);
}
