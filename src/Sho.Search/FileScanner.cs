using Sho.Core.Models;

namespace Sho.Search;

public static class FileScanner
{
    public static IEnumerable<DocumentRef> Enumerate(string root, IReadOnlyCollection<string> supportedExtensions)
    {
        if (!Directory.Exists(root)) yield break;
        var ext = new HashSet<string>(supportedExtensions, StringComparer.OrdinalIgnoreCase);

        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files;
            string[] subs;
            try { files = Directory.GetFiles(dir); }
            catch { files = Array.Empty<string>(); }
            try { subs = Directory.GetDirectories(dir); }
            catch { subs = Array.Empty<string>(); }

            foreach (var f in files)
            {
                var e = Path.GetExtension(f);
                if (!ext.Contains(e)) continue;
                FileInfo fi;
                try { fi = new FileInfo(f); }
                catch { continue; }
                if (!fi.Exists) continue;
                yield return new DocumentRef(f, fi.Length, fi.LastWriteTimeUtc, e);
            }

            foreach (var s in subs)
            {
                var name = Path.GetFileName(s);
                if (string.IsNullOrEmpty(name)) continue;
                if (name.StartsWith('$') || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                    continue;
                stack.Push(s);
            }
        }
    }
}
