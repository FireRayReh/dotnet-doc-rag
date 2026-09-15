namespace Cryptex.Core.Interfaces;

/// <summary>
/// Result of parsing a raw document into plain text (optionally split into pages/sections).
/// </summary>
public sealed class ParsedDocument
{
    /// <summary>Full extracted plain text of the document.</summary>
    public required string Text { get; init; }

    /// <summary>Optional per-page/per-sheet/per-slide text segments, when the format has natural sections.
    /// If populated, chunkers may use this to attach page numbers to chunk metadata.</summary>
    public IReadOnlyList<ParsedSegment>? Segments { get; init; }

    /// <summary>Additional metadata extracted from the document (author, title, sheet names, etc.).</summary>
    public Dictionary<string, object?> Metadata { get; init; } = new();
}

public sealed class ParsedSegment
{
    public int Index { get; init; }
    public required string Text { get; init; }
    public string? Label { get; init; }
}

/// <summary>
/// Parses a specific document format into plain text.
/// </summary>
public interface IDocumentParser
{
    /// <summary>File extensions this parser handles, without the leading dot (e.g. "pdf", "docx").</summary>
    IReadOnlySet<string> SupportedExtensions { get; }

    /// <summary>
    /// Parses the given stream. <paramref name="password"/> is supplied when the caller knows (or
    /// suspects) the document is encrypted/password-protected; implementations that don't support
    /// encryption for their format should ignore it.
    /// </summary>
    Task<ParsedDocument> ParseAsync(Stream content, string fileName, string? password, CancellationToken cancellationToken = default);
}
