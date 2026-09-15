using System.Text.Json;
using DocRag.Core.Models;

namespace DocRag.Api.Services;

public sealed class DocumentRegistryOptions
{
    public const string SectionName = "DocumentRegistry";

    /// <summary>Path to the JSON file used to persist ingested-document metadata across restarts.</summary>
    public string StorePath { get; set; } = "data/documents.json";

    /// <summary>Directory where original file bytes are optionally retained (encrypted at rest) to support re-indexing.</summary>
    public string OriginalsPath { get; set; } = "data/originals";

    /// <summary>Whether to retain original file bytes for later re-indexing. Off by default to minimize storage/PII footprint.</summary>
    public bool RetainOriginals { get; set; } = false;
}

/// <summary>
/// Lightweight, file-backed registry of <see cref="IngestedDocument"/> metadata. This deliberately
/// avoids pulling in a full database dependency for a greenfield service whose durable state
/// (the actual chunk vectors) already lives in Qdrant - this registry only tracks bookkeeping
/// (file name, status, chunk counts, timestamps) needed for the admin/list/reindex endpoints.
/// Thread-safe via a simple in-process lock; adequate for a single-instance deployment. For
/// multi-instance deployments, swap this for a real database-backed implementation of the same
/// surface without touching callers.
/// </summary>
public sealed class DocumentRegistry
{
    private readonly DocumentRegistryOptions _options;
    private readonly object _lock = new();
    private Dictionary<string, IngestedDocument> _documents = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public DocumentRegistry(DocumentRegistryOptions options)
    {
        _options = options;
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_options.StorePath))
            {
                var json = File.ReadAllText(_options.StorePath);
                var docs = JsonSerializer.Deserialize<List<IngestedDocument>>(json, JsonOptions) ?? new();
                _documents = docs.ToDictionary(d => d.FileId);
            }
        }
        catch (IOException)
        {
            _documents = new();
        }
        catch (JsonException)
        {
            _documents = new();
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_options.StorePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(_documents.Values.ToList(), JsonOptions);
        File.WriteAllText(_options.StorePath, json);
    }

    public void Upsert(IngestedDocument document)
    {
        lock (_lock)
        {
            _documents[document.FileId] = document;
            Save();
        }
    }

    public IngestedDocument? Get(string fileId)
    {
        lock (_lock)
        {
            return _documents.TryGetValue(fileId, out var doc) ? doc : null;
        }
    }

    public IReadOnlyList<IngestedDocument> List(string? userId = null)
    {
        lock (_lock)
        {
            var query = _documents.Values.AsEnumerable();
            if (!string.IsNullOrEmpty(userId)) query = query.Where(d => d.UserId == userId);
            return query.OrderByDescending(d => d.IngestedAtUtc).ToList();
        }
    }

    public void Remove(string fileId)
    {
        lock (_lock)
        {
            _documents.Remove(fileId);
            Save();
        }
        TryDeleteOriginal(fileId);
    }

    public string? OriginalPathFor(string fileId, string fileName)
    {
        if (!_options.RetainOriginals) return null;
        Directory.CreateDirectory(_options.OriginalsPath);
        return Path.Combine(_options.OriginalsPath, $"{fileId}{Path.GetExtension(fileName)}.enc");
    }

    public bool RetainOriginals => _options.RetainOriginals;

    private void TryDeleteOriginal(string fileId)
    {
        if (!_options.RetainOriginals) return;
        try
        {
            if (Directory.Exists(_options.OriginalsPath))
            {
                foreach (var f in Directory.EnumerateFiles(_options.OriginalsPath, $"{fileId}.*"))
                    File.Delete(f);
            }
        }
        catch (IOException) { /* best-effort cleanup */ }
    }
}
