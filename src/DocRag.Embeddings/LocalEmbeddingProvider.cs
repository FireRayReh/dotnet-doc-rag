using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocRag.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace DocRag.Embeddings;

/// <summary>
/// Embedding provider for any OpenAI-compatible local server (Ollama, vLLM, llama.cpp server, LM
/// Studio, etc.) that exposes POST {BaseUrl}/embeddings. Talks plain HTTP/JSON so it has no
/// dependency on a specific vendor SDK, keeping it easy to point at whichever model is running.
/// </summary>
public sealed class LocalEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<LocalEmbeddingProvider>? _logger;

    public string ModelName { get; }
    public int Dimensions { get; }

    public LocalEmbeddingProvider(HttpClient httpClient, LocalEmbeddingOptions options, ILogger<LocalEmbeddingProvider>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
            throw new ArgumentException("Embedding:Local:BaseUrl must be configured.", nameof(options));

        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        ModelName = options.Model;
        Dimensions = options.Dimensions;
        _logger = logger;
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0) return Array.Empty<float[]>();

        var request = new EmbeddingRequest(ModelName, texts.ToArray());
        using var response = await _httpClient.PostAsJsonAsync("embeddings", request, JsonOptions, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger?.LogError("Local embedding endpoint returned {Status}: {Body}", response.StatusCode, body);
            throw new HttpRequestException($"Local embedding endpoint returned {(int)response.StatusCode}: {body}");
        }

        var parsed = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Local embedding endpoint returned an empty/invalid response.");

        return parsed.Data
            .OrderBy(d => d.Index)
            .Select(d => d.Embedding)
            .ToList();
    }

    public async Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await EmbedAsync(new[] { text }, cancellationToken);
        return result[0];
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record EmbeddingRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string[] Input);

    private sealed record EmbeddingResponse(
        [property: JsonPropertyName("data")] List<EmbeddingData> Data);

    private sealed record EmbeddingData(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("embedding")] float[] Embedding);
}
