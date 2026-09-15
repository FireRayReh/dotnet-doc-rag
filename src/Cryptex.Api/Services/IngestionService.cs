using Cryptex.Core.Chunking;
using Cryptex.Core.Interfaces;
using Cryptex.Core.Models;
using Cryptex.Ingestion;
using Cryptex.Security;
using Cryptex.Security.Envelope;

namespace Cryptex.Api.Services;

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
    private readonly IEnvelopeDecryptor? _envelopeDecryptor;
    private readonly EnvelopeOptions? _envelopeOptions;
    private readonly OfficeCryptoDecryptor _officeCryptoDecryptor;
    private readonly ILogger<IngestionService> _logger;

    public IngestionService(
        DocumentParserFactory parserFactory,
        DocumentChunker chunker,
        IEmbeddingProvider embeddingProvider,
        IVectorStore vectorStore,
        DocumentRegistry registry,
        OfficeCryptoDecryptor officeCryptoDecryptor,
        ILogger<IngestionService> logger,
        IDocumentCipher? cipher = null,
        IEnvelopeDecryptor? envelopeDecryptor = null,
        EnvelopeOptions? envelopeOptions = null)
    {
        _parserFactory = parserFactory;
        _chunker = chunker;
        _embeddingProvider = embeddingProvider;
        _vectorStore = vectorStore;
        _registry = registry;
        _officeCryptoDecryptor = officeCryptoDecryptor;
        _cipher = cipher;
        _envelopeDecryptor = envelopeDecryptor;
        _envelopeOptions = envelopeOptions;
        _logger = logger;
    }

    /// <summary>
    /// True when this file name carries one of the configured encrypted-envelope extensions
    /// (<c>Envelope:EncryptedExtensions</c>, default <c>.enc</c>/<c>.aes</c>) and envelope handling
    /// is available. Used by the bulk folder ingest to pick up files no parser would claim.
    /// </summary>
    public bool IsEnvelopeCandidate(string fileName) =>
        _envelopeDecryptor is not null
        && _envelopeOptions is { Enabled: true }
        && _envelopeOptions.IsEncryptedFileName(fileName);

    public async Task<IngestionResult> IngestAsync(
        Stream content,
        string fileName,
        string? fileId,
        string? userId,
        string? password,
        string sourceLabel,
        string? envelopeKeyId = null,
        bool forceEnvelope = false,
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

            // Customer-encrypted envelope: decrypt to plaintext bytes in memory, then work out what
            // the plaintext actually is. Everything downstream (parser factory, parsers) stays
            // completely unaware that encryption was ever involved.
            Stream parseSource = seekable;
            var parseFileName = fileName;
            MemoryStream? decrypted = null;
            var envelopeApplied = false;
            Dictionary<string, object?>? envelopeMetadata = null;

            if (forceEnvelope || IsEnvelopeCandidate(fileName))
            {
                if (_envelopeDecryptor is null || _envelopeOptions is not { Enabled: true })
                    throw new NotSupportedException(
                        "Envelope decryption was requested but is not enabled; set Envelope:Enabled and configure at least one key.");

                (decrypted, parseFileName, envelopeMetadata) =
                    DecryptEnvelope(seekable, fileName, envelopeKeyId, password);
                parseSource = decrypted;
                envelopeApplied = true;
                document.Format = DocumentParserFactory.GetExtension(parseFileName);
            }

            ParsedDocument parsed;
            try
            {
                parsed = await _parserFactory.ParseAsync(parseSource, parseFileName, password, cancellationToken);
            }
            finally
            {
                decrypted?.Dispose();
            }

            var baseMetadata = new Dictionary<string, object?>
            {
                ["source"] = sourceLabel,
                ["file_name"] = fileName,
            };
            if (userId != null) baseMetadata["user_id"] = userId;
            if (envelopeMetadata != null)
            {
                foreach (var kvp in envelopeMetadata) baseMetadata[kvp.Key] = kvp.Value;
            }
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
            document.WasEncrypted = envelopeApplied || (parsed.Metadata.TryGetValue("was_encrypted", out var enc) && enc is true);
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

    /// <summary>
    /// Decrypts a customer-encrypted envelope entirely in memory and resolves what the recovered
    /// plaintext really is, returning a stream to parse plus the synthetic file name (with the true
    /// extension) that the parser factory should dispatch on.
    /// </summary>
    /// <remarks>
    /// Plaintext is never written to disk. Every decryption failure surfaces as the single opaque
    /// <see cref="EnvelopeDecryptionException"/>, so no caller can tell a wrong key from bad padding
    /// from a failed MAC - see the remarks on <see cref="AesCbcEnvelopeDecryptor"/>.
    /// </remarks>
    private (MemoryStream Content, string FileName, Dictionary<string, object?> Metadata) DecryptEnvelope(
        MemoryStream envelope,
        string fileName,
        string? keyId,
        string? password)
    {
        var extensions = _envelopeOptions!.EncryptedExtensions;

        envelope.Position = 0;
        var plaintext = _envelopeDecryptor!.Decrypt(envelope.ToArray(), keyId);

        // Everything past this point must fail as the SAME opaque error the decryptor raises.
        // "Valid padding but unrecognisable plaintext" reaching a caller as a distinguishable
        // response re-creates the padding oracle the uniform error exists to deny: an attacker
        // submitting chosen ciphertexts learns padding validity from the error alone.
        try
        {
            var sniffed = FormatSniffer.Sniff(plaintext, fileName, extensions);

            if (sniffed.Extension == FormatSniffer.CompoundFileFormat)
            {
                // An OLE2/CFB compound file: for Office content this is an MS-OFFCRYPTO-encrypted
                // OOXML container, so hand it to OfficeCryptoDecryptor and sniff the result again.
                if (string.IsNullOrEmpty(password))
                    throw new NotSupportedException(
                        "The decrypted content is an encrypted Office (OLE2/CFB) container; supply the document password to open it.");

                using var cfb = new MemoryStream(plaintext, writable: false);
                using var inner = _officeCryptoDecryptor.Decrypt(cfb, password);
                plaintext = inner.ToArray();
                sniffed = FormatSniffer.Sniff(plaintext, fileName, extensions);

                if (sniffed.Extension == FormatSniffer.CompoundFileFormat)
                    throw new FormatDetectionException("The decrypted Office container did not yield a recognisable OOXML package.");
            }

            var resolvedName = BuildSyntheticFileName(fileName, sniffed.Extension, extensions);

            _logger.LogInformation(
                "Decrypted envelope {FileName} ({Bytes} bytes plaintext); detected format '{Format}' via {Source}.",
                fileName, plaintext.Length, sniffed.Extension, sniffed.Source);

            var metadata = new Dictionary<string, object?>
            {
                ["envelope_decrypted"] = true,
                ["detected_format"] = sniffed.Extension,
                ["format_detection_source"] = sniffed.Source.ToString()
            };

            return (new MemoryStream(plaintext, writable: false), resolvedName, metadata);
        }
        catch (Exception ex)
        {
            // Logged server-side so an operator can still diagnose a genuine format or
            // missing-password problem; the caller is told nothing beyond "it didn't work".
            _logger.LogInformation(
                ex,
                "Envelope {FileName}: decryption succeeded but the plaintext could not be resolved to a parsable document.",
                fileName);
            throw new EnvelopeDecryptionException(null, ex);
        }
    }

    /// <summary>
    /// Produces the file name the parser factory dispatches on: the original name with the
    /// encrypted wrapper extension stripped, ensured to end in the detected format's extension.
    /// </summary>
    internal static string BuildSyntheticFileName(string fileName, string detectedExtension, IReadOnlyCollection<string>? encryptedExtensions)
    {
        var stripped = FormatSniffer.StripEncryptedExtension(
            string.IsNullOrWhiteSpace(fileName) ? "document" : fileName, encryptedExtensions);

        var existing = Path.GetExtension(stripped).TrimStart('.').ToLowerInvariant();
        var alreadyCorrect = existing == detectedExtension
            || (detectedExtension == "html" && existing == "htm")
            || (detectedExtension == "txt" && existing is "txt" or "md" or "markdown");

        return alreadyCorrect ? stripped : stripped + "." + detectedExtension;
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
