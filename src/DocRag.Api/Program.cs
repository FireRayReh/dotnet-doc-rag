using DocRag.Api.Services;
using DocRag.Core.Chunking;
using DocRag.Core.Interfaces;
using DocRag.Embeddings;
using DocRag.Ingestion;
using DocRag.Ingestion.Parsers;
using DocRag.Security;
using DocRag.VectorStore;
using Microsoft.OpenApi;
using OfficeOpenXml;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------------------------
// Configuration binding
// ---------------------------------------------------------------------------------------------

var embeddingOptions = new EmbeddingOptions();
builder.Configuration.GetSection(EmbeddingOptions.SectionName).Bind(embeddingOptions);

var rerankerOptions = new RerankerOptions();
builder.Configuration.GetSection(RerankerOptions.SectionName).Bind(rerankerOptions);

var qdrantOptions = new QdrantOptions();
builder.Configuration.GetSection(QdrantOptions.SectionName).Bind(qdrantOptions);

var chunkingOptions = new ChunkingOptions();
builder.Configuration.GetSection("Chunking").Bind(chunkingOptions);

var registryOptions = new DocumentRegistryOptions();
builder.Configuration.GetSection(DocumentRegistryOptions.SectionName).Bind(registryOptions);

var encryptionKeyEnvVar = builder.Configuration["Security:EncryptionKeyEnvVar"] ?? "DOC_ENCRYPTION_KEY";
var encryptionKey = Environment.GetEnvironmentVariable(encryptionKeyEnvVar);

// EPPlus 8 requires the license to be set once at process startup before any ExcelPackage is used.
ExcelPackage.License.SetNonCommercialOrganization("DocRag self-hosted deployment");

// ---------------------------------------------------------------------------------------------
// Core services
// ---------------------------------------------------------------------------------------------

builder.Services.AddSingleton(embeddingOptions);
builder.Services.AddSingleton(rerankerOptions);
builder.Services.AddSingleton(qdrantOptions);
builder.Services.AddSingleton(chunkingOptions);
builder.Services.AddSingleton(registryOptions);

builder.Services.AddSingleton(new TiktokenTokenCounter("gpt-4o"));
builder.Services.AddSingleton<DocumentChunker>();

builder.Services.AddSingleton<PdfParser>();
builder.Services.AddSingleton<OpenXmlParser>();
builder.Services.AddSingleton<EncryptedXlsxParser>();
builder.Services.AddSingleton<PlainTextParser>();
builder.Services.AddSingleton<HtmlParser>();
builder.Services.AddSingleton<OfficeCryptoDecryptor>();
builder.Services.AddSingleton<DocumentParserFactory>();

AesGcmDocumentCipher? documentCipher = null;
if (!string.IsNullOrWhiteSpace(encryptionKey))
{
    try
    {
        documentCipher = new AesGcmDocumentCipher(encryptionKey);
    }
    catch (ArgumentException) when (!registryOptions.RetainOriginals)
    {
        // A placeholder/invalid key is harmless as long as nothing actually needs at-rest
        // encryption yet (RetainOriginals off). Surface this loudly once logging is available
        // below rather than crashing the whole service on startup.
    }
}

if (registryOptions.RetainOriginals && documentCipher is null)
{
    throw new InvalidOperationException(
        $"DocumentRegistry:RetainOriginals is enabled but {encryptionKeyEnvVar} is not set to a valid " +
        "base64-encoded 256-bit key (see AesGcmDocumentCipher.GenerateKey()). Set it before starting.");
}

if (documentCipher != null)
{
    builder.Services.AddSingleton<IDocumentCipher>(documentCipher);
}

builder.Services.AddHttpClient<LocalEmbeddingProvider>();
builder.Services.AddSingleton<IEmbeddingProvider>(sp =>
{
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    return EmbeddingProviderFactory.Create(embeddingOptions, httpClientFactory.CreateClient(nameof(LocalEmbeddingProvider)), loggerFactory);
});

builder.Services.AddHttpClient<SimpleCrossEncoderReranker>();
builder.Services.AddSingleton<IReranker>(sp =>
{
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var logger = sp.GetService<ILogger<SimpleCrossEncoderReranker>>();
    var client = string.IsNullOrWhiteSpace(rerankerOptions.BaseUrl) ? null : httpClientFactory.CreateClient(nameof(SimpleCrossEncoderReranker));
    return new SimpleCrossEncoderReranker(rerankerOptions, client, logger);
});

builder.Services.AddSingleton<IVectorStore>(sp =>
    new QdrantVectorStore(qdrantOptions, sp.GetService<ILogger<QdrantVectorStore>>()));

builder.Services.AddSingleton(sp => new DocumentRegistry(registryOptions));
builder.Services.AddSingleton<IngestionService>();

// ---------------------------------------------------------------------------------------------
// ASP.NET Core services
// ---------------------------------------------------------------------------------------------

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "DocRag API",
        Version = "v1",
        Description = "RAG backend implementing the LibreChat rag_api contract, plus admin endpoints for bulk document ingestion."
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthorization();
app.MapControllers();

app.Run();

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program { }
