namespace Cryptex.Core.Chunking;

/// <summary>
/// Counts tokens for text using a tiktoken-compatible tokenizer. Used both for chunk sizing and
/// for reporting token counts back to callers.
/// </summary>
public interface ITokenCounter
{
    int CountTokens(string text);
}
