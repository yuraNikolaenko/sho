namespace Sho.Core.Models;

public sealed record FileSummary(
    string FilePath,
    int HitCount,
    long SizeBytes,
    DateTime ModifiedUtc,
    string Extension)
{
    public string FileName => System.IO.Path.GetFileName(FilePath);
}
