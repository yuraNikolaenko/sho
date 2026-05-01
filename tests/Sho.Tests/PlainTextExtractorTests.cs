using System.Text;
using Sho.Extractors;

namespace Sho.Tests;

public class PlainTextExtractorTests : IDisposable
{
    private readonly string _tmpDir;

    public PlainTextExtractorTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "sho-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, true); } catch { }
    }

    [Fact]
    public async Task Reads_utf8_with_bom()
    {
        var path = Path.Combine(_tmpDir, "u8bom.txt");
        var content = "Ніколаенко Юрій";
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(true));

        var extractor = new PlainTextExtractor();
        Assert.True(extractor.CanHandle(path));
        var got = await extractor.ExtractAsync(path, CancellationToken.None);
        Assert.Contains("Ніколаенко", got);
    }

    [Fact]
    public async Task Reads_cp1251_cyrillic()
    {
        var path = Path.Combine(_tmpDir, "cp1251.txt");
        var enc = Encoding.GetEncoding(1251);
        await File.WriteAllBytesAsync(path, enc.GetBytes("Привет мир"));

        var extractor = new PlainTextExtractor();
        var got = await extractor.ExtractAsync(path, CancellationToken.None);
        Assert.Contains("Привет", got);
    }

    [Fact]
    public void CanHandle_known_extensions()
    {
        var e = new PlainTextExtractor();
        Assert.True(e.CanHandle("a.txt"));
        Assert.True(e.CanHandle("a.MD"));
        Assert.True(e.CanHandle("a.cs"));
        Assert.False(e.CanHandle("a.docx"));
        Assert.False(e.CanHandle("a.pdf"));
    }
}

public class RtfTextExtractorTests
{
    [Fact]
    public void Strips_basic_rtf_keeping_text()
    {
        var rtf = @"{\rtf1\ansi\ansicpg1251\deff0{\fonttbl{\f0 Arial;}}\f0\fs24 Hello \b world\b0 .\par Second line.\par}";
        var got = RtfTextExtractor.StripRtf(rtf);
        Assert.Contains("Hello", got);
        Assert.Contains("world", got);
        Assert.Contains("Second line", got);
    }

    [Fact]
    public void Decodes_unicode_escape()
    {
        var word = "Ніколаенко";
        var sb = new System.Text.StringBuilder(@"{\rtf1");
        foreach (var c in word) sb.Append("\\u").Append((int)c).Append('?');
        sb.Append('}');
        var got = RtfTextExtractor.StripRtf(sb.ToString());
        Assert.Contains(word, got);
    }

    [Fact]
    public void Ignores_destinations()
    {
        var rtf = @"{\rtf1{\*\fldinst HYPERLINK ""http://x""}visible}";
        var got = RtfTextExtractor.StripRtf(rtf);
        Assert.DoesNotContain("HYPERLINK", got);
        Assert.Contains("visible", got);
    }
}
