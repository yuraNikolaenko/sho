namespace Sho.Core.Models;

public sealed record IndexProgress(
    int FilesProcessed,
    int FilesTotal,
    string? CurrentFile,
    string? Stage,
    long ElapsedMs = 0)
{
    public double Fraction => FilesTotal == 0 ? 0 : (double)FilesProcessed / FilesTotal;

    public double FilesPerSecond => ElapsedMs == 0 ? 0 : FilesProcessed * 1000.0 / ElapsedMs;

    public TimeSpan? Eta
    {
        get
        {
            if (FilesProcessed == 0 || FilesTotal == 0 || ElapsedMs == 0) return null;
            int remaining = FilesTotal - FilesProcessed;
            if (remaining <= 0) return TimeSpan.Zero;
            double secPerFile = ElapsedMs / 1000.0 / FilesProcessed;
            return TimeSpan.FromSeconds(secPerFile * remaining);
        }
    }
}
