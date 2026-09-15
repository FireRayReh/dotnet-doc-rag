using Cryptex.Core.Chunking;
using Cryptex.Core.Interfaces;
using Xunit;

namespace Cryptex.Tests.Chunking;

public class DocumentChunkerTests
{
    private static DocumentChunker CreateChunker(int maxTokens = 32, int overlapTokens = 8)
    {
        var counter = new TiktokenTokenCounter("gpt-4o");
        var options = new ChunkingOptions { MaxTokens = maxTokens, OverlapTokens = overlapTokens };
        return new DocumentChunker(counter, options);
    }

    [Fact]
    public void Chunk_EmptyDocument_ProducesNoChunks()
    {
        var chunker = CreateChunker();
        var doc = new ParsedDocument { Text = "" };

        var chunks = chunker.Chunk(doc, "file-1");

        Assert.Empty(chunks);
    }

    [Fact]
    public void Chunk_ShortDocument_ProducesSingleChunk()
    {
        var chunker = CreateChunker();
        var doc = new ParsedDocument { Text = "This is a short sentence about a company policy." };

        var chunks = chunker.Chunk(doc, "file-1");

        Assert.Single(chunks);
        Assert.Equal("file-1", chunks[0].FileId);
        Assert.Contains("company policy", chunks[0].Content);
    }

    [Fact]
    public void Chunk_LongDocument_RespectsMaxTokenBudget()
    {
        var chunker = CreateChunker(maxTokens: 20, overlapTokens: 4);
        var paragraph = string.Join(" ", Enumerable.Repeat("The quick brown fox jumps over the lazy dog.", 30));
        var doc = new ParsedDocument { Text = paragraph };
        var counter = new TiktokenTokenCounter("gpt-4o");

        var chunks = chunker.Chunk(doc, "file-1");

        Assert.True(chunks.Count > 1, "Expected the long document to be split into multiple chunks.");
        foreach (var chunk in chunks)
        {
            Assert.True(counter.CountTokens(chunk.Content) <= 20 + 2 /* small slack for join whitespace */,
                $"Chunk exceeded token budget: {counter.CountTokens(chunk.Content)} tokens.");
        }
    }

    [Fact]
    public void Chunk_ConsecutiveChunks_CarryOverlap()
    {
        var chunker = CreateChunker(maxTokens: 15, overlapTokens: 5);
        var sentences = Enumerable.Range(1, 20).Select(i => $"Sentence number {i} describes topic {i}.");
        var doc = new ParsedDocument { Text = string.Join(" ", sentences) };

        var chunks = chunker.Chunk(doc, "file-1");

        Assert.True(chunks.Count > 1);
        // Overlap means the start of chunk N+1 should share some trailing text with chunk N.
        for (int i = 0; i < chunks.Count - 1; i++)
        {
            var lastWordsOfCurrent = chunks[i].Content.Split(' ').TakeLast(3);
            var combinedNext = chunks[i + 1].Content;
            Assert.True(lastWordsOfCurrent.Any(w => combinedNext.Contains(w)),
                "Expected some overlap between consecutive chunks.");
        }
    }

    [Fact]
    public void Chunk_AssignsSequentialChunkIndex()
    {
        var chunker = CreateChunker(maxTokens: 10, overlapTokens: 2);
        var doc = new ParsedDocument { Text = string.Join(" ", Enumerable.Range(1, 50).Select(i => $"word{i}")) };

        var chunks = chunker.Chunk(doc, "file-1");

        for (int i = 0; i < chunks.Count; i++)
        {
            Assert.Equal(i, chunks[i].ChunkIndex);
            Assert.Equal(i, chunks[i].Metadata["chunk_index"]);
        }
    }

    [Fact]
    public void Chunk_UsesSegmentsWhenPresent()
    {
        var chunker = CreateChunker();
        var doc = new ParsedDocument
        {
            Text = "ignored",
            Segments = new[]
            {
                new ParsedSegment { Index = 0, Text = "Page one content.", Label = "page 1" },
                new ParsedSegment { Index = 1, Text = "Page two content.", Label = "page 2" }
            }
        };

        var chunks = chunker.Chunk(doc, "file-1");

        Assert.Equal(2, chunks.Count);
        Assert.Equal("page 1", chunks[0].Metadata["segment"]);
        Assert.Equal("page 2", chunks[1].Metadata["segment"]);
    }
}
