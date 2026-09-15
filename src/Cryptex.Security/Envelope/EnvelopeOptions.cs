namespace Cryptex.Security.Envelope;

/// <summary>Where the MAC sits relative to the rest of the blob.</summary>
public enum MacPlacement
{
    /// <summary>Blob layout is <c>IV || ciphertext || MAC</c> - the MAC is the last N bytes.</summary>
    Append = 0,

    /// <summary>Blob layout is <c>MAC || IV || ciphertext</c> - the MAC comes before the IV.</summary>
    Prepend = 1
}

/// <summary>Block padding used by the customer's encryptor.</summary>
public enum EnvelopePaddingMode
{
    /// <summary>Standard PKCS#7 padding - what <c>System.Security.Cryptography.Aes</c> emits by default.</summary>
    Pkcs7 = 0,

    /// <summary>No padding removal at all; for hand-rolled encryptors that zero-pad the final block.</summary>
    None = 1
}

/// <summary>
/// Optional encrypt-then-MAC settings. Off by default: not every customer encryptor emits a MAC.
/// Turning this on is strongly recommended whenever the encryptor does produce one - see the
/// remarks on <see cref="AesCbcEnvelopeDecryptor"/>.
/// </summary>
public sealed class EnvelopeHmacOptions
{
    public bool Enabled { get; set; }

    /// <summary>One of <c>HMACSHA256</c> (default), <c>HMACSHA384</c>, <c>HMACSHA512</c>, <c>HMACSHA1</c>.</summary>
    public string Algorithm { get; set; } = "HMACSHA256";

    /// <summary>
    /// Base64 MAC key, separate from the encryption key. Supply via the
    /// <c>CRYPTEX_ENVELOPE_HMAC_KEY</c> environment variable in production; never commit it.
    /// </summary>
    public string KeyBase64 { get; set; } = string.Empty;

    /// <summary>Whether the MAC is appended after the ciphertext or prepended before the IV.</summary>
    public MacPlacement Placement { get; set; } = MacPlacement.Append;

    /// <summary>
    /// MAC length in bytes. 0 (default) means "the algorithm's full output size". A smaller value
    /// supports encryptors that store a truncated MAC.
    /// </summary>
    public int Length { get; set; }
}

/// <summary>
/// Configuration for the customer-encrypted-envelope ingestion path (<c>Envelope</c> section of
/// appsettings.json, overridable by environment variables).
/// </summary>
/// <remarks>
/// Encryption keys must never be committed to appsettings.json. Populate
/// <see cref="Keys"/> at startup from <c>CRYPTEX_ENVELOPE_KEY_&lt;keyId&gt;</c> environment
/// variables via <see cref="LoadKeysFromEnvironment"/>.
/// </remarks>
public sealed class EnvelopeOptions
{
    public const string SectionName = "Envelope";

    /// <summary>Environment variable prefix for per-key-id encryption keys.</summary>
    public const string KeyEnvVarPrefix = "CRYPTEX_ENVELOPE_KEY_";

    /// <summary>Environment variable holding the base64 HMAC key.</summary>
    public const string HmacKeyEnvVar = "CRYPTEX_ENVELOPE_HMAC_KEY";

    /// <summary>Master switch. When false, no envelope decryption is attempted at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>Length of the IV prefix, in bytes. 16 for AES-CBC.</summary>
    public int IvLength { get; set; } = 16;

    /// <summary>Padding the customer's encryptor used.</summary>
    public EnvelopePaddingMode PaddingMode { get; set; } = EnvelopePaddingMode.Pkcs7;

    /// <summary>File extensions (with leading dot) that mark a file as an encrypted envelope.</summary>
    public List<string> EncryptedExtensions { get; set; } = new() { ".enc", ".aes" };

    /// <summary>Key id used when the request/blob/filename carries none.</summary>
    public string DefaultKeyId { get; set; } = "default";

    /// <summary>keyId -&gt; base64-encoded AES key (128/192/256-bit). Supply via env vars in production.</summary>
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public EnvelopeHmacOptions Hmac { get; set; } = new();

    /// <summary>
    /// Returns true when <paramref name="fileName"/> carries one of the configured encrypted
    /// extensions (case-insensitive).
    /// </summary>
    public bool IsEncryptedFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        var ext = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(ext)
            && EncryptedExtensions.Any(e => string.Equals(Normalize(e), ext, StringComparison.OrdinalIgnoreCase));

        static string Normalize(string e) => e.StartsWith('.') ? e : "." + e;
    }

    /// <summary>
    /// Merges keys supplied through the environment into <see cref="Keys"/>, so real key material
    /// never has to sit in appsettings.json. Reads every <c>CRYPTEX_ENVELOPE_KEY_&lt;keyId&gt;</c>
    /// variable (the suffix is the key id) plus <c>CRYPTEX_ENVELOPE_HMAC_KEY</c>. Environment
    /// values win over anything present in configuration.
    /// </summary>
    public void LoadKeysFromEnvironment(IEnumerable<KeyValuePair<string, string?>>? environment = null)
    {
        var vars = environment ?? Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Select(e => new KeyValuePair<string, string?>((string)e.Key, (string?)e.Value));

        foreach (var (name, value) in vars)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;

            if (string.Equals(name, HmacKeyEnvVar, StringComparison.Ordinal))
            {
                Hmac.KeyBase64 = value;
                continue;
            }

            if (!name.StartsWith(KeyEnvVarPrefix, StringComparison.Ordinal)) continue;

            var keyId = name[KeyEnvVarPrefix.Length..];
            if (keyId.Length == 0) continue;
            Keys[keyId] = value;
        }
    }
}
