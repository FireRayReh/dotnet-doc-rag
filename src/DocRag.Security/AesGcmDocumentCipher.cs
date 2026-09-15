using System.Security.Cryptography;
using DocRag.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace DocRag.Security;

/// <summary>
/// At-rest AES-256-GCM cipher for stored chunk text / original file bytes.
/// Ciphertext layout: [12-byte nonce][ciphertext][16-byte tag].
/// The key is read once from configuration/environment and never logged.
/// </summary>
public sealed class AesGcmDocumentCipher : IDocumentCipher
{
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;

    private readonly byte[] _key;

    public AesGcmDocumentCipher(string base64Key, ILogger<AesGcmDocumentCipher>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
            throw new ArgumentException("A base64-encoded 256-bit encryption key is required. Set it via the DOC_ENCRYPTION_KEY environment variable.", nameof(base64Key));

        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64Key);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("Encryption key must be valid base64.", nameof(base64Key), ex);
        }

        if (key.Length != 32)
            throw new ArgumentException($"Encryption key must decode to 32 bytes (256 bits); got {key.Length}.", nameof(base64Key));

        _key = key;
        // Deliberately never log the key material itself.
        logger?.LogInformation("AES-256-GCM document cipher initialized.");
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];

        using var gcm = new AesGcm(_key, TagSizeBytes);
        gcm.Encrypt(nonce, plaintext, ciphertext, tag);

        var result = new byte[NonceSizeBytes + ciphertext.Length + TagSizeBytes];
        nonce.CopyTo(result, 0);
        ciphertext.CopyTo(result, NonceSizeBytes);
        tag.CopyTo(result, NonceSizeBytes + ciphertext.Length);
        return result;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext)
    {
        if (ciphertext.Length < NonceSizeBytes + TagSizeBytes)
            throw new ArgumentException("Ciphertext too short to contain nonce and tag.", nameof(ciphertext));

        var nonce = ciphertext[..NonceSizeBytes];
        var tag = ciphertext[^TagSizeBytes..];
        var cipherBody = ciphertext[NonceSizeBytes..^TagSizeBytes];

        var plaintext = new byte[cipherBody.Length];
        using var gcm = new AesGcm(_key, TagSizeBytes);
        gcm.Decrypt(nonce, cipherBody, tag, plaintext);
        return plaintext;
    }

    /// <summary>Generates a fresh base64-encoded 256-bit key, useful for provisioning DOC_ENCRYPTION_KEY.</summary>
    public static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
