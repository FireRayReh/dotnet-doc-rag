using Cryptex.Api.Contracts;
using Cryptex.Api.Services;
using Cryptex.Core.Interfaces;
using Cryptex.Ingestion;
using Cryptex.VectorStore;
using Microsoft.AspNetCore.Mvc;

namespace Cryptex.Api.Controllers;

/// <summary>
/// Administrative endpoints for bulk-ingesting a folder of company documents (as opposed to
/// per-chat uploads through RagApiController), listing what has been indexed, re-indexing, and
/// checking service status.
/// </summary>
[ApiController]
[Route("/admin")]
public sealed class AdminController : ControllerBase
{
    private readonly IngestionService _ingestionService;
    private readonly DocumentRegistry _registry;
    private readonly DocumentParserFactory _parserFactory;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly IVectorStore _vectorStore;
    private readonly QdrantOptions _qdrantOptions;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        IngestionService ingestionService,
        DocumentRegistry registry,
        DocumentParserFactory parserFactory,
        IEmbeddingProvider embeddingProvider,
        IVectorStore vectorStore,
        QdrantOptions qdrantOptions,
        ILogger<AdminController> logger)
    {
        _ingestionService = ingestionService;
        _registry = registry;
        _parserFactory = parserFactory;
        _embeddingProvider = embeddingProvider;
        _vectorStore = vectorStore;
        _qdrantOptions = qdrantOptions;
        _logger = logger;
    }

    /// <summary>Bulk-ingests every supported file under a folder (e.g. a mounted company documents share).</summary>
    [HttpPost("ingest-folder")]
    public async Task<ActionResult<BulkIngestResponse>> IngestFolder([FromBody] BulkIngestRequest request, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(request.Path))
            return NotFound($"Folder not found: {request.Path}");

        var searchOption = request.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        // Encrypted envelopes carry an opaque extension (.enc/.aes) that no parser claims, so they
        // have to be picked up explicitly alongside the natively-supported formats.
        var files = Directory.EnumerateFiles(request.Path, "*", searchOption)
            .Where(f => request.ForceEnvelope
                        || _parserFactory.IsSupported(f)
                        || _ingestionService.IsEnvelopeCandidate(f))
            .ToList();

        var results = new List<BulkIngestFileResult>();
        int succeeded = 0, failed = 0;

        foreach (var filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(filePath);
            var password = request.PasswordsByFile?.GetValueOrDefault(fileName) ?? request.Password;
            var envelopeKeyId = request.EnvelopeKeyIdsByFile?.GetValueOrDefault(fileName) ?? request.EnvelopeKeyId;

            try
            {
                await using var fs = System.IO.File.OpenRead(filePath);
                var result = await _ingestionService.IngestAsync(
                    fs, fileName, fileId: null, userId: null, password,
                    sourceLabel: $"bulk-folder:{request.Path}",
                    envelopeKeyId: envelopeKeyId,
                    forceEnvelope: request.ForceEnvelope,
                    cancellationToken: cancellationToken);

                results.Add(new BulkIngestFileResult
                {
                    FileName = fileName,
                    FileId = result.Document.FileId,
                    Status = "succeeded",
                    Chunks = result.ChunkCount
                });
                succeeded++;
            }
            catch (EnvelopeDecryptionException)
            {
                // Same uniform, opaque failure as the /embed path - no padding-vs-MAC-vs-key detail.
                _logger.LogInformation("Envelope decryption rejected {FilePath} during bulk ingest.", filePath);
                results.Add(new BulkIngestFileResult { FileName = fileName, Status = "failed", Error = EnvelopeDecryptionException.UniformMessage });
                failed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bulk ingest failed for {FilePath}.", filePath);
                results.Add(new BulkIngestFileResult { FileName = fileName, Status = "failed", Error = ex.Message });
                failed++;
            }
        }

        return Ok(new BulkIngestResponse
        {
            TotalFiles = files.Count,
            Succeeded = succeeded,
            Failed = failed,
            Results = results
        });
    }

    /// <summary>Lists all documents currently tracked in the registry (per-chat uploads and bulk-ingested alike).</summary>
    [HttpGet("documents")]
    public ActionResult<List<DocumentSummary>> ListDocuments([FromQuery] string? userId)
    {
        var docs = _registry.List(userId).Select(DocumentSummary.FromDocument).ToList();
        return Ok(docs);
    }

    [HttpGet("documents/{id}")]
    public ActionResult<DocumentSummary> GetDocument(string id)
    {
        var doc = _registry.Get(id);
        if (doc is null) return NotFound();
        return Ok(DocumentSummary.FromDocument(doc));
    }

    /// <summary>
    /// Re-runs the ingestion pipeline for a previously ingested document, using its retained
    /// original bytes if <c>DocumentRegistry:RetainOriginals</c> is enabled. Without retained
    /// originals there is nothing to re-parse from, so this returns 409 in that case - re-upload
    /// (or re-run the bulk folder ingest) instead.
    /// </summary>
    [HttpPost("reindex/{id}")]
    public async Task<ActionResult<BulkIngestFileResult>> Reindex(string id, [FromQuery] string? password, CancellationToken cancellationToken)
    {
        var doc = _registry.Get(id);
        if (doc is null) return NotFound();

        if (!_registry.RetainOriginals)
            return Conflict("Re-indexing requires DocumentRegistry:RetainOriginals=true so original file bytes are available; re-upload the file instead.");

        return StatusCode(501, "Re-indexing from retained originals is not yet wired to a decrypt-and-replay path; re-upload the file to refresh its embeddings.");
    }

    [HttpGet("status")]
    public async Task<ActionResult<StatusResponse>> Status(CancellationToken cancellationToken)
    {
        var docs = _registry.List();
        var fileIdCounts = await _vectorStore.ListFileIdsAsync(cancellationToken);

        return Ok(new StatusResponse
        {
            DocumentCount = docs.Count,
            TotalChunks = fileIdCounts.Values.Sum(),
            EmbeddingProvider = _embeddingProvider.GetType().Name,
            EmbeddingModel = _embeddingProvider.ModelName,
            EmbeddingDimensions = _embeddingProvider.Dimensions,
            VectorStoreCollection = _qdrantOptions.CollectionName
        });
    }
}
