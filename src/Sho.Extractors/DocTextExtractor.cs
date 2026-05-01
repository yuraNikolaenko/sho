using System.Text;
using NPOI.POIFS.FileSystem;
using Sho.Core.Abstractions;

namespace Sho.Extractors;

public sealed class DocTextExtractor : IFileTextExtractor
{
    private const int MinRunLength = 4;

    public bool CanHandle(string filePath) =>
        string.Equals(Path.GetExtension(filePath), ".doc", StringComparison.OrdinalIgnoreCase);

    public Task<string> ExtractAsync(string filePath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var poifs = new POIFSFileSystem(fs);
            byte[]? bytes = TryReadStream(poifs, "WordDocument")
                         ?? TryReadStream(poifs, "1Table")
                         ?? TryReadStream(poifs, "0Table");
            if (bytes == null) return string.Empty;
            return ExtractRuns(bytes, cancellationToken);
        }, cancellationToken);

    private static byte[]? TryReadStream(POIFSFileSystem poifs, string name)
    {
        try
        {
            var entry = (DocumentEntry)poifs.Root.GetEntry(name);
            using var s = new DocumentInputStream(entry);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    internal static string ExtractRuns(byte[] bytes, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        var run = new StringBuilder();

        for (int i = 0; i + 1 < bytes.Length; i += 2)
        {
            if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
            char c = (char)(bytes[i] | (bytes[i + 1] << 8));

            if (IsPrintable(c))
            {
                run.Append(c);
            }
            else if (c == '\r' || c == '\n' || c == '\t' || c == '\v' || c == '\f')
            {
                if (run.Length >= MinRunLength) { sb.Append(run); sb.AppendLine(); }
                run.Clear();
            }
            else
            {
                if (run.Length >= MinRunLength) { sb.Append(run); sb.AppendLine(); }
                run.Clear();
            }
        }
        if (run.Length >= MinRunLength) sb.Append(run);
        return sb.ToString();
    }

    private static bool IsPrintable(char c)
    {
        if (c >= 0x20 && c < 0x7F) return true;
        if (c < 0x80) return false;
        if (c == '￾' || c == '￿' || c == '﻿') return false;
        if (char.IsSurrogate(c)) return false;
        if (char.IsLetterOrDigit(c) || char.IsPunctuation(c) || char.IsSymbol(c) || c == ' ') return true;
        return false;
    }
}
