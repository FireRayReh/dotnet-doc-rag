using System.Text.Json.Serialization;
using DocRag.Core.Models;

namespace DocRag.Api.Contracts;

public sealed class BulkIngestRequest
{
    /// <summary>Absolute or container-relative path to a folder of documents to ingest (e.g. a mounted company docs share).</summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("recursive")]
    public bool Recursive { get; init; } = true;

    /// <summary>Optional password applied to every encrypted file found, when all documents in the folder share one password.</summary>
    [JsonPropertyName("password")]
    public string? Password { get; init; }

    /// <summary>Optional per-file-name password overrides, for folders with mixed passwords.</summary>
    [JsonPropertyName("passwords_by_file")]
    public Dictionary<string, string>? PasswordsByFile { get; init; }
}

public sealed class BulkIngestResponse
{
    [JsonPropertyName("total_files")]
    public int TotalFiles { get; init; }

    [JsonPropertyName("succeeded")]
    public int Succeeded { get; init; }

    [JsonPropertyName("failed")]
    public int Failed { get; init; }

    [JsonPropertyName("results")]
    public List<BulkIngestFileResult> Results { get; init; } = new();
}

public sealed class BulkIngestFileResult
{
    [JsonPropertyName("file_name")]
    public required string FileName { get; init; }

    [JsonPropertyName("file_id")]
    public string? FileId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("chunks")]
    public int Chunks { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

public sealed class DocumentSummary
{
    [JsonPropertyName("file_id")]
    public required string FileId { get; init; }

    [JsonPropertyName("file_name")]
    public required string FileName { get; init; }

    [JsonPropertyName("format")]
    public required string Format { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("chunk_count")]
    public int ChunkCount { get; init; }

    [JsonPropertyName("was_encrypted")]
    public bool WasEncrypted { get; init; }

    [JsonPropertyName("ingested_at")]
    public DateTimeOffset IngestedAt { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    public static DocumentSummary FromDocument(IngestedDocument d) => new()
    {
        FileId = d.FileId,
        FileName = d.FileName,
        Format = d.Format,
        Status = d.Status.ToString(),
        ChunkCount = d.ChunkCount,
        WasEncrypted = d.WasEncrypted,
        IngestedAt = d.IngestedAtUtc,
        Error = d.Error
    };
}

public sealed class StatusResponse
{
    [JsonPropertyName("document_count")]
    public int DocumentCount { get; init; }

    [JsonPropertyName("total_chunks")]
    public int TotalChunks { get; init; }

    [JsonPropertyName("embedding_provider")]
    public required string EmbeddingProvider { get; init; }

    [JsonPropertyName("embedding_model")]
    public required string EmbeddingModel { get; init; }

    [JsonPropertyName("embedding_dimensions")]
    public int EmbeddingDimensions { get; init; }

    [JsonPropertyName("vector_store_collection")]
    public required string VectorStoreCollection { get; init; }
}
