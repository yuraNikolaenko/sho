using Sho.Core.Abstractions;
using UtfUnknown;

namespace Sho.Extractors;

public sealed class PlainTextExtractor : IFileTextExtractor
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".tsv", ".log", ".xml", ".json", ".yaml", ".yml",
        ".ini", ".cfg", ".conf", ".properties", ".env",
        ".cs", ".js", ".ts", ".py", ".rb", ".go", ".rs", ".java", ".kt",
        ".c", ".cpp", ".h", ".hpp", ".sql", ".sh", ".ps1", ".bat", ".cmd",
        ".html", ".htm", ".css", ".scss", ".less", ".tex"
    };

    public bool CanHandle(string filePath) =>
        Extensions.Contains(Path.GetExtension(filePath));

    public async Task<string> ExtractAsync(string filePath, CancellationToken cancellationToken)
    {
        var fi = new FileInfo(filePath);
        if (!fi.Exists || fi.Length == 0) return string.Empty;

        const long MaxBytes = 64L * 1024 * 1024;
        long bytesToRead = Math.Min(fi.Length, MaxBytes);

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
        var buffer = new byte[bytesToRead];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        if (total < buffer.Length) Array.Resize(ref buffer, total);

        var detected = CharsetDetector.DetectFromBytes(buffer);
        var enc = detected.Detected?.Encoding ?? System.Text.Encoding.UTF8;
        try { return enc.GetString(buffer); }
        catch { return System.Text.Encoding.UTF8.GetString(buffer); }
    }
}
