using System.Security.Cryptography;
using Cryptex.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Cryptex.Security.Envelope;

/// <summary>
/// Decrypts source files that the customer's own C# code encrypted in bulk with
/// <c>System.Security.Cryptography.Aes</c> in CBC mode, writing the IV in front of the ciphertext.
/// Blob layout is <c>IV (IvLength bytes) || ciphertext</c>, optionally wrapped by an
/// encrypt-then-MAC HMAC (see <see cref="EnvelopeHmacOptions"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Security note - unauthenticated CBC is malleable.</b> Without a MAC, AES-CBC provides
/// confidentiality only: an attacker who can modify a blob can flip chosen bits of the recovered
/// plaintext (a one-byte change in ciphertext block N scrambles block N and flips the corresponding
/// byte of block N+1) and this decryptor has no way to notice. Worse, if failure modes are
/// distinguishable, CBC + PKCS#7 forms a classic
/// <see href="https://en.wikipedia.org/wiki/Padding_oracle_attack">padding oracle</see>. Two
/// mitigations are built in, and you should use both:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Enable HMAC</b> (<c>Envelope:Hmac:Enabled</c>) whenever the customer's encryptor emits
///     one. It is verified <i>before</i> any decryption is attempted, so tampered blobs never reach
///     the cipher at all. This is strongly recommended; it is off by default only because not every
///     existing corpus has a MAC to verify against.
///   </description></item>
///   <item><description>
///     <b>Uniform failure.</b> Every failure path - unknown key id, truncated blob, misaligned
///     ciphertext, bad padding, failed MAC, wrong key - throws
///     <see cref="EnvelopeDecryptionException"/> with one identical message and no distinguishing
///     detail. The specific reason is logged at <c>Debug</c> level server-side only and must never
///     be echoed back to a caller.
///   </description></item>
/// </list>
/// <para>
/// Plaintext is never written to disk: decryption happens entirely in memory, and per-operation
/// copies of key material are wiped with <see cref="CryptographicOperations.ZeroMemory"/>.
/// Keys come only from configuration/environment, are never hardcoded, and are never logged - not
/// even truncated, not even their length.
/// </para>
/// </remarks>
public sealed class AesCbcEnvelopeDecryptor : IEnvelopeDecryptor, IDisposable
{
    private const int AesBlockSizeBytes = 16;

    private readonly EnvelopeOptions _options;
    private readonly ILogger<AesCbcEnvelopeDecryptor>? _logger;
    private readonly Dictionary<string, byte[]> _keys;
    private readonly byte[]? _macKey;
    private readonly int _macLength;
    private bool _disposed;

    public AesCbcEnvelopeDecryptor(EnvelopeOptions options, ILogger<AesCbcEnvelopeDecryptor>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;

        if (_options.IvLength is < 1 or > 64)
            throw new ArgumentException($"Envelope:IvLength must be between 1 and 64 bytes; got {_options.IvLength}.", nameof(options));

        _keys = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (keyId, base64) in _options.Keys)
        {
            if (string.IsNullOrWhiteSpace(base64)) continue;
            _keys[keyId] = DecodeAesKey(keyId, base64);
        }

        if (_keys.Count == 0)
            throw new ArgumentException(
                "Envelope decryption is enabled but no keys are configured. Supply at least one via the " +
                $"{EnvelopeOptions.KeyEnvVarPrefix}<keyId> environment variable.", nameof(options));

        if (_options.Hmac.Enabled)
        {
            if (string.IsNullOrWhiteSpace(_options.Hmac.KeyBase64))
                throw new ArgumentException(
                    $"Envelope:Hmac:Enabled is true but no MAC key is configured. Set {EnvelopeOptions.HmacKeyEnvVar}.", nameof(options));

            try
            {
                _macKey = Convert.FromBase64String(_options.Hmac.KeyBase64);
            }
            catch (FormatException ex)
            {
                throw new ArgumentException("Envelope:Hmac:KeyBase64 must be valid base64.", nameof(options), ex);
            }

            if (_macKey.Length == 0)
                throw new ArgumentException("Envelope:Hmac:KeyBase64 must decode to a non-empty key.", nameof(options));

            int fullMacLength;
            using (var probe = CreateHmac(_macKey)) fullMacLength = probe.HashSize / 8;
            _macLength = _options.Hmac.Length > 0 ? _options.Hmac.Length : fullMacLength;

            if (_macLength > fullMacLength)
                throw new ArgumentException(
                    $"Envelope:Hmac:Length ({_options.Hmac.Length}) exceeds the {_options.Hmac.Algorithm} output size ({fullMacLength} bytes).", nameof(options));
        }

