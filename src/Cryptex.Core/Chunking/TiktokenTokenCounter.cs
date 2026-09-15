using Microsoft.ML.Tokenizers;

namespace Cryptex.Core.Chunking;

/// <summary>
/// Token counter backed by <see cref="TiktokenTokenizer"/> (the same BPE family used by
/// OpenAI's cl100k/o200k encodings), giving a realistic token-budget estimate regardless of
/// which embedding model actually processes the text.
/// </summary>
public sealed class TiktokenTokenCounter : ITokenCounter
{
    private readonly TiktokenTokenizer _tokenizer;

    public TiktokenTokenCounter(string modelName = "gpt-4o")
    {
        _tokenizer = TiktokenTokenizer.CreateForModel(modelName);
    }

    public int CountTokens(string text) => _tokenizer.CountTokens(text, considerPreTokenization: true, considerNormalization: true);

    /// <summary>Returns the (start, length) character offsets of each token, used by the chunker to
    /// find safe split points without re-decoding through the BPE vocab.</summary>
    internal IReadOnlyList<(int Start, int Length)> GetTokenOffsets(string text)
    {
        var tokens = _tokenizer.EncodeToTokens(text, out _, considerPreTokenization: true, considerNormalization: true);
        var offsets = new List<(int, int)>(tokens.Count);
        foreach (var token in tokens)
        {
            offsets.Add((token.Offset.Start.Value, token.Offset.End.Value - token.Offset.Start.Value));
        }
        return offsets;
    }
}
