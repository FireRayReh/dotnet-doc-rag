using DocRag.Api.Contracts;
using DocRag.Api.Services;
using DocRag.Core.Interfaces;
using DocRag.Core.Models;
using DocRag.Security;
using Microsoft.AspNetCore.Mvc;

namespace DocRag.Api.Controllers;

/// <summary>
/// Implements the LibreChat `rag_api` HTTP contract (see Contracts/RagApiContracts.cs) so this
/// service can be dropped in as the RAG_API_URL backend for LibreChat's file upload / RAG UI.
/// </summary>
[ApiController]
public sealed class RagApiController : ControllerBase
{
    private readonly IngestionService _ingestionService;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly IVectorStore _vectorStore;
    private readonly IReranker _reranker;
    private readonly ILogger<RagApiController> _logger;

    public RagApiController(
        IngestionService ingestionService,
        IEmbeddingProvider embeddingProvider,
        IVectorStore vectorStore,
        IReranker reranker,
        ILogger<RagApiController> logger)
    {
        _ingestionService = ingestionService;
        _embeddingProvider = embeddingProvider;
        _vectorStore = vectorStore;
        _reranker = reranker;
        _logger = logger;
    }

    [HttpGet("/health")]
    public ActionResult<HealthResponse> Health() => Ok(new HealthResponse { Status = "UP" });

    /// <summary>
    /// Accepts a per-chat file upload from LibreChat, ingests it (parse -&gt; chunk -&gt; embed -&gt;
    /// store), and reports how many chunks were produced. Optional form fields: file_id (LibreChat's
    /// id for the uploaded file; generated if omitted), user_id, password (for encrypted documents).
    /// </summary>
    [HttpPost("/embed")]
    [RequestSizeLimit(200_000_000)]
    public async Task<ActionResult<EmbedResponse>> Embed(
        [FromForm] IFormFile file,
        [FromForm] string? file_id,
        [FromForm] string? user_id,
        [FromForm] string? password,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new EmbedResponse { Status = false, FileId = file_id ?? string.Empty, FileName = file?.FileName ?? string.Empty, Error = "No file was uploaded." });

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await _ingestionService.IngestAsync(
                stream, file.FileName, file_id, user_id, password, sourceLabel: "librechat-upload", cancellationToken);

            return Ok(new EmbedResponse
            {
                Status = true,
                FileId = result.Document.FileId,
                FileName = result.Document.FileName,
                Chunks = result.ChunkCount
            });
        }
        catch (InvalidDocumentPasswordException ex)
        {
            return Conflict(new EmbedResponse { Status = false, FileId = file_id ?? string.Empty, FileName = file.FileName, Error = ex.Message });
        }
        catch (UnsupportedOfficeEncryptionException ex)
        {
            return UnprocessableEntity(new EmbedResponse { Status = false, FileId = file_id ?? string.Empty, FileName = file.FileName, Error = ex.Message });
        }
        catch (NotSupportedException ex)
        {
            return UnprocessableEntity(new EmbedResponse { Status = false, FileId = file_id ?? string.Empty, FileName = file.FileName, Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Embed failed for {FileName}.", file.FileName);
            return StatusCode(500, new EmbedResponse { Status = false, FileId = file_id ?? string.Empty, FileName = file.FileName, Error = "Internal error while embedding the document." });
        }
    }

    /// <summary>Similarity search over stored chunks, optionally filtered to a set of file ids.</summary>
    [HttpPost("/query")]
    public async Task<ActionResult<List<QueryResult>>> Query([FromBody] QueryRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest("query is required.");

        var queryVector = await _embeddingProvider.EmbedQueryAsync(request.Query, cancellationToken);

        // Over-fetch a bit before reranking so the reranker has a meaningful candidate pool
        // (SimpleCrossEncoderReranker is a no-op passthrough when no reranker endpoint is configured).
        var fetchCount = Math.Max(request.K * 4, request.K);
        var hits = await _vectorStore.SearchAsync(queryVector, fetchCount, request.FileId, request.ScoreThreshold, cancellationToken);
        var reranked = await _reranker.RerankAsync(request.Query, hits, request.K, cancellationToken);

        return Ok(reranked.Select(ToQueryResult).ToList());
    }

    [HttpDelete("/documents/{id}")]
    public async Task<ActionResult<DeleteResponse>> DeleteOne(string id, CancellationToken cancellationToken)
    {
        await _ingestionService.DeleteAsync(id, cancellationToken);
        return Ok(new DeleteResponse { Status = true, Deleted = new List<string> { id } });
    }

    [HttpDelete("/documents")]
    public async Task<ActionResult<DeleteResponse>> DeleteMany([FromBody] DeleteManyRequest request, CancellationToken cancellationToken)
    {
        if (request.FileIds.Count == 0)
            return BadRequest("file_ids is required.");

        await _ingestionService.DeleteManyAsync(request.FileIds, cancellationToken);
        return Ok(new DeleteResponse { Status = true, Deleted = request.FileIds });
    }

    [HttpGet("/documents/{id}")]
    public async Task<ActionResult<DocumentChunksResponse>> GetDocument(string id, CancellationToken cancellationToken)
    {
        var chunks = await _vectorStore.GetByFileIdAsync(id, cancellationToken);
        if (chunks.Count == 0) return NotFound();

        return Ok(new DocumentChunksResponse
        {
            FileId = id,
            Chunks = chunks.Select(ToQueryResult).ToList()
        });
    }

    private static QueryResult ToQueryResult(SearchResult r) => new()
    {
        PageContent = r.PageContent,
        Metadata = r.Metadata,
        Score = r.Score
    };
}
