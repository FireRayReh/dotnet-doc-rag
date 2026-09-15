// ============================================================================================
// LibreChat `rag_api` contract shapes.
//
// This file is the single source of truth for the wire shapes LibreChat's backend expects when
// RAG_API_URL points at this service, mirroring the conventions documented by the reference
// implementation (https://github.com/danny-avila/rag_api). If LibreChat's actual OpenAPI spec
// could not be fetched at build time, these are reasonable/documented conventions inferred from
// that project's README and source; if LibreChat's behavior turns out to differ in your
// deployment, this is the only file you should need to touch.
//
// Endpoints implemented against this contract (see RagApiController):
//   GET    /health                    -> HealthResponse
//   POST   /embed                     -> multipart/form-data (file [+ file_id, user_id]) -> EmbedResponse
//   POST   /query                     -> QueryRequest -> QueryResult[]
//   DELETE /documents/{id}            -> DeleteResponse
//   DELETE /documents                 -> DeleteManyRequest -> DeleteResponse
//   GET    /documents/{id}            -> DocumentChunksResponse
// ============================================================================================

using System.Text.Json.Serialization;

namespace DocRag.Api.Contracts;

public sealed class HealthResponse
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "UP";
}

public sealed class EmbedResponse
{
    [JsonPropertyName("status")]
    public bool Status { get; init; }

    [JsonPropertyName("file_id")]
    public required string FileId { get; init; }

    [JsonPropertyName("filename")]
    public required string FileName { get; init; }

    [JsonPropertyName("chunks")]
    public int Chunks { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

public sealed class QueryRequest
{
    /// <summary>The natural-language query text to embed and search for.</summary>
    [JsonPropertyName("query")]
    public required string Query { get; init; }

    /// <summary>Restrict the search to these file ids. LibreChat sends the ids of files attached to the
    /// current conversation/message. Accepts either a single id or a list.</summary>
    [JsonPropertyName("file_id")]
    public List<string>? FileId { get; init; }

    /// <summary>Number of results to return (top-k).</summary>
    [JsonPropertyName("k")]
    public int K { get; init; } = 4;

    /// <summary>Optional minimum similarity score threshold.</summary>
    [JsonPropertyName("score_threshold")]
    public double? ScoreThreshold { get; init; }
}

/// <summary>One matched chunk. rag_api returns page_content/metadata pairs with a similarity score.</summary>
public sealed class QueryResult
{
    [JsonPropertyName("page_content")]
    public required string PageContent { get; init; }

    [JsonPropertyName("metadata")]
    public required Dictionary<string, object?> Metadata { get; init; }

    [JsonPropertyName("score")]
    public double Score { get; init; }
}

public sealed class DeleteManyRequest
{
    [JsonPropertyName("file_ids")]
    public required List<string> FileIds { get; init; }
}

public sealed class DeleteResponse
{
    [JsonPropertyName("status")]
    public bool Status { get; init; }

    [JsonPropertyName("deleted")]
    public List<string> Deleted { get; init; } = new();
}

public sealed class DocumentChunksResponse
{
    [JsonPropertyName("file_id")]
    public required string FileId { get; init; }

    [JsonPropertyName("chunks")]
    public required List<QueryResult> Chunks { get; init; }
}
