using System.Security.Cryptography;
using System.Text;

namespace Sho.Core.IO;

public static class PathHasher
{
    public static string Hash(string path)
    {
        var norm = System.IO.Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(norm));
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }
}
