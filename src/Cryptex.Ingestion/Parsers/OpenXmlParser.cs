using System.Text;
using Cryptex.Core.Interfaces;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using A = DocumentFormat.OpenXml.Drawing;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Cryptex.Ingestion.Parsers;

/// <summary>
/// Parses unencrypted OOXML documents (docx/xlsx/pptx) using the DocumentFormat.OpenXml SDK.
/// Encrypted files are decrypted upstream (by <c>OfficeCryptoDecryptor</c> for docx/pptx, or opened
/// directly by <c>EncryptedXlsxParser</c> via EPPlus for xlsx) before reaching this parser.
/// </summary>
public sealed class OpenXmlParser : IDocumentParser
{
    public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { "docx", "xlsx", "pptx" };

    public Task<ParsedDocument> ParseAsync(Stream content, string fileName, string? password, CancellationToken cancellationToken = default)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "docx" => Task.FromResult(ParseWordDocument(content)),
            "pptx" => Task.FromResult(ParsePresentation(content)),
            "xlsx" => Task.FromResult(ParseWorkbook(content)),
            _ => throw new NotSupportedException($"OpenXmlParser does not support '.{ext}'.")
        };
    }

    private static ParsedDocument ParseWordDocument(Stream content)
    {
        using var doc = WordprocessingDocument.Open(content, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        var text = body?.InnerText ?? string.Empty;

        // Rebuild with paragraph breaks so downstream chunking sees real paragraph boundaries
        // (InnerText concatenates all run text without separators).
        var sb = new StringBuilder();
        if (body != null)
        {
            foreach (var para in body.Descendants<W.Paragraph>())
            {
                var paraText = string.Concat(para.Descendants<W.Text>().Select(t => t.Text));
                if (!string.IsNullOrWhiteSpace(paraText))
                {
                    sb.AppendLine(paraText);
                    sb.AppendLine();
                }
            }
        }

        var metadata = new Dictionary<string, object?> { ["format"] = "docx" };
        var props = doc.PackageProperties;
        if (!string.IsNullOrWhiteSpace(props.Title)) metadata["title"] = props.Title;
        if (!string.IsNullOrWhiteSpace(props.Creator)) metadata["author"] = props.Creator;

        return new ParsedDocument { Text = sb.Length > 0 ? sb.ToString() : text, Metadata = metadata };
    }

    private static ParsedDocument ParsePresentation(Stream content)
    {
        using var doc = PresentationDocument.Open(content, false);
        var slideParts = doc.PresentationPart?.SlideParts.ToList() ?? new List<SlidePart>();

        var segments = new List<ParsedSegment>(slideParts.Count);
        var sb = new StringBuilder();
        for (int i = 0; i < slideParts.Count; i++)
        {
            var slide = slideParts[i].Slide;
            var texts = slide is null
                ? Enumerable.Empty<string>()
                : slide.Descendants<A.Text>().Select(t => t.Text ?? string.Empty);
            var slideText = string.Join("\n", texts.Where(t => !string.IsNullOrWhiteSpace(t)));
            segments.Add(new ParsedSegment { Index = i, Text = slideText, Label = $"slide {i + 1}" });
            sb.AppendLine(slideText);
            sb.AppendLine();
        }

        var metadata = new Dictionary<string, object?> { ["format"] = "pptx", ["slide_count"] = slideParts.Count };
        var props = doc.PackageProperties;
        if (!string.IsNullOrWhiteSpace(props.Title)) metadata["title"] = props.Title;

        return new ParsedDocument { Text = sb.ToString(), Segments = segments, Metadata = metadata };
    }

    private static ParsedDocument ParseWorkbook(Stream content)
    {
        using var doc = SpreadsheetDocument.Open(content, false);
        var workbookPart = doc.WorkbookPart ?? throw new InvalidOperationException("Workbook has no WorkbookPart.");
        var sharedStringTable = workbookPart.SharedStringTablePart?.SharedStringTable;
        var sharedStrings = sharedStringTable is null
            ? Array.Empty<string>()
            : sharedStringTable.Elements<SharedStringItem>().Select(s => s.InnerText).ToArray();

        var workbook = workbookPart.Workbook ?? throw new InvalidOperationException("Workbook part has no <workbook> element.");
        var sheets = workbook.Descendants<Sheet>().ToList();
        var segments = new List<ParsedSegment>(sheets.Count);
        var sb = new StringBuilder();

        for (int i = 0; i < sheets.Count; i++)
        {
            var sheet = sheets[i];
            var sheetName = sheet.Name?.Value ?? $"Sheet{i + 1}";
            if (sheet.Id?.Value is null) continue;
            var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);
            var worksheet = worksheetPart.Worksheet ?? throw new InvalidOperationException($"Sheet '{sheetName}' has no <worksheet> element.");

            var sheetSb = new StringBuilder();
            foreach (var row in worksheet.Descendants<Row>())
            {
                var cellValues = row.Elements<Cell>().Select(c => GetCellValue(c, sharedStrings));
                var line = string.Join("\t", cellValues);
                if (!string.IsNullOrWhiteSpace(line))
                    sheetSb.AppendLine(line);
            }

            var sheetText = sheetSb.ToString();
            segments.Add(new ParsedSegment { Index = i, Text = sheetText, Label = sheetName });
            sb.AppendLine($"# {sheetName}");
            sb.AppendLine(sheetText);
            sb.AppendLine();
        }

        var metadata = new Dictionary<string, object?> { ["format"] = "xlsx", ["sheet_count"] = sheets.Count };
        return new ParsedDocument { Text = sb.ToString(), Segments = segments, Metadata = metadata };
    }

    private static string GetCellValue(Cell cell, string[] sharedStrings)
    {
        var value = cell.CellValue?.InnerText ?? string.Empty;
        if (cell.DataType?.Value == CellValues.SharedString && int.TryParse(value, out var idx) && idx >= 0 && idx < sharedStrings.Length)
            return sharedStrings[idx];
        return value;
    }
}
