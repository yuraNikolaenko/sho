namespace Sho.Core.Models;

public sealed record SearchHit(
    string FilePath,
    int LineNumber,
    string LineText,
    int MatchStart,
    int MatchLength,
    string Snippet);
