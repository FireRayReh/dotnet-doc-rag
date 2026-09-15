using Cryptex.Core.Interfaces;
using Cryptex.Core.Models;

namespace Cryptex.Core.Chunking;

public sealed class ChunkingOptions
{
    /// <summary>Maximum number of tokens per chunk.</summary>
    public int MaxTokens { get; set; } = 512;

    /// <summary>Number of tokens of overlap carried over between consecutive chunks.</summary>
    public int OverlapTokens { get; set; } = 64;
}

/// <summary>
/// Token-aware chunker: splits document text on paragraph/sentence boundaries where possible,
/// packs them into windows bounded by <see cref="ChunkingOptions.MaxTokens"/>, and carries
/// <see cref="ChunkingOptions.OverlapTokens"/> of trailing context into the next chunk so
/// retrieval doesn't lose context at chunk boundaries.
/// </summary>
public sealed class DocumentChunker
{
    private readonly TiktokenTokenCounter _tokenCounter;
    private readonly ChunkingOptions _options;

    public DocumentChunker(TiktokenTokenCounter tokenCounter, ChunkingOptions options)
    {
        _tokenCounter = tokenCounter;
        _options = options;
    }

    public IReadOnlyList<DocumentChunk> Chunk(ParsedDocument document, string fileId, IReadOnlyDictionary<string, object?>? baseMetadata = null)
    {
        if (_options.MaxTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(_options.MaxTokens), "MaxTokens must be > 0");
        if (_options.OverlapTokens < 0 || _options.OverlapTokens >= _options.MaxTokens)
            throw new ArgumentOutOfRangeException(nameof(_options.OverlapTokens), "OverlapTokens must be >= 0 and < MaxTokens");

        var chunks = new List<DocumentChunk>();
        int chunkIndex = 0;

        if (document.Segments is { Count: > 0 })
        {
            foreach (var segment in document.Segments)
            {
                foreach (var windowText in SplitIntoWindows(segment.Text))
                {
                    chunks.Add(BuildChunk(windowText, fileId, chunkIndex++, baseMetadata, segment.Label, segment.Index));
                }
            }
        }
        else
        {
            foreach (var windowText in SplitIntoWindows(document.Text))
            {
                chunks.Add(BuildChunk(windowText, fileId, chunkIndex++, baseMetadata, null, null));
            }
        }

        return chunks;
    }

    private DocumentChunk BuildChunk(string text, string fileId, int index, IReadOnlyDictionary<string, object?>? baseMetadata, string? segmentLabel, int? segmentIndex)
    {
        var metadata = new Dictionary<string, object?>();
        if (baseMetadata != null)
        {
            foreach (var kvp in baseMetadata) metadata[kvp.Key] = kvp.Value;
        }
        metadata["file_id"] = fileId;
        metadata["chunk_index"] = index;
        if (segmentLabel != null) metadata["segment"] = segmentLabel;
        if (segmentIndex != null) metadata["segment_index"] = segmentIndex;

        return new DocumentChunk
        {
            FileId = fileId,
            ChunkIndex = index,
            Content = text,
            TokenCount = _tokenCounter.CountTokens(text),
            Metadata = metadata
        };
    }

    /// <summary>
    /// Splits a block of text into token-bounded windows using paragraph -> sentence -> hard-token
    /// fallback splitting, with overlap carried between windows.
    /// </summary>
    private IEnumerable<string> SplitIntoWindows(string text)
    {
        text = text?.Trim() ?? string.Empty;
        if (text.Length == 0) yield break;

        var units = SplitIntoUnits(text);
        if (units.Count == 0) yield break;

        var current = new List<string>();
        var currentTokens = 0;

        foreach (var unit in units)
        {
            var unitTokens = _tokenCounter.CountTokens(unit);

            // A single unit larger than MaxTokens must be hard-split.
            if (unitTokens > _options.MaxTokens)
            {
                if (current.Count > 0)
                {
                    yield return string.Join(" ", current);
                    current = CarryOverlap(current);
                    currentTokens = _tokenCounter.CountTokens(string.Join(" ", current));
                }

                foreach (var piece in HardSplit(unit))
                    yield return piece;

                continue;
            }

            if (currentTokens + unitTokens > _options.MaxTokens && current.Count > 0)
            {
                yield return string.Join(" ", current);
                current = CarryOverlap(current);
                currentTokens = _tokenCounter.CountTokens(string.Join(" ", current));
            }

            current.Add(unit);
            currentTokens += unitTokens;
        }

        if (current.Count > 0)
            yield return string.Join(" ", current);
    }

    /// <summary>Keeps trailing units from the previous window totalling roughly OverlapTokens, to seed the next window.</summary>
    private List<string> CarryOverlap(List<string> previous)
    {
        if (_options.OverlapTokens == 0) return new List<string>();

        var carried = new List<string>();
        var tokens = 0;
        for (int i = previous.Count - 1; i >= 0; i--)
        {
            var t = _tokenCounter.CountTokens(previous[i]);
            if (tokens + t > _options.OverlapTokens && carried.Count > 0) break;
            carried.Insert(0, previous[i]);
            tokens += t;
            if (tokens >= _options.OverlapTokens) break;
        }
        return carried;
    }

    private static List<string> SplitIntoUnits(string text)
    {
        // First split on blank-line-delimited paragraphs; then split any oversized paragraph into sentences.
        var paragraphs = text.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
        var units = new List<string>();
        foreach (var p in paragraphs)
        {
            var trimmed = p.Trim();
            if (trimmed.Length == 0) continue;
            units.AddRange(SplitIntoSentences(trimmed));
        }
        return units;
    }

    private static IEnumerable<string> SplitIntoSentences(string paragraph)
    {
        // Simple sentence boundary heuristic: split after '.', '!', '?' followed by whitespace + uppercase/digit,
        // but keep it cheap and dependency-free. Falls back to the whole paragraph if no boundaries found.
        var sentences = new List<string>();
        int start = 0;
        for (int i = 0; i < paragraph.Length; i++)
        {
            char c = paragraph[i];
            if ((c == '.' || c == '!' || c == '?') && i + 1 < paragraph.Length && char.IsWhiteSpace(paragraph[i + 1]))
            {
                var sentence = paragraph[start..(i + 1)].Trim();
                if (sentence.Length > 0) sentences.Add(sentence);
                start = i + 1;
            }
        }
        if (start < paragraph.Length)
        {
            var tail = paragraph[start..].Trim();
            if (tail.Length > 0) sentences.Add(tail);
        }
        return sentences.Count > 0 ? sentences : new List<string> { paragraph };
    }

    /// <summary>Last-resort splitter for a single unit (e.g. one huge sentence/word run) that alone exceeds MaxTokens.
    /// Splits on token offsets directly so we never exceed the budget.</summary>
    private IEnumerable<string> HardSplit(string unit)
    {
        var offsets = _tokenCounter.GetTokenOffsets(unit);
        if (offsets.Count == 0)
        {
            yield return unit;
            yield break;
        }

        int i = 0;
        while (i < offsets.Count)
        {
            int end = Math.Min(i + _options.MaxTokens, offsets.Count);
            var startChar = offsets[i].Start;
            var lastOffset = offsets[end - 1];
            var endChar = lastOffset.Start + lastOffset.Length;
            yield return unit[startChar..endChar];

            if (end >= offsets.Count) break;

            // advance with overlap in token terms
            int step = Math.Max(1, _options.MaxTokens - _options.OverlapTokens);
            i += step;
        }
    }
}
