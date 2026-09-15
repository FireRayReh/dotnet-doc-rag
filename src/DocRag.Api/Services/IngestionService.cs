using DocRag.Core.Chunking;
using DocRag.Core.Interfaces;
using DocRag.Core.Models;
using DocRag.Ingestion;
using DocRag.Security;

namespace DocRag.Api.Services;

public sealed class IngestionResult
{
    public required IngestedDocument Document { get; init; }
    public required int ChunkCount { get; init; }
}

/// <summary>
/// Orchestrates the full ingestion pipeline shared by both the per-chat upload endpoint
/// (RagApiController) and the bulk admin folder ingest endpoint (AdminController):
/// parse -&gt; chunk -&gt; embed -&gt; upsert into the vector store -&gt; record in the document registry.
/// </summary>
public sealed class IngestionService
{
    private readonly DocumentParserFactory _parserFactory;
    private readonly DocumentChunker _chunker;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly IVectorStore _vectorStore;
    private readonly DocumentRegistry _registry;
    private readonly IDocumentCipher? _cipher;
    private readonly ILogger<IngestionService> _logger;

    public IngestionService(
        DocumentParserFactory parserFactory,
        DocumentChunker chunker,
        IEmbeddingProvider embeddingProvider,
        IVectorStore vectorStore,
        DocumentRegistry registry,
        ILogger<IngestionService> logger,
        IDocumentCipher? cipher = null)
    {
        _parserFactory = parserFactory;
        _chunker = chunker;
        _embeddingProvider = embeddingProvider;
        _vectorStore = vectorStore;
        _registry = registry;
        _cipher = cipher;
        _logger = logger;
    }

    public async Task<IngestionResult> IngestAsync(
        Stream content,
        string fileName,
        string? fileId,
        string? userId,
        string? password,
        string sourceLabel,
        CancellationToken cancellationToken = default)
    {
        fileId ??= Guid.NewGuid().ToString("N");

        var document = new IngestedDocument
        {
            FileId = fileId,
            FileName = fileName,
            Format = DocumentParserFactory.GetExtension(fileName),
            UserId = userId,
            Status = DocumentStatus.Processing
        };
        _registry.Upsert(document);

        try
        {
            // Buffer to a seekable MemoryStream: parsers (and the CFB/encryption sniffing) need Seek,
            // which an incoming multipart/HTTP request stream does not reliably support.
            using var seekable = await ToSeekableAsync(content, cancellationToken);

            if (_registry.RetainOriginals && _cipher != null)
            {
                await PersistOriginalAsync(seekable, fileId, fileName, cancellationToken);
            }

            seekable.Position = 0;
            var parsed = await _parserFactory.ParseAsync(seekable, fileName, password, cancellationToken);

            var baseMetadata = new Dictionary<string, object?>
            {
                ["source"] = sourceLabel,
                ["file_name"] = fileName,
            };
            if (userId != null) baseMetadata["user_id"] = userId;
            foreach (var kvp in parsed.Metadata) baseMetadata[kvp.Key] = kvp.Value;

            var chunks = _chunker.Chunk(parsed, fileId, baseMetadata);
            if (chunks.Count == 0)
            {
                _logger.LogWarning("Document {FileName} ({FileId}) produced zero chunks (empty content?).", fileName, fileId);
            }
            else
            {
                var embeddings = await _embeddingProvider.EmbedAsync(chunks.Select(c => c.Content).ToList(), cancellationToken);
                for (int i = 0; i < chunks.Count; i++) chunks[i].Embedding = embeddings[i];

                await _vectorStore.EnsureCollectionAsync(_embeddingProvider.Dimensions, cancellationToken);
                await _vectorStore.UpsertAsync(chunks, cancellationToken);
            }

            document.Status = DocumentStatus.Completed;
            document.ChunkCount = chunks.Count;
            document.WasEncrypted = parsed.Metadata.TryGetValue("was_encrypted", out var enc) && enc is true;
            document.Error = null;
            _registry.Upsert(document);

            return new IngestionResult { Document = document, ChunkCount = chunks.Count };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ingest {FileName} ({FileId}).", fileName, fileId);
            document.Status = DocumentStatus.Failed;
            document.Error = ex.Message;
            _registry.Upsert(document);
            throw;
        }
    }

    public async Task DeleteAsync(string fileId, CancellationToken cancellationToken = default)
    {
        await _vectorStore.DeleteByFileIdAsync(fileId, cancellationToken);
        _registry.Remove(fileId);
    }

    public async Task DeleteManyAsync(IReadOnlyCollection<string> fileIds, CancellationToken cancellationToken = default)
    {
        await _vectorStore.DeleteByFileIdsAsync(fileIds, cancellationToken);
        foreach (var id in fileIds) _registry.Remove(id);
    }

    private async Task PersistOriginalAsync(MemoryStream seekable, string fileId, string fileName, CancellationToken cancellationToken)
    {
        var path = _registry.OriginalPathFor(fileId, fileName);
        if (path is null || _cipher is null) return;

        seekable.Position = 0;
        var plainBytes = seekable.ToArray();
        var cipherBytes = _cipher.Encrypt(plainBytes);
        await File.WriteAllBytesAsync(path, cipherBytes, cancellationToken);
    }

    private static async Task<MemoryStream> ToSeekableAsync(Stream content, CancellationToken cancellationToken)
    {
        var ms = new MemoryStream();
        await content.CopyToAsync(ms, cancellationToken);
        ms.Position = 0;
        return ms;
    }
}
