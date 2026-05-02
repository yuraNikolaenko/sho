namespace Sho.Core.Models;

public sealed record IndexMetadata(
    DateTime BuiltUtc,
    int DocumentCount,
    IReadOnlyList<string> RootFolders,
    string AnalyzerVersion = "");
