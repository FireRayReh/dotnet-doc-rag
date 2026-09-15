using DocRag.Core.Interfaces;

namespace DocRag.Ingestion.Parsers;

/// <summary>Parses plain text and Markdown files. Markdown is kept as-is (chunking treats it as text;
/// headings/paragraph structure naturally guides paragraph-based chunk boundaries).</summary>
public sealed class PlainTextParser : IDocumentParser
{
    public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { "txt", "md", "markdown" };

    public async Task<ParsedDocument> ParseAsync(Stream content, string fileName, string? password, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(content, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken);
        return new ParsedDocument
        {
            Text = text,
            Metadata = new Dictionary<string, object?> { ["format"] = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant() }
        };
    }
}
