using System.Text;
using DocRag.Core.Interfaces;
using OfficeOpenXml;

namespace DocRag.Ingestion.Parsers;

/// <summary>
/// Parses password-protected .xlsx workbooks directly via EPPlus, which natively supports opening
/// encrypted OOXML packages (unlike the DocumentFormat.OpenXml SDK). Used only when a password is
/// supplied and the file is detected as an encrypted CFB container; unencrypted xlsx files go
/// through <see cref="OpenXmlParser"/> instead.
///
/// Note: EPPlus's license must be configured once at process startup (see Program.cs) before this
/// parser is used, per EPPlus 8's licensing API.
/// </summary>
public sealed class EncryptedXlsxParser : IDocumentParser
{
    public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { "xlsx" };

    public Task<ParsedDocument> ParseAsync(Stream content, string fileName, string? password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException("EncryptedXlsxParser requires a password; use OpenXmlParser for unencrypted workbooks.");

        // EPPlus needs a seekable stream it fully owns for the duration of the read.
        using var seekable = CopyToSeekable(content);
        using var package = new ExcelPackage(seekable, password);

        var sb = new StringBuilder();
        var segments = new List<ParsedSegment>(package.Workbook.Worksheets.Count);
        int index = 0;
        foreach (var sheet in package.Workbook.Worksheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sheetSb = new StringBuilder();
            if (sheet.Dimension != null)
            {
                for (int row = sheet.Dimension.Start.Row; row <= sheet.Dimension.End.Row; row++)
                {
                    var cells = new List<string>();
                    for (int col = sheet.Dimension.Start.Column; col <= sheet.Dimension.End.Column; col++)
                    {
                        var text = sheet.Cells[row, col].Text;
                        cells.Add(text);
                    }
                    var line = string.Join("\t", cells);
                    if (!string.IsNullOrWhiteSpace(line))
                        sheetSb.AppendLine(line);
                }
            }

            var sheetText = sheetSb.ToString();
            segments.Add(new ParsedSegment { Index = index, Text = sheetText, Label = sheet.Name });
            sb.AppendLine($"# {sheet.Name}");
            sb.AppendLine(sheetText);
            sb.AppendLine();
            index++;
        }

        var metadata = new Dictionary<string, object?>
        {
            ["format"] = "xlsx",
            ["sheet_count"] = package.Workbook.Worksheets.Count,
            ["was_encrypted"] = true
        };

        return Task.FromResult(new ParsedDocument { Text = sb.ToString(), Segments = segments, Metadata = metadata });
    }

    private static MemoryStream CopyToSeekable(Stream source)
    {
        var ms = new MemoryStream();
        source.CopyTo(ms);
        ms.Position = 0;
        return ms;
    }
}
