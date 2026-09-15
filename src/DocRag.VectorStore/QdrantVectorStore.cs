using DocRag.Core.Interfaces;
using DocRag.Core.Models;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace DocRag.VectorStore;

/// <summary>
/// <see cref="IVectorStore"/> implementation backed by Qdrant, using the official Qdrant.Client
/// gRPC client. Performs dense-vector similarity search with an optional payload filter on
/// "file_id". The installed Qdrant.Client version's high-level API only exposes dense-vector
/// search/upsert helpers; hybrid dense+sparse search would additionally require configuring a
/// named sparse vector on the collection and issuing a fused Query request (see QueryAsync in the
/// client) - left as a documented extension point rather than implemented here, since the
/// dense-only path already satisfies the rag_api contract this service implements.
/// </summary>
public sealed class QdrantVectorStore : IVectorStore
{
    private readonly QdrantClient _client;
    private readonly QdrantOptions _options;
    private readonly ILogger<QdrantVectorStore>? _logger;

    public QdrantVectorStore(QdrantOptions options, ILogger<QdrantVectorStore>? logger = null)
    {
        _options = options;
        _client = new QdrantClient(options.Host, options.GrpcPort, options.UseHttps, options.ApiKey);
        _logger = logger;
    }

    public async Task EnsureCollectionAsync(int vectorSize, CancellationToken cancellationToken = default)
    {
        var exists = await _client.CollectionExistsAsync(_options.CollectionName, cancellationToken);
        if (exists)
        {
            _logger?.LogDebug("Qdrant collection '{Collection}' already exists.", _options.CollectionName);
            return;
        }

        _logger?.LogInformation("Creating Qdrant collection '{Collection}' (dim={Dim}).", _options.CollectionName, vectorSize);
        await _client.CreateCollectionAsync(
            _options.CollectionName,
            new VectorParams { Size = (ulong)vectorSize, Distance = Distance.Cosine },
            cancellationToken: cancellationToken);
    }

