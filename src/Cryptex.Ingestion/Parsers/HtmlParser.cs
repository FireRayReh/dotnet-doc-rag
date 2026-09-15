using Cryptex.Core.Interfaces;
using HtmlAgilityPack;

namespace Cryptex.Ingestion.Parsers;

/// <summary>Parses HTML files, stripping tags/scripts/styles down to readable text.</summary>
public sealed class HtmlParser : IDocumentParser
{
    public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { "html", "htm" };

    public Task<ParsedDocument> ParseAsync(Stream content, string fileName, string? password, CancellationToken cancellationToken = default)
    {
        var doc = new HtmlDocument();
        doc.Load(content);

        foreach (var node in doc.DocumentNode.SelectNodes("//script|//style") ?? Enumerable.Empty<HtmlNode>())
        {
            node.Remove();
        }

        string title = doc.DocumentNode.SelectSingleNode("//title")?.InnerText?.Trim() ?? string.Empty;
        string text = HtmlEntity.DeEntitize(doc.DocumentNode.InnerText);

        // Collapse excess whitespace left behind by tag stripping while preserving paragraph breaks.
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
        var cleaned = string.Join("\n\n", lines);

        var metadata = new Dictionary<string, object?> { ["format"] = "html" };
        if (!string.IsNullOrWhiteSpace(title)) metadata["title"] = title;

        return Task.FromResult(new ParsedDocument { Text = cleaned, Metadata = metadata });
    }
}
