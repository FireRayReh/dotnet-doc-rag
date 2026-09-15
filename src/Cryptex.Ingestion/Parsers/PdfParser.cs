using Cryptex.Core.Interfaces;
using UglyToad.PdfPig;

namespace Cryptex.Ingestion.Parsers;

/// <summary>Parses PDF files, including password-protected ones (via UglyToad.PdfPig's native decryption support).</summary>
public sealed class PdfParser : IDocumentParser
{
    public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { "pdf" };

    public Task<ParsedDocument> ParseAsync(Stream content, string fileName, string? password, CancellationToken cancellationToken = default)
    {
        var options = new ParsingOptions
        {
            UseLenientParsing = true
        };
        if (!string.IsNullOrEmpty(password))
            options.Password = password;

        using var pdf = PdfDocument.Open(content, options);

        var segments = new List<ParsedSegment>(pdf.NumberOfPages);
        var sb = new System.Text.StringBuilder();

        int index = 0;
        foreach (var page in pdf.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageText = page.Text;
            segments.Add(new ParsedSegment { Index = index, Text = pageText, Label = $"page {page.Number}" });
            sb.AppendLine(pageText);
            sb.AppendLine();
            index++;
        }

        var metadata = new Dictionary<string, object?>
        {
            ["format"] = "pdf",
            ["page_count"] = pdf.NumberOfPages,
            ["was_encrypted"] = pdf.IsEncrypted
        };

        try
        {
            var info = pdf.Information;
            if (!string.IsNullOrWhiteSpace(info?.Title)) metadata["title"] = info!.Title;
            if (!string.IsNullOrWhiteSpace(info?.Author)) metadata["author"] = info!.Author;
        }
        catch
        {
            // Document info dictionary is optional/best-effort; ignore extraction failures.
        }

        return Task.FromResult(new ParsedDocument
        {
            Text = sb.ToString(),
            Segments = segments,
            Metadata = metadata
        });
    }
}
