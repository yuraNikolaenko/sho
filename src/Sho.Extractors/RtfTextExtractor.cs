using System.Text;
using Sho.Core.Abstractions;

namespace Sho.Extractors;

public sealed class RtfTextExtractor : IFileTextExtractor
{
    public bool CanHandle(string filePath) =>
        string.Equals(Path.GetExtension(filePath), ".rtf", StringComparison.OrdinalIgnoreCase);

    public async Task<string> ExtractAsync(string filePath, CancellationToken cancellationToken)
    {
        var raw = await File.ReadAllTextAsync(filePath, Encoding.ASCII, cancellationToken).ConfigureAwait(false);
        return StripRtf(raw);
    }

    internal static string StripRtf(string rtf)
    {
        if (string.IsNullOrEmpty(rtf)) return string.Empty;

        var sb = new StringBuilder(rtf.Length);
        var stack = new Stack<int>();
        int i = 0, depth = 0;
        bool ignore = false;
        int ignoreDepth = 0;

        while (i < rtf.Length)
        {
            char c = rtf[i];
            if (c == '{')
            {
                depth++;
                stack.Push(ignore ? 1 : 0);
                i++;
            }
            else if (c == '}')
            {
                depth--;
                if (stack.Count > 0)
                {
                    stack.Pop();
                    if (ignore && depth < ignoreDepth) ignore = false;
                }
                i++;
            }
            else if (c == '\\')
            {
                i++;
                if (i >= rtf.Length) break;
                char next = rtf[i];
                if (next == '\\' || next == '{' || next == '}')
                {
                    if (!ignore) sb.Append(next);
                    i++;
                }
                else if (next == '\'')
                {
                    if (i + 2 < rtf.Length)
                    {
                        var hex = rtf.Substring(i + 1, 2);
                        if (byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out byte b))
                        {
                            if (!ignore)
                            {
                                try { sb.Append(Encoding.GetEncoding(1251).GetString(new[] { b })); }
                                catch { sb.Append((char)b); }
                            }
                        }
                        i += 3;
                    }
                    else i = rtf.Length;
                }
                else if (next == '*')
                {
                    ignore = true;
                    ignoreDepth = depth;
                    i++;
                }
                else if (char.IsLetter(next))
                {
                    int start = i;
                    while (i < rtf.Length && char.IsLetter(rtf[i])) i++;
                    string word = rtf.Substring(start, i - start);
                    int param = 0;
                    bool hasParam = false;
                    bool neg = false;
                    if (i < rtf.Length && rtf[i] == '-') { neg = true; i++; }
                    while (i < rtf.Length && char.IsDigit(rtf[i]))
                    {
                        hasParam = true;
                        param = param * 10 + (rtf[i] - '0');
                        i++;
                    }
                    if (i < rtf.Length && rtf[i] == ' ') i++;

                    if (!ignore)
                    {
                        switch (word)
                        {
                            case "par":
                            case "line":
                                sb.AppendLine();
                                break;
                            case "tab":
                                sb.Append('\t');
                                break;
                            case "u" when hasParam:
                                int code = neg ? -param : param;
                                if (code < 0) code += 65536;
                                sb.Append((char)code);
                                if (i < rtf.Length && rtf[i] != '\\' && rtf[i] != '{' && rtf[i] != '}') i++;
                                break;
                        }
                    }
                }
                else
                {
                    i++;
                }
            }
            else if (c == '\r' || c == '\n')
            {
                i++;
            }
            else
            {
                if (!ignore) sb.Append(c);
                i++;
            }
        }

        return sb.ToString();
    }
}
