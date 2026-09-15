using Azure;
using Azure.AI.OpenAI;
using Cryptex.Core.Interfaces;
using Microsoft.Extensions.Logging;
using OpenAI.Embeddings;

namespace Cryptex.Embeddings;

/// <summary>
/// Embedding provider backed by Azure OpenAI. The deployment name is fully configurable so newer
/// embedding models (e.g. future text-embedding-3-* successors) can be swapped in purely via config.
/// </summary>
public sealed class AzureOpenAIEmbeddingProvider : IEmbeddingProvider
{
    private readonly EmbeddingClient _client;
    private readonly ILogger<AzureOpenAIEmbeddingProvider>? _logger;

    public string ModelName { get; }
    public int Dimensions { get; }

    public AzureOpenAIEmbeddingProvider(AzureOpenAIEmbeddingOptions options, ILogger<AzureOpenAIEmbeddingProvider>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint))
            throw new ArgumentException("Embedding:AzureOpenAI:Endpoint must be configured.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new ArgumentException("Embedding:AzureOpenAI:ApiKey must be configured.", nameof(options));

        var azureClient = new AzureOpenAIClient(new Uri(options.Endpoint), new AzureKeyCredential(options.ApiKey));
        _client = azureClient.GetEmbeddingClient(options.Deployment);
        ModelName = options.Deployment;
        Dimensions = options.Dimensions;
        _logger = logger;
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0) return Array.Empty<float[]>();

        var options = new EmbeddingGenerationOptions { Dimensions = Dimensions };
        var response = await _client.GenerateEmbeddingsAsync(texts, options, cancellationToken);
        return response.Value.Select(e => e.ToFloats().ToArray()).ToList();
    }

    public async Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await EmbedAsync(new[] { text }, cancellationToken);
        return result[0];
    }
}
