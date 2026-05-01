using System.Text;
using Sho.Core.Abstractions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Sho.Extractors;

public sealed class PdfTextExtractor : IFileTextExtractor
{
    public bool CanHandle(string filePath) =>
        string.Equals(Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase);

    public Task<string> ExtractAsync(string filePath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sb = new StringBuilder();
            using var doc = PdfDocument.Open(filePath);
            foreach (Page page in doc.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                sb.AppendLine(page.Text);
            }
            return sb.ToString();
        }, cancellationToken);
}