    public async Task UpsertAsync(IReadOnlyList<DocumentChunk> chunks, CancellationToken cancellationToken = default)
    {
        if (chunks.Count == 0) return;

        var points = chunks.Select(ToPointStruct).ToList();
        await _client.UpsertAsync(_options.CollectionName, points, wait: true, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        float[] queryVector,
        int topK,
        IReadOnlyCollection<string>? fileIds = null,
        double? scoreThreshold = null,
        CancellationToken cancellationToken = default)
    {
        Filter? filter = null;
        if (fileIds is { Count: > 0 })
        {
            filter = new Filter();
            filter.Must.Add(Conditions.Match("file_id", fileIds.ToList()));
        }

        // SearchAsync is marked obsolete in favor of the more general QueryAsync, but remains fully
        // supported and is simpler for a plain dense-vector top-k search than building a Query object.
#pragma warning disable CS0618
        var results = await _client.SearchAsync(
            _options.CollectionName,
            queryVector,
            filter: filter,
            limit: (ulong)topK,
            scoreThreshold: scoreThreshold.HasValue ? (float)scoreThreshold.Value : null,
            payloadSelector: true,
            cancellationToken: cancellationToken);
#pragma warning restore CS0618

        return results.Select(ToSearchResult).ToList();
    }

    public async Task DeleteByFileIdAsync(string fileId, CancellationToken cancellationToken = default)
    {
        var filter = new Filter();
        filter.Must.Add(Conditions.MatchKeyword("file_id", fileId));
        await _client.DeleteAsync(_options.CollectionName, filter, cancellationToken: cancellationToken);
    }

    public async Task DeleteByFileIdsAsync(IReadOnlyCollection<string> fileIds, CancellationToken cancellationToken = default)
    {
        if (fileIds.Count == 0) return;
        var filter = new Filter();
        filter.Must.Add(Conditions.Match("file_id", fileIds.ToList()));
        await _client.DeleteAsync(_options.CollectionName, filter, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<SearchResult>> GetByFileIdAsync(string fileId, CancellationToken cancellationToken = default)
    {
        var filter = new Filter();
        filter.Must.Add(Conditions.MatchKeyword("file_id", fileId));

        var results = new List<SearchResult>();
        PointId? offset = null;
        while (true)
        {
            var scrolled = await _client.ScrollAsync(
                _options.CollectionName,
                filter,
                limit: 256,
                offset: offset,
                payloadSelector: true,
                cancellationToken: cancellationToken);

            results.AddRange(scrolled.Result.Select(ToSearchResult));

            if (scrolled.NextPageOffset is null || scrolled.Result.Count == 0) break;
            offset = scrolled.NextPageOffset;
        }
        return results;
    }

    public async Task<IReadOnlyDictionary<string, int>> ListFileIdsAsync(CancellationToken cancellationToken = default)
    {
        var counts = new Dictionary<string, int>();
        PointId? offset = null;
        while (true)
        {
            var scrolled = await _client.ScrollAsync(
                _options.CollectionName,
                limit: 512,
                offset: offset,
                payloadSelector: new[] { "file_id" },
                cancellationToken: cancellationToken);

            foreach (var point in scrolled.Result)
            {
                if (point.Payload.TryGetValue("file_id", out var value))
                {
                    var fileId = value.StringValue;
                    counts[fileId] = counts.GetValueOrDefault(fileId) + 1;
                }
            }

            if (scrolled.NextPageOffset is null || scrolled.Result.Count == 0) break;
            offset = scrolled.NextPageOffset;
        }
        return counts;
    }

    private static PointStruct ToPointStruct(DocumentChunk chunk)
    {
        var point = new PointStruct
        {
            Id = new PointId { Uuid = chunk.Id },
            Vectors = chunk.Embedding ?? throw new InvalidOperationException($"Chunk {chunk.Id} has no embedding to upsert.")
        };

        point.Payload["file_id"] = chunk.FileId;
        point.Payload["chunk_index"] = chunk.ChunkIndex;
        point.Payload["page_content"] = chunk.Content;
        point.Payload["token_count"] = chunk.TokenCount;

        foreach (var kvp in chunk.Metadata)
        {
            point.Payload[kvp.Key] = ToQdrantValue(kvp.Value);
        }

        return point;
    }

    private static Value ToQdrantValue(object? value) => value switch
    {
        null => new Value { NullValue = Qdrant.Client.Grpc.NullValue.NullValue },
        string s => s,
        bool b => b,
        int i => i,
        long l => l,
        double d => d,
        float f => (double)f,
        _ => value.ToString() ?? string.Empty
    };

    private static SearchResult ToSearchResult(ScoredPoint point)
    {
        var metadata = point.Payload.ToDictionary(kv => kv.Key, kv => (object?)FromQdrantValue(kv.Value));
        var content = point.Payload.TryGetValue("page_content", out var c) ? c.StringValue : string.Empty;
        return new SearchResult
        {
            Id = point.Id.Uuid,
            PageContent = content,
            Score = point.Score,
            Metadata = metadata
        };
    }

    private static SearchResult ToSearchResult(RetrievedPoint point)
    {
        var metadata = point.Payload.ToDictionary(kv => kv.Key, kv => (object?)FromQdrantValue(kv.Value));
        var content = point.Payload.TryGetValue("page_content", out var c) ? c.StringValue : string.Empty;
        return new SearchResult
        {
            Id = point.Id.Uuid,
            PageContent = content,
            Score = 1.0,
            Metadata = metadata
        };
    }

    private static object? FromQdrantValue(Value v) => v.KindCase switch
    {
        Value.KindOneofCase.StringValue => v.StringValue,
        Value.KindOneofCase.IntegerValue => v.IntegerValue,
        Value.KindOneofCase.DoubleValue => v.DoubleValue,
        Value.KindOneofCase.BoolValue => v.BoolValue,
        Value.KindOneofCase.NullValue => null,
        _ => v.ToString()
    };
}
