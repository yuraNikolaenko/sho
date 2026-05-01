using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using Mammoth;

namespace Sho.App.Services;

public sealed class DocxPreviewService
{
    private readonly ConcurrentDictionary<string, CachedDoc> _cache = new();
    private readonly DocumentConverter _converter = new();
    private readonly string _previewDir;

    private sealed record CachedDoc(string BodyHtml, DateTime FileMtimeUtc);

    public DocxPreviewService()
    {
        _previewDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "sho", "preview");
        Directory.CreateDirectory(_previewDir);
    }

    public bool IsSupported(string? filePath) =>
        !string.IsNullOrEmpty(filePath)
        && string.Equals(Path.GetExtension(filePath), ".docx", StringComparison.OrdinalIgnoreCase);

    public void ClearCache() => _cache.Clear();

    /// <summary>
    /// Render the requested file (or a placeholder) into a stand-alone HTML file under
    /// %LOCALAPPDATA%\sho\preview\ and return its absolute path. Always returns a path so
    /// the caller can navigate the WebView to file:// without size limits.
    /// </summary>
    public string Render(string? filePath, IEnumerable<string>? highlightTerms, bool darkTheme, int? scrollToLineNumber = null)
    {
        string html;
        string key;

        if (string.IsNullOrEmpty(filePath))
        {
            html = WrapHtml("<p style='opacity:0.6'>Select a line or a file to preview.</p>",
                Array.Empty<string>(), darkTheme);
            key = "empty";
        }
        else if (!IsSupported(filePath))
        {
            html = WrapHtml("<p style='opacity:0.6'>Preview is available only for .docx files.</p>",
                Array.Empty<string>(), darkTheme);
            key = "notsupported";
        }
        else if (!File.Exists(filePath))
        {
            html = WrapHtml("<p style='opacity:0.6'>File not found.</p>",
                Array.Empty<string>(), darkTheme);
            key = "missing";
        }
        else
        {
            var fi = new FileInfo(filePath);
            var bodyHtml = _cache.TryGetValue(filePath, out var cached) && cached.FileMtimeUtc == fi.LastWriteTimeUtc
                ? cached.BodyHtml
                : ConvertAndCache(filePath, fi);
            var terms = (highlightTerms ?? Array.Empty<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct()
                .ToList();
            html = WrapHtml(bodyHtml, terms, darkTheme);
            key = Sho.Core.IO.PathHasher.Hash(filePath);
        }

        var outPath = Path.Combine(_previewDir, $"{key}.html");
        File.WriteAllText(outPath, html, new UTF8Encoding(false));
        return outPath;
    }

    private string ConvertAndCache(string filePath, FileInfo fi)
    {
        try
        {
            var result = _converter.ConvertToHtml(filePath);
            var html = result.Value ?? string.Empty;
            _cache[filePath] = new CachedDoc(html, fi.LastWriteTimeUtc);
            return html;
        }
        catch (Exception ex)
        {
            return $"<p style='color:#ff6b6b'>Failed to render: {System.Net.WebUtility.HtmlEncode(ex.Message)}</p>";
        }
    }

    private static string WrapHtml(string body, IReadOnlyList<string> terms, bool darkTheme)
    {
        string bg = darkTheme ? "#1e1e1e" : "#ffffff";
        string fg = darkTheme ? "#e8e8e8" : "#1f1f1f";
        string accent = darkTheme ? "#3a3a3a" : "#e0e0e0";
        const string markBg = "#ffd54f";
        const string markFg = "#1a1a1a";

        var termsJson = JsonSerializer.Serialize(terms);
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\">")
          .Append("<style>")
          .Append($"html,body{{margin:0;padding:0;background:{bg};color:{fg};font-family:'Segoe UI Variable','Segoe UI',sans-serif;font-size:14px;line-height:1.5}}")
          .Append("body{padding:18px 22px}")
          .Append("h1,h2,h3,h4,h5{line-height:1.25;margin:0.8em 0 0.4em}")
          .Append("h1{font-size:22px}h2{font-size:19px}h3{font-size:17px}")
          .Append("p{margin:0 0 0.6em}")
          .Append($"table{{border-collapse:collapse;margin:0.5em 0}}td,th{{border:1px solid {accent};padding:4px 8px}}")
          .Append("img{max-width:100%;height:auto}")
          .Append($"mark{{background:{markBg};color:{markFg};padding:0 2px;border-radius:2px}}")
          .Append($"mark.current{{outline:2px solid {markBg};outline-offset:2px}}")
          .Append("a{color:#4ea1ff}")
          .Append("</style></head><body>")
          .Append(body)
          .Append("<script>(function(){")
          .Append("const terms=").Append(termsJson).Append(";")
          .Append("if(!terms.length)return;")
          .Append("const lower=terms.map(t=>t.toLowerCase());")
          .Append("const walker=document.createTreeWalker(document.body,NodeFilter.SHOW_TEXT,null);")
          .Append("const nodes=[];let n;while(n=walker.nextNode()){")
          .Append("if(!n.parentElement)continue;")
          .Append("const tag=n.parentElement.tagName;if(tag==='SCRIPT'||tag==='STYLE'||tag==='MARK')continue;")
          .Append("nodes.push(n);}")
          .Append("let counter=0;")
          .Append("for(const tn of nodes){")
          .Append("const text=tn.nodeValue;const lo=text.toLowerCase();")
          .Append("const positions=[];")
          .Append("for(const t of lower){let i=0;while((i=lo.indexOf(t,i))!==-1){positions.push([i,i+t.length]);i+=t.length||1;}}")
          .Append("if(!positions.length)continue;")
          .Append("positions.sort((a,b)=>a[0]-b[0]);")
          .Append("const merged=[];for(const p of positions){if(merged.length&&merged[merged.length-1][1]>p[0]){merged[merged.length-1][1]=Math.max(merged[merged.length-1][1],p[1]);}else{merged.push([...p]);}}")
          .Append("const frag=document.createDocumentFragment();let last=0;")
          .Append("for(const [s,e] of merged){if(s>last)frag.appendChild(document.createTextNode(text.substring(last,s)));")
          .Append("const m=document.createElement('mark');m.id='m'+counter++;m.textContent=text.substring(s,e);frag.appendChild(m);last=e;}")
          .Append("if(last<text.length)frag.appendChild(document.createTextNode(text.substring(last)));")
          .Append("tn.parentNode.replaceChild(frag,tn);}")
          .Append("const first=document.getElementById('m0');")
          .Append("if(first){first.classList.add('current');first.scrollIntoView({block:'center',behavior:'instant'});}")
          .Append("})();</script>")
          .Append("</body></html>");
        return sb.ToString();
    }
}
