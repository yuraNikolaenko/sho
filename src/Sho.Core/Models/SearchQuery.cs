namespace Sho.Core.Models;

public sealed record SearchQuery(
    string Text,
    bool CaseSensitive = false,
    bool WholeWord = false,
    int ContextChars = 80,
    int MaxLineHits = 5000);
