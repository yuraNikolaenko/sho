namespace Sho.Core.Models;

public sealed record IndexProgress(
    int FilesProcessed,
    int FilesTotal,
    string? CurrentFile,
    string? Stage)
{
    public double Fraction => FilesTotal == 0 ? 0 : (double)FilesProcessed / FilesTotal;
}
