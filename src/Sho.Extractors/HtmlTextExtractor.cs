using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Sho.Core.Abstractions;
using UtfUnknown;

namespace Sho.Extractors;

public sealed partial class HtmlTextExtractor : IFileTextExtractor
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".html", ".htm", ".xhtml" };

    public bool CanHandle(string filePath) =>
        Extensions.Contains(Path.GetExtension(filePath));

    public async Task<string> ExtractAsync(string filePath, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        var enc = CharsetDetector.DetectFromBytes(bytes).Detected?.Encoding ?? Encoding.UTF8;
        var raw = enc.GetString(bytes);

        var noScript = ScriptStyleRegex().Replace(raw, " ");
        var noTags = TagRegex().Replace(noScript, " ");
        return WebUtility.HtmlDecode(noTags);
    }

    [GeneratedRegex(@"<(script|style)\b[^>]*>[\s\S]*?</\1>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();
}
