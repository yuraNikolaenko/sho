using System.Text;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using Sho.Extractors;

namespace Sho.Tests;

public class XlsTextExtractorTests : IDisposable
{
    private readonly string _tmp;

    public XlsTextExtractorTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "sho-xls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    [Fact]
    public async Task Extracts_strings_from_xls()
    {
        var path = Path.Combine(_tmp, "test.xls");
        using (var fs = File.Create(path))
        {
            var wb = new HSSFWorkbook();
            var sheet = wb.CreateSheet("S1");
            var row = sheet.CreateRow(0);
            row.CreateCell(0).SetCellValue("Hello Ніколаенко");
            row.CreateCell(1).SetCellValue(42.0);
            sheet.CreateRow(1).CreateCell(0).SetCellValue("Юрій Вікторович");
            wb.Write(fs);
        }

        var extractor = new XlsTextExtractor();
        Assert.True(extractor.CanHandle(path));
        var text = await extractor.ExtractAsync(path, CancellationToken.None);

        Assert.Contains("Hello Ніколаенко", text);
        Assert.Contains("42", text);
        Assert.Contains("Юрій Вікторович", text);
    }
}

public class DocExtractRunsTests
{
    [Fact]
    public void Harvests_utf16_runs_above_threshold()
    {
        var word1 = "Ніколаенко";
        var word2 = "tiny";
        var sb = new StringBuilder();
        sb.Append('\0').Append('\0').Append('\0');
        sb.Append(word1);
        sb.Append('\0').Append('\0');
        sb.Append(word2);
        sb.Append('\0');
        sb.Append("Юрій Вікторович");

        var bytes = Encoding.Unicode.GetBytes(sb.ToString());
        var got = DocTextExtractor.ExtractRuns(bytes);

        Assert.Contains("Ніколаенко", got);
        Assert.Contains("Юрій Вікторович", got);
    }

    [Fact]
    public void Skips_runs_below_threshold()
    {
        var bytes = Encoding.Unicode.GetBytes("ab\0\0\0\0LongerText");
        var got = DocTextExtractor.ExtractRuns(bytes);
        Assert.DoesNotContain("ab", got);
        Assert.Contains("LongerText", got);
    }
}
