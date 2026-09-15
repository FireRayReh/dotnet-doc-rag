using System.Text;
using Cryptex.Api.Services;
using Cryptex.Core.Chunking;
using Cryptex.Core.Interfaces;
using Cryptex.Core.Models;
using Cryptex.Ingestion;
using Cryptex.Ingestion.Parsers;
using Cryptex.Security;
using Cryptex.Security.Envelope;
using Cryptex.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cryptex.Tests.Ingestion;

/// <summary>
/// End-to-end proof that a file encrypted by the customer's own C# code (plain AES-CBC, IV
/// prepended) is decrypted, format-sniffed and parsed by the ordinary parsers - which stay
/// completely unaware that encryption was ever involved - for plain text, a real PDF and a real
/// DOCX generated in-test.
/// </summary>
public class EnvelopeIngestionPipelineTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("cryptex-envelope-tests").FullName;
    private readonly string _keyBase64 = CustomerEnvelopeEncryptor.GenerateKeyBase64();
    private readonly List<string> _capturedChunks = new();

    [Fact]
    public async Task IngestAsync_DecryptsSniffsAndParses_APlainTextEnvelope()
    {
        const string body = "Cryptex ingestion note.\n\nThe parsers never learn this file was encrypted.";
        var blob = CustomerEnvelopeEncryptor.Encrypt(Encoding.UTF8.GetBytes(body), _keyBase64);

        var document = await IngestAsync(blob, "meeting-notes.txt.enc");

        Assert.Equal(DocumentStatus.Completed, document.Status);
        Assert.Equal("txt", document.Format);
        Assert.True(document.WasEncrypted);
        Assert.Contains("never learn this file was encrypted", string.Join("\n", _capturedChunks));
    }

    [Fact]
    public async Task IngestAsync_DecryptsSniffsAndParses_APdfEnvelope()
    {
        const string marker = "Cryptex round trip through a real PDF";
        var blob = CustomerEnvelopeEncryptor.Encrypt(DocumentFixtures.CreatePdf(marker), _keyBase64);

        // No inner extension at all: the format can only come from the PDF magic bytes.
        var document = await IngestAsync(blob, "blob-00417.enc");

        Assert.Equal(DocumentStatus.Completed, document.Status);
        Assert.Equal("pdf", document.Format);
        Assert.True(document.WasEncrypted);
        Assert.Contains(marker, string.Join("\n", _capturedChunks));
    }

    [Fact]
    public async Task IngestAsync_DecryptsSniffsAndParses_ADocxEnvelope()
    {
        const string marker = "Cryptex round trip through a real DOCX";
        var blob = CustomerEnvelopeEncryptor.Encrypt(DocumentFixtures.CreateDocx(marker, "Second paragraph."), _keyBase64);

        var document = await IngestAsync(blob, "handbook.aes");

        Assert.Equal(DocumentStatus.Completed, document.Status);
        Assert.Equal("docx", document.Format);
        Assert.True(document.WasEncrypted);
        Assert.Contains(marker, string.Join("\n", _capturedChunks));
    }

    [Fact]
    public async Task IngestAsync_WithForceEnvelope_DecryptsAFileWithNoEncryptedExtension()
    {
        const string body = "Forced envelope handling for a file named like anything at all.";
        var blob = CustomerEnvelopeEncryptor.Encrypt(Encoding.UTF8.GetBytes(body), _keyBase64);

        var document = await IngestAsync(blob, "opaque-payload", forceEnvelope: true);

        Assert.Equal(DocumentStatus.Completed, document.Status);
        Assert.Equal("txt", document.Format);
        Assert.Contains("Forced envelope handling", string.Join("\n", _capturedChunks));
    }

    [Fact]
    public async Task IngestAsync_WithKeyRotation_UsesTheRequestedKeyId()
    {
        var legacyKey = CustomerEnvelopeEncryptor.GenerateKeyBase64();
        var blob = CustomerEnvelopeEncryptor.Encrypt(Encoding.UTF8.GetBytes("archived under the legacy key"), legacyKey);

        var service = BuildService(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["current"] = _keyBase64,
            ["legacy"] = legacyKey
        }, defaultKeyId: "current");

        using var stream = new MemoryStream(blob);
        var result = await service.IngestAsync(
            stream, "archive.txt.enc", fileId: null, userId: null, password: null,
            sourceLabel: "test", envelopeKeyId: "legacy");

        Assert.Equal(DocumentStatus.Completed, result.Document.Status);
        Assert.Contains("archived under the legacy key", string.Join("\n", _capturedChunks));
    }

    [Fact]
    public async Task IngestAsync_WithTheWrongKey_FailsWithTheUniformOpaqueError()
    {
        var blob = CustomerEnvelopeEncryptor.Encrypt(Encoding.UTF8.GetBytes("secret"), CustomerEnvelopeEncryptor.GenerateKeyBase64());

        var service = BuildService(new Dictionary<string, string> { ["default"] = _keyBase64 });

        using var stream = new MemoryStream(blob);
        var ex = await Assert.ThrowsAsync<EnvelopeDecryptionException>(() => service.IngestAsync(
            stream, "secret.txt.enc", fileId: null, userId: null, password: null, sourceLabel: "test"));

        Assert.Equal(EnvelopeDecryptionException.UniformMessage, ex.Message);
    }

    /// <summary>
    /// Padding-oracle regression at the pipeline boundary, one layer above
    /// <see cref="AesCbcEnvelopeDecryptor"/>'s own uniform-error guarantee. Decryption <i>succeeding</i>
    /// but the recovered plaintext being unrecognisable must be indistinguishable from decryption
    /// failing outright. If these two diverge, an attacker submitting chosen ciphertexts learns
    /// whether the padding was valid from the error alone - which is the entire padding oracle,
    /// reconstructed from a format error. Fix the code, not the test.
    /// </summary>
    [Fact]
    public async Task IngestAsync_ValidKeyButUnrecognisablePlaintext_FailsIdenticallyToAWrongKey()
    {
        // Decrypts cleanly under the real key, but is no format the sniffer can identify: control
        // bytes only, no magic prefix, and a file name carrying no inner extension to fall back on.
        var unrecognisable = new byte[256];
        for (var i = 0; i < unrecognisable.Length; i++) unrecognisable[i] = (byte)(i % 7 + 1);

        var decryptsButUnparsable = CustomerEnvelopeEncryptor.Encrypt(unrecognisable, _keyBase64);
        var wrongKeyBlob = CustomerEnvelopeEncryptor.Encrypt(
            Encoding.UTF8.GetBytes("a genuine document"), CustomerEnvelopeEncryptor.GenerateKeyBase64());

        var service = BuildService(new Dictionary<string, string> { ["default"] = _keyBase64 });

        using var unparsableStream = new MemoryStream(decryptsButUnparsable);
        var sniffFailure = await Record.ExceptionAsync(() => service.IngestAsync(
            unparsableStream, "blob-00931.enc", fileId: null, userId: null, password: null, sourceLabel: "test"));

        using var wrongKeyStream = new MemoryStream(wrongKeyBlob);
        var decryptFailure = await Record.ExceptionAsync(() => service.IngestAsync(
            wrongKeyStream, "blob-00932.enc", fileId: null, userId: null, password: null, sourceLabel: "test"));

        Assert.IsType<EnvelopeDecryptionException>(sniffFailure);
        Assert.IsType<EnvelopeDecryptionException>(decryptFailure);
        Assert.Equal(decryptFailure.Message, sniffFailure.Message);
        Assert.Equal(EnvelopeDecryptionException.UniformMessage, sniffFailure.Message);
    }

    [Fact]
    public void IsEnvelopeCandidate_MatchesOnlyTheConfiguredEncryptedExtensions()
    {
        var service = BuildService(new Dictionary<string, string> { ["default"] = _keyBase64 });

        Assert.True(service.IsEnvelopeCandidate("/data/docs/report.pdf.enc"));
        Assert.True(service.IsEnvelopeCandidate("/data/docs/report.pdf.AES"));
        Assert.False(service.IsEnvelopeCandidate("/data/docs/report.pdf"));
    }

    private async Task<IngestedDocument> IngestAsync(byte[] blob, string fileName, bool forceEnvelope = false)
    {
        var service = BuildService(new Dictionary<string, string> { ["default"] = _keyBase64 });
        using var stream = new MemoryStream(blob);
        var result = await service.IngestAsync(
            stream, fileName, fileId: null, userId: null, password: null,
            sourceLabel: "test", forceEnvelope: forceEnvelope);
        return result.Document;
    }

    private IngestionService BuildService(Dictionary<string, string> keys, string defaultKeyId = "default")
    {
        var envelopeOptions = new EnvelopeOptions
        {
            Enabled = true,
            DefaultKeyId = defaultKeyId,
            Keys = new Dictionary<string, string>(keys, StringComparer.OrdinalIgnoreCase)
        };

        var embeddings = new Mock<IEmbeddingProvider>();
        embeddings.SetupGet(e => e.Dimensions).Returns(3);
        embeddings.SetupGet(e => e.ModelName).Returns("test-model");
        embeddings
            .Setup(e => e.EmbedAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, CancellationToken _) =>
            {
                _capturedChunks.AddRange(texts);
                return texts.Select(_ => new float[] { 0.1f, 0.2f, 0.3f }).ToList();
            });

        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(v => v.EnsureCollectionAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        vectorStore.Setup(v => v.UpsertAsync(It.IsAny<IReadOnlyList<DocumentChunk>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var officeCrypto = new OfficeCryptoDecryptor();
        var parserFactory = new DocumentParserFactory(
            new PdfParser(), new OpenXmlParser(), new EncryptedXlsxParser(),
            new PlainTextParser(), new HtmlParser(), officeCrypto);

        var registry = new DocumentRegistry(new DocumentRegistryOptions
        {
            StorePath = Path.Combine(_tempDir, $"documents-{Guid.NewGuid():N}.json"),
            OriginalsPath = Path.Combine(_tempDir, "originals"),
            RetainOriginals = false
        });

        return new IngestionService(
            parserFactory,
            new DocumentChunker(new TiktokenTokenCounter("gpt-4o"), new ChunkingOptions()),
            embeddings.Object,
            vectorStore.Object,
            registry,
            officeCrypto,
            NullLogger<IngestionService>.Instance,
            cipher: null,
            envelopeDecryptor: new AesCbcEnvelopeDecryptor(envelopeOptions),
            envelopeOptions: envelopeOptions);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }
}
