using System.Security.Cryptography;
using System.Text;

namespace Sho.Core.IO;

public static class PathHasher
{
    public static string Hash(string path)
    {
        var norm = Normalize(path);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(norm));
        return ToHex(bytes);
    }

    public static string Hash(IEnumerable<string> paths)
    {
        var sorted = paths
            .Select(Normalize)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        if (sorted.Count == 0) return Hash("");
        var combined = string.Join("|", sorted);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return ToHex(bytes);
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        try { return System.IO.Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant(); }
        catch { return path.TrimEnd('\\', '/').ToLowerInvariant(); }
    }

    private static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }
}
