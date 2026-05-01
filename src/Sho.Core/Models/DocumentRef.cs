namespace Sho.Core.Models;

public sealed record DocumentRef(
    string Path,
    long SizeBytes,
    DateTime ModifiedUtc,
    string Extension)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}
