using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Sho.Core.Abstractions;

namespace Sho.Extractors;

public sealed class XlsxTextExtractor : IFileTextExtractor
{
    public bool CanHandle(string filePath) =>
        string.Equals(Path.GetExtension(filePath), ".xlsx", StringComparison.OrdinalIgnoreCase);

    public Task<string> ExtractAsync(string filePath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sb = new StringBuilder();
            using var doc = SpreadsheetDocument.Open(filePath, false);
            var wb = doc.WorkbookPart;
            if (wb == null) return string.Empty;

            var sst = wb.SharedStringTablePart?.SharedStringTable;

            foreach (var ws in wb.WorksheetParts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var row in ws.Worksheet.Descendants<Row>())
                {
                    foreach (var cell in row.Elements<Cell>())
                    {
                        string? value = null;
                        if (cell.DataType?.Value == CellValues.SharedString && sst != null)
                        {
                            if (int.TryParse(cell.InnerText, out int idx) && idx >= 0 && idx < sst.ChildElements.Count)
                                value = sst.ElementAt(idx).InnerText;
                        }
                        else if (cell.DataType?.Value == CellValues.InlineString)
                        {
                            value = cell.InnerText;
                        }
                        else
                        {
                            value = cell.CellValue?.InnerText ?? cell.InnerText;
                        }
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
}
