using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Sho.Core.Abstractions;

namespace Sho.Extractors;

public sealed class DocxTextExtractor : IFileTextExtractor
{
    public bool CanHandle(string filePath) =>
        string.Equals(Path.GetExtension(filePath), ".docx", StringComparison.OrdinalIgnoreCase);

    public Task<string> ExtractAsync(string filePath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sb = new StringBuilder();
            using var doc = WordprocessingDocument.Open(filePath, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body == null) return string.Empty;

            foreach (var para in body.Descendants<Paragraph>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var t in para.Descendants<Text>())
                    sb.Append(t.Text);
                sb.AppendLine();
            }
            return sb.ToString();
        }, cancellationToken);
}
