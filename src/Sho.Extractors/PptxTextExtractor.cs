using System.Text;
using DocumentFormat.OpenXml.Packaging;
using Sho.Core.Abstractions;
using A = DocumentFormat.OpenXml.Drawing;

namespace Sho.Extractors;

public sealed class PptxTextExtractor : IFileTextExtractor
{
    public bool CanHandle(string filePath) =>
        string.Equals(Path.GetExtension(filePath), ".pptx", StringComparison.OrdinalIgnoreCase);

    public Task<string> ExtractAsync(string filePath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sb = new StringBuilder();
            using var doc = PresentationDocument.Open(filePath, false);
            var pres = doc.PresentationPart;
            if (pres == null) return string.Empty;

            foreach (var slide in pres.SlideParts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var t in slide.Slide.Descendants<A.Text>())
                {
                    sb.Append(t.Text);
                    sb.Append(' ');
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }, cancellationToken);
}
