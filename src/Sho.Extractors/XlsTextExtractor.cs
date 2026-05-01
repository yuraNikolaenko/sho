using System.Text;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using Sho.Core.Abstractions;

namespace Sho.Extractors;

public sealed class XlsTextExtractor : IFileTextExtractor
{
    public bool CanHandle(string filePath) =>
        string.Equals(Path.GetExtension(filePath), ".xls", StringComparison.OrdinalIgnoreCase);

    public Task<string> ExtractAsync(string filePath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var wb = new HSSFWorkbook(fs);
            var sb = new StringBuilder();

            for (int sheetIdx = 0; sheetIdx < wb.NumberOfSheets; sheetIdx++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sheet = wb.GetSheetAt(sheetIdx);
                if (sheet == null) continue;

                for (int r = sheet.FirstRowNum; r <= sheet.LastRowNum; r++)
                {
                    var row = sheet.GetRow(r);
                    if (row == null) continue;
                    for (int c = row.FirstCellNum; c < row.LastCellNum; c++)
                    {
                        var cell = row.GetCell(c);
                        if (cell == null) continue;
                        string? value = cell.CellType switch
                        {
                            CellType.String => cell.StringCellValue,
                            CellType.Numeric => cell.NumericCellValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            CellType.Boolean => cell.BooleanCellValue.ToString(),
                            CellType.Formula => SafeFormulaValue(cell),
                            _ => null
                        };
                        if (!string.IsNullOrEmpty(value))
                        {
                            sb.Append(value);
                            sb.Append('\t');
                        }
                    }
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }, cancellationToken);

    private static string? SafeFormulaValue(ICell cell)
    {
        try
        {
            return cell.CachedFormulaResultType switch
            {
                CellType.String => cell.StringCellValue,
                CellType.Numeric => cell.NumericCellValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                CellType.Boolean => cell.BooleanCellValue.ToString(),
                _ => cell.CellFormula
            };
        }
        catch { return cell.CellFormula; }
    }
}
