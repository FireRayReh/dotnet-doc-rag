using DocRag.Core.Models;

namespace DocRag.Core.Interfaces;

/// <summary>
/// Abstraction over the vector database used for chunk storage and similarity search.
/// </summary>
public interface IVectorStore
{
    /// <summary>Ensures the backing collection exists with the right vector size/distance metric.</summary>
    Task EnsureCollectionAsync(int vectorSize, CancellationToken cancellationToken = default);

    /// <summary>Inserts or updates the given chunks (each must already carry an <see cref="DocumentChunk.Embedding"/>).</summary>
    Task UpsertAsync(IReadOnlyList<DocumentChunk> chunks, CancellationToken cancellationToken = default);

    /// <summary>Runs a similarity search for <paramref name="queryVector"/>, optionally filtered by file ids.</summary>
    Task<IReadOnlyList<SearchResult>> SearchAsync(
        float[] queryVector,
        int topK,
        IReadOnlyCollection<string>? fileIds = null,
        double? scoreThreshold = null,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes all chunks belonging to the given file id.</summary>
    Task DeleteByFileIdAsync(string fileId, CancellationToken cancellationToken = default);

    /// <summary>Deletes all chunks belonging to any of the given file ids.</summary>
    Task DeleteByFileIdsAsync(IReadOnlyCollection<string> fileIds, CancellationToken cancellationToken = default);

    /// <summary>Fetches all chunks belonging to a file id (used by admin/detail endpoints).</summary>
    Task<IReadOnlyList<SearchResult>> GetByFileIdAsync(string fileId, CancellationToken cancellationToken = default);

    /// <summary>Returns distinct file ids currently stored, with chunk counts.</summary>
    Task<IReadOnlyDictionary<string, int>> ListFileIdsAsync(CancellationToken cancellationToken = default);
}
