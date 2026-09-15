using DocRag.Core.Models;

namespace DocRag.Core.Interfaces;

/// <summary>
/// Optionally re-scores/re-orders initial vector search hits using a cross-encoder.
/// Implementations that have no reranker configured should simply return the input unchanged.
/// </summary>
public interface IReranker
{
    Task<IReadOnlyList<SearchResult>> RerankAsync(string query, IReadOnlyList<SearchResult> candidates, int topK, CancellationToken cancellationToken = default);
}
