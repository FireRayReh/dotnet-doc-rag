namespace DocRag.Core.Interfaces;

/// <summary>
/// At-rest encryption for stored document bytes / chunk text. Distinct from
/// <c>OfficeCryptoDecryptor</c>, which decrypts *source* files that arrive password-protected.
/// </summary>
public interface IDocumentCipher
{
    /// <summary>Encrypts plaintext bytes, returning ciphertext (nonce/tag embedded).</summary>
    byte[] Encrypt(ReadOnlySpan<byte> plaintext);

    /// <summary>Decrypts ciphertext previously produced by <see cref="Encrypt"/>.</summary>
    byte[] Decrypt(ReadOnlySpan<byte> ciphertext);

    /// <summary>Convenience overload for encrypting UTF-8 text.</summary>
    byte[] EncryptText(string plaintext) => Encrypt(System.Text.Encoding.UTF8.GetBytes(plaintext));

    /// <summary>Convenience overload for decrypting to UTF-8 text.</summary>
    string DecryptText(ReadOnlySpan<byte> ciphertext) => System.Text.Encoding.UTF8.GetString(Decrypt(ciphertext));
}
