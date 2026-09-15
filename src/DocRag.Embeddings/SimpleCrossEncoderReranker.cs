using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocRag.Core.Interfaces;
using DocRag.Core.Models;
using Microsoft.Extensions.Logging;

namespace DocRag.Embeddings;

public sealed class RerankerOptions
{
    public const string SectionName = "Reranker";

    /// <summary>When empty/null, reranking is disabled and results pass through unchanged.</summary>
    public string? BaseUrl { get; set; }
    public string Model { get; set; } = "bge-reranker-v2-m3";
}

/// <summary>
/// Re-scores vector-search candidates using a local cross-encoder rerank endpoint
/// (e.g. an OpenAI-compatible /rerank route served by TEI, vLLM, or Infinity). If no
/// <see cref="RerankerOptions.BaseUrl"/> is configured, acts as a no-op passthrough so reranking
/// remains fully optional.
/// </summary>
public sealed class SimpleCrossEncoderReranker : IReranker
{
    private readonly HttpClient? _httpClient;
    private readonly RerankerOptions _options;
    private readonly ILogger<SimpleCrossEncoderReranker>? _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public SimpleCrossEncoderReranker(RerankerOptions options, HttpClient? httpClient = null, ILogger<SimpleCrossEncoderReranker>? logger = null)
    {
        _options = options;
        _httpClient = httpClient;
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(options.BaseUrl) && httpClient != null)
            httpClient.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
    }

    public async Task<IReadOnlyList<SearchResult>> RerankAsync(string query, IReadOnlyList<SearchResult> candidates, int topK, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl) || _httpClient is null || candidates.Count == 0)
            return candidates.Take(topK).ToList();

        try
        {
            var request = new RerankRequest(_options.Model, query, candidates.Select(c => c.PageContent).ToArray());
            using var response = await _httpClient.PostAsJsonAsync("rerank", request, JsonOptions, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Reranker endpoint returned {Status}; falling back to unranked results.", response.StatusCode);
                return candidates.Take(topK).ToList();
            }

            var parsed = await response.Content.ReadFromJsonAsync<RerankResponse>(JsonOptions, cancellationToken);
            if (parsed?.Results is null || parsed.Results.Count == 0)
                return candidates.Take(topK).ToList();

            return parsed.Results
                .OrderByDescending(r => r.Score)
                .Take(topK)
                .Select(r => candidates[r.Index])
                .ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Reranker call failed; falling back to unranked results.");
            return candidates.Take(topK).ToList();
        }
    }

    private sealed record RerankRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("documents")] string[] Documents);

    private sealed record RerankResponse([property: JsonPropertyName("results")] List<RerankResultItem> Results);

    private sealed record RerankResultItem(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("relevance_score")] double Score);
}
