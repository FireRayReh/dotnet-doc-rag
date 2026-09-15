namespace Cryptex.VectorStore;

public sealed class QdrantOptions
{
    public const string SectionName = "VectorStore:Qdrant";

    /// <summary>Qdrant gRPC endpoint, e.g. "localhost" or "qdrant.internal". Host only (no scheme) unless UseHttps controls scheme resolution downstream.</summary>
    public string Host { get; set; } = "localhost";
    public int GrpcPort { get; set; } = 6334;
    public bool UseHttps { get; set; } = false;
    public string? ApiKey { get; set; }
    public string CollectionName { get; set; } = "cryptex_chunks";
}