        // Deliberately logs counts only - never key ids' material, never lengths of key bytes.
        _logger?.LogInformation(
            "AES-CBC envelope decryptor initialized with {KeyCount} key(s); HMAC verification {HmacState}.",
            _keys.Count, _options.Hmac.Enabled ? "enabled" : "disabled");
    }

    /// <inheritdoc />
    public byte[] Decrypt(ReadOnlySpan<byte> envelope, string? keyId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            return DecryptCore(envelope, keyId);
        }
        catch (EnvelopeDecryptionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Any unanticipated failure collapses into the same opaque error as every other one.
            _logger?.LogDebug(ex, "Envelope decryption failed: unexpected error.");
            throw new EnvelopeDecryptionException(null, ex);
        }
    }

    private byte[] DecryptCore(ReadOnlySpan<byte> envelope, string? keyId)
    {
        var resolvedKeyId = string.IsNullOrWhiteSpace(keyId) ? _options.DefaultKeyId : keyId!;

        if (!_keys.TryGetValue(resolvedKeyId, out var storedKey))
            throw Fail($"no key configured for key id '{resolvedKeyId}'");

        var body = _options.Hmac.Enabled ? VerifyMacAndStripIt(envelope) : envelope;

        if (body.Length <= _options.IvLength)
            throw Fail("blob is shorter than the IV prefix plus at least one ciphertext block");

        var iv = body[.._options.IvLength];
        var ciphertext = body[_options.IvLength..];

        if (ciphertext.Length == 0 || ciphertext.Length % AesBlockSizeBytes != 0)
            throw Fail("ciphertext length is not a positive multiple of the AES block size");

        // Work on a copy of the key so it can be wiped immediately after use; the long-lived copy
        // is wiped in Dispose.
        var key = new byte[storedKey.Length];
        try
        {
            storedKey.CopyTo(key, 0);

            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = _options.PaddingMode == EnvelopePaddingMode.Pkcs7
                ? System.Security.Cryptography.PaddingMode.PKCS7
                : System.Security.Cryptography.PaddingMode.None;
            aes.Key = key;

            try
            {
                return aes.DecryptCbc(ciphertext, iv, aes.Padding);
            }
            catch (CryptographicException ex)
            {
                // Bad padding and "wrong key" are indistinguishable to the caller on purpose.
                _logger?.LogDebug(ex, "Envelope decryption failed: cipher rejected the ciphertext (bad padding or wrong key).");
                throw new EnvelopeDecryptionException(null, ex);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Verifies the encrypt-then-MAC tag over everything that is not the tag, in constant time, and
    /// returns the remaining <c>IV || ciphertext</c> body.
    /// </summary>
    private ReadOnlySpan<byte> VerifyMacAndStripIt(ReadOnlySpan<byte> envelope)
    {
        if (_macKey is null) throw Fail("MAC verification requested without a MAC key");

        if (envelope.Length <= _macLength)
            throw Fail("blob is too short to contain a MAC");

        ReadOnlySpan<byte> presentedMac, body;
        if (_options.Hmac.Placement == MacPlacement.Prepend)
        {
            presentedMac = envelope[.._macLength];
            body = envelope[_macLength..];
        }
        else
        {
            presentedMac = envelope[^_macLength..];
            body = envelope[..^_macLength];
        }

        var macKey = new byte[_macKey.Length];
        Span<byte> computed = stackalloc byte[64];
        try
        {
            _macKey.CopyTo(macKey, 0);
            using var hmac = CreateHmac(macKey);

            var fullLength = hmac.HashSize / 8;
            if (!hmac.TryComputeHash(body, computed, out var written) || written != fullLength)
                throw Fail("MAC computation produced an unexpected length");

            if (!CryptographicOperations.FixedTimeEquals(computed[.._macLength], presentedMac))
                throw Fail("MAC verification failed");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(macKey);
            CryptographicOperations.ZeroMemory(computed);
        }

        return body;
    }

    private HMAC CreateHmac(byte[] key) => _options.Hmac.Algorithm?.Trim().ToUpperInvariant() switch
    {
        null or "" or "HMACSHA256" or "SHA256" => new HMACSHA256(key),
        "HMACSHA384" or "SHA384" => new HMACSHA384(key),
        "HMACSHA512" or "SHA512" => new HMACSHA512(key),
        "HMACSHA1" or "SHA1" => new HMACSHA1(key),
        var other => throw new ArgumentException($"Unsupported Envelope:Hmac:Algorithm '{other}'. Use HMACSHA256, HMACSHA384, HMACSHA512 or HMACSHA1.")
    };

    /// <summary>
    /// Logs the real reason at Debug level (server-side only) and produces the single opaque
    /// exception every failure path shares.
    /// </summary>
    private EnvelopeDecryptionException Fail(string internalReason)
    {
        _logger?.LogDebug("Envelope decryption failed: {InternalReason}.", internalReason);
        return new EnvelopeDecryptionException();
    }

    private static byte[] DecodeAesKey(string keyId, string base64)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException($"Envelope key '{keyId}' is not valid base64.", ex);
        }

        if (key.Length is not (16 or 24 or 32))
        {
            CryptographicOperations.ZeroMemory(key);
            throw new ArgumentException($"Envelope key '{keyId}' must decode to 16, 24 or 32 bytes (AES-128/192/256).");
        }

        return key;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var key in _keys.Values) CryptographicOperations.ZeroMemory(key);
        _keys.Clear();
        if (_macKey is not null) CryptographicOperations.ZeroMemory(_macKey);
    }
}
