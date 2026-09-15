namespace DocRag.Core.Interfaces;

/// <summary>
/// Produces embedding vectors for text, backed by either a hosted (Azure OpenAI) or local
/// OpenAI-compatible endpoint. Implementations are selected at runtime via configuration so the
/// embedding model can be swapped without code changes.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>Logical name of the underlying model (e.g. "text-embedding-3-large", "bge-m3").</summary>
    string ModelName { get; }

    /// <summary>Dimensionality of vectors produced by this provider.</summary>
    int Dimensions { get; }

    /// <summary>Embeds a batch of texts, returning one vector per input in the same order.</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);

    /// <summary>Embeds a single query string (may apply query-specific instructions/prefixes for some models).</summary>
    Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken = default);
}
