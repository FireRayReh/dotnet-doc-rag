namespace Cryptex.Core.Models;

/// <summary>
/// Represents a single chunk of a source document, ready for embedding and storage.
/// </summary>
public sealed class DocumentChunk
{
    /// <summary>Unique id for this chunk (also used as the point id in the vector store; must be a
    /// valid UUID since Qdrant point ids are typed as either UUID or unsigned integer).</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Id of the parent document this chunk belongs to (LibreChat's "file_id").</summary>
    public required string FileId { get; init; }

    /// <summary>Zero-based position of this chunk within the document.</summary>
    public int ChunkIndex { get; init; }

    /// <summary>Raw chunk text (what rag_api calls "page_content").</summary>
    public required string Content { get; init; }

    /// <summary>Approximate token count of <see cref="Content"/> for the configured tokenizer.</summary>
    public int TokenCount { get; init; }

    /// <summary>Arbitrary metadata surfaced back to LibreChat (source, file_id, page number, etc.).</summary>
    public Dictionary<string, object?> Metadata { get; init; } = new();

    /// <summary>Embedding vector for this chunk, populated after the embedding step.</summary>
    public float[]? Embedding { get; set; }
}

/// <summary>
/// Represents a document that has been ingested (or is being ingested) into the system.
/// </summary>
public sealed class IngestedDocument
{
    /// <summary>Unique id for the document (LibreChat's "file_id").</summary>
    public string FileId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Original file name as uploaded.</summary>
    public required string FileName { get; init; }

    /// <summary>MIME/content type of the original file.</summary>
    public string? ContentType { get; init; }

    /// <summary>Detected format extension. Updated after envelope decryption, when the true format
    /// is only known once the blob has been decrypted and sniffed.</summary>
    public required string Format { get; set; }

    /// <summary>Owning user id, if the upload is scoped to a user (LibreChat sends this).</summary>
    public string? UserId { get; init; }

    /// <summary>Number of chunks produced for this document.</summary>
    public int ChunkCount { get; set; }

    /// <summary>Whether the source file was password protected / encrypted.</summary>
    public bool WasEncrypted { get; set; }

    /// <summary>UTC timestamp the document was ingested.</summary>
    public DateTimeOffset IngestedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Current processing status.</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>Error message if ingestion failed.</summary>
    public string? Error { get; set; }

    /// <summary>Free-form metadata (e.g. original folder path for bulk admin ingestion).</summary>
    public Dictionary<string, object?> Metadata { get; init; } = new();
}

public enum DocumentStatus
{
    Pending,
    Processing,
    Completed,
    Failed
}

/// <summary>
/// A dense embedding vector paired with the id it belongs to.
/// </summary>
public sealed class EmbeddingVector
{
    public required string Id { get; init; }
    public required float[] Values { get; init; }
}

/// <summary>
/// A single similarity search hit returned from the vector store, shaped to match
/// what rag_api / LibreChat expects back from `/query`.
/// </summary>
public sealed class SearchResult
{
    public required string Id { get; init; }
    public required string PageContent { get; init; }
    public required double Score { get; init; }
    public Dictionary<string, object?> Metadata { get; init; } = new();
}
