using System.Security.Cryptography;
using System.Text;
using Cryptex.Core.Interfaces;
using Cryptex.Security.Envelope;
using Cryptex.Tests.TestSupport;
using Xunit;

namespace Cryptex.Tests.Security;

/// <summary>
/// Exercises <see cref="AesCbcEnvelopeDecryptor"/> against blobs produced by
/// <see cref="CustomerEnvelopeEncryptor"/>, which uses nothing but plain
/// <c>System.Security.Cryptography.Aes</c> in CBC mode with the IV prepended - i.e. exactly what
/// the customer's own C# encryptor emits.
/// </summary>
public class AesCbcEnvelopeDecryptorTests
{
    private const string KeyId = "primary";

    private static EnvelopeOptions Options(string keyBase64, string keyId = KeyId) => new()
    {
        Enabled = true,
        DefaultKeyId = keyId,
        Keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [keyId] = keyBase64 }
    };

    [Theory]
    [InlineData(128)]
    [InlineData(192)]
    [InlineData(256)]
    public void Decrypt_RoundTrips_ForAllSupportedKeySizes(int bits)
    {
        var key = CustomerEnvelopeEncryptor.GenerateKeyBase64(bits);
        var plaintext = Encoding.UTF8.GetBytes($"Quarterly report body encrypted under an AES-{bits} key.");
        var blob = CustomerEnvelopeEncryptor.Encrypt(plaintext, key);

        using var decryptor = new AesCbcEnvelopeDecryptor(Options(key));

        Assert.Equal(plaintext, decryptor.Decrypt(blob));
    }

    [Fact]
    public void Decrypt_RoundTrips_LargeBinaryPayload()
    {
        var key = CustomerEnvelopeEncryptor.GenerateKeyBase64();
        var plaintext = RandomNumberGenerator.GetBytes(64 * 1024 + 7); // deliberately not block-aligned
        var blob = CustomerEnvelopeEncryptor.Encrypt(plaintext, key);

        using var decryptor = new AesCbcEnvelopeDecryptor(Options(key));

        Assert.Equal(plaintext, decryptor.Decrypt(blob));
    }

    [Fact]
    public void Decrypt_WithConfiguredIvLength_ReadsIvFromTheLeadingBytes()
    {
        var key = CustomerEnvelopeEncryptor.GenerateKeyBase64();
        var plaintext = Encoding.UTF8.GetBytes("IV is the first 16 bytes of the blob.");
        var blob = CustomerEnvelopeEncryptor.Encrypt(plaintext, key);

        var options = Options(key);
        options.IvLength = 16;
        using var decryptor = new AesCbcEnvelopeDecryptor(options);

        Assert.Equal(plaintext, decryptor.Decrypt(blob));

        // Corrupting the IV region must not corrupt anything past the first block.
        var tweakedIv = CustomerEnvelopeEncryptor.FlipBit(blob, 3);
        var recovered = decryptor.Decrypt(tweakedIv);
        Assert.Equal(plaintext.Length, recovered.Length);
        Assert.NotEqual(plaintext, recovered);
        Assert.Equal(plaintext[16..], recovered[16..]);
    }

    [Fact]
    public void Decrypt_WithNoPadding_ReturnsRawBlocks()
    {
        var key = CustomerEnvelopeEncryptor.GenerateKeyBase64();
        var plaintext = Encoding.UTF8.GetBytes("exactly-thirty-two-bytes-long!!!"); // 32 bytes, block-aligned
        Assert.Equal(32, plaintext.Length);

        var blob = CustomerEnvelopeEncryptor.Encrypt(plaintext, key, PaddingMode.None);

        var options = Options(key);
        options.PaddingMode = EnvelopePaddingMode.None;
        using var decryptor = new AesCbcEnvelopeDecryptor(options);

        Assert.Equal(plaintext, decryptor.Decrypt(blob));
    }

    // -------------------------------------------------------------------------------------------
    // Key rotation
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Decrypt_WithTwoKeyIds_SelectsTheRequestedKey()
    {
        var key2023 = CustomerEnvelopeEncryptor.GenerateKeyBase64();
        var key2024 = CustomerEnvelopeEncryptor.GenerateKeyBase64();

        var old = Encoding.UTF8.GetBytes("encrypted under the 2023 key");
        var recent = Encoding.UTF8.GetBytes("encrypted under the 2024 key");

        var oldBlob = CustomerEnvelopeEncryptor.Encrypt(old, key2023);
        var recentBlob = CustomerEnvelopeEncryptor.Encrypt(recent, key2024);

        using var decryptor = new AesCbcEnvelopeDecryptor(new EnvelopeOptions
        {
            Enabled = true,
            DefaultKeyId = "2024",
            Keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["2023"] = key2023,
                ["2024"] = key2024
            }
        });

        Assert.Equal(old, decryptor.Decrypt(oldBlob, "2023"));
        Assert.Equal(recent, decryptor.Decrypt(recentBlob, "2024"));

        // No key id supplied -> DefaultKeyId ("2024").
        Assert.Equal(recent, decryptor.Decrypt(recentBlob));

        // The wrong key id fails, indistinguishably from every other failure.
        AssertUniformFailure(() => decryptor.Decrypt(oldBlob, "2024"));
    }

    [Fact]
    public void Decrypt_WithUnknownKeyId_FailsUniformly()
    {
        var key = CustomerEnvelopeEncryptor.GenerateKeyBase64();
        var blob = CustomerEnvelopeEncryptor.Encrypt(Encoding.UTF8.GetBytes("hello"), key);

        using var decryptor = new AesCbcEnvelopeDecryptor(Options(key));

        AssertUniformFailure(() => decryptor.Decrypt(blob, "no-such-key"));
    }

    // -------------------------------------------------------------------------------------------
    // Padding-oracle regression: every failure mode must be indistinguishable.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The core security property of this decryptor: a wrong key, a tampered ciphertext and a
    /// truncated blob must all produce the <b>identical</b> exception type and message. If this test
    /// ever fails because someone made one path more informative, that change has turned the ingest
    /// endpoint into a padding oracle - an attacker who can submit chosen blobs and observe which
    /// error comes back can decrypt real documents byte by byte. Fix the code, not the test.
    /// </summary>
    [Fact]
    public void Decrypt_WrongKey_TamperedCiphertext_AndTruncatedBlob_AllFailIdentically()
    {
        var realKey = CustomerEnvelopeEncryptor.GenerateKeyBase64();
        var otherKey = CustomerEnvelopeEncryptor.GenerateKeyBase64();
        var plaintext = Encoding.UTF8.GetBytes("Board minutes: confidential. Padding oracle bait bait bait.");
        var blob = CustomerEnvelopeEncryptor.Encrypt(plaintext, realKey);

        using var wrongKeyDecryptor = new AesCbcEnvelopeDecryptor(Options(otherKey));
        using var rightKeyDecryptor = new AesCbcEnvelopeDecryptor(Options(realKey));

        var failures = new List<Exception>
        {
            // 1. Wrong key (PKCS#7 unpadding will almost certainly reject the garbage plaintext).
            Record.Exception(() => wrongKeyDecryptor.Decrypt(blob))!,

            // 2. Tampered ciphertext - flip a bit in the final block so the padding is destroyed.
            Record.Exception(() => rightKeyDecryptor.Decrypt(CustomerEnvelopeEncryptor.FlipBit(blob, -3)))!,

            // 3. Truncated blob - cut it mid-block so it is no longer a whole number of AES blocks.
            Record.Exception(() => rightKeyDecryptor.Decrypt(blob[..^5]))!,

            // 4. Truncated to shorter than the IV prefix.
            Record.Exception(() => rightKeyDecryptor.Decrypt(blob[..8]))!,

            // 5. Empty blob.
            Record.Exception(() => rightKeyDecryptor.Decrypt(Array.Empty<byte>()))!
        };

        Assert.All(failures, ex => Assert.IsType<EnvelopeDecryptionException>(ex));

        var distinctTypes = failures.Select(e => e.GetType()).Distinct().ToList();
        var distinctMessages = failures.Select(e => e.Message).Distinct().ToList();

        Assert.Single(distinctTypes);
        Assert.Single(distinctMessages);
        Assert.Equal(EnvelopeDecryptionException.UniformMessage, distinctMessages[0]);
    }

    // -------------------------------------------------------------------------------------------
    // HMAC (encrypt-then-MAC)
    // -------------------------------------------------------------------------------------------

    private static EnvelopeOptions HmacOptions(string keyBase64, string macKeyBase64, MacPlacement placement = MacPlacement.Append, int macLength = 0)
    {
        var options = Options(keyBase64);
        options.Hmac = new EnvelopeHmacOptions
        {
            Enabled = true,
            Algorithm = "HMACSHA256",
            KeyBase64 = macKeyBase64,
            Placement = placement,
            Length = macLength
        };
        return options;
    }

    [Theory]
    [InlineData(MacPlacement.Append)]
    [InlineData(MacPlacement.Prepend)]
    public void Decrypt_WithHmacEnabled_AcceptsAValidMac(MacPlacement placement)
    {
        var key = CustomerEnvelopeEncryptor.GenerateKeyBase64();
        var macKey = CustomerEnvelopeEncryptor.GenerateKeyBase64();
        var plaintext = Encoding.UTF8.GetBytes("authenticated envelope contents");

        var blob = CustomerEnvelopeEncryptor.WithHmac(
            CustomerEnvelopeEncryptor.Encrypt(plaintext, key), macKey, placement);

        using var decryptor = new AesCbcEnvelopeDecryptor(HmacOptions(key, macKey, placement));

        Assert.Equal(plaintext, decryptor.Decrypt(blob));
    }

    [Fact]
    public void Decrypt_WithHmacEnabled_AcceptsATruncatedMacWhenLengthIsConfigured()
    {
        var key = CustomerEnvelopeEncryptor.GenerateKeyBase64();
        var macKey = CustomerEnvelopeEncryptor.GenerateKeyBase64();
        var plaintext = Encoding.UTF8.GetBytes("16-byte truncated MAC");

        var blob = CustomerEnvelopeEncryptor.WithHmac(
            CustomerEnvelopeEncryptor.Encrypt(plaintext, key), macKey, MacPlacement.Append, macLength: 16);

        using var decryptor = new AesCbcEnvelopeDecryptor(HmacOptions(key, macKey, MacPlacement.Append, macLength: 16));

        Assert.Equal(plaintext, decryptor.Decrypt(blob));
    }

    [Fact]
    public void Decrypt_WithHmacEnabled_TamperedMacOrBodyFailsWithTheSameUniformError()
    {
        var key = CustomerEnvelopeEncryptor.GenerateKeyBase64();
        var macKey = CustomerEnvelopeEncryptor.GenerateKeyBase64();
        var wrongMacKey = CustomerEnvelopeEncryptor.GenerateKeyBase64();
        var plaintext = Encoding.UTF8.GetBytes("authenticated envelope contents");

        var blob = CustomerEnvelopeEncryptor.WithHmac(
            CustomerEnvelopeEncryptor.Encrypt(plaintext, key), macKey);

        using var decryptor = new AesCbcEnvelopeDecryptor(HmacOptions(key, macKey));

        var failures = new List<Exception>
        {
            // Tampered MAC tag (last byte of the blob).
            Record.Exception(() => decryptor.Decrypt(CustomerEnvelopeEncryptor.FlipBit(blob, -1)))!,
            // Tampered ciphertext under an otherwise-valid-looking blob.
            Record.Exception(() => decryptor.Decrypt(CustomerEnvelopeEncryptor.FlipBit(blob, 40)))!,
            // MAC computed under a different key entirely.
            Record.Exception(() => decryptor.Decrypt(
                CustomerEnvelopeEncryptor.WithHmac(CustomerEnvelopeEncryptor.Encrypt(plaintext, key), wrongMacKey)))!,
            // Blob too short to even contain a MAC.
            Record.Exception(() => decryptor.Decrypt(blob[..4]))!
        };

        Assert.All(failures, ex => Assert.IsType<EnvelopeDecryptionException>(ex));
        Assert.Single(failures.Select(e => e.Message).Distinct());
        Assert.Equal(EnvelopeDecryptionException.UniformMessage, failures[0].Message);
    }

    [Fact]
    public void Decrypt_WithHmacEnabled_RejectsTamperingBeforeTheCipherRuns()
    {
        // A bit flip in the *middle* of the ciphertext leaves the padding intact, so an
        // unauthenticated CBC decryptor happily returns malleable garbage. With HMAC on, it must be
        // rejected instead - which is precisely why enabling HMAC is strongly recommended.
        var key = CustomerEnvelopeEncryptor.GenerateKeyBase64();
        var macKey = CustomerEnvelopeEncryptor.GenerateKeyBase64();
        var plaintext = Encoding.UTF8.GetBytes(new string('A', 200));
        var inner = CustomerEnvelopeEncryptor.Encrypt(plaintext, key);

        var tamperedInner = CustomerEnvelopeEncryptor.FlipBit(inner, 32);

        using var unauthenticated = new AesCbcEnvelopeDecryptor(Options(key));
        var malleated = unauthenticated.Decrypt(tamperedInner);
        Assert.NotEqual(plaintext, malleated); // silently wrong: exactly the malleability risk

        // Same tampering, but under a MAC computed over the *original* body: rejected outright,
        // before the cipher ever sees the bytes.
        using var authenticated = new AesCbcEnvelopeDecryptor(HmacOptions(key, macKey));
        var forged = CustomerEnvelopeEncryptor.WithHmac(inner, macKey);
        forged[32] ^= 0x40;

        AssertUniformFailure(() => authenticated.Decrypt(forged));
        Assert.Equal(plaintext, authenticated.Decrypt(CustomerEnvelopeEncryptor.WithHmac(inner, macKey)));
    }

    // -------------------------------------------------------------------------------------------
    // Configuration validation
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Constructor_WithNoKeys_Throws()
    {
        var options = new EnvelopeOptions { Enabled = true };
        Assert.Throws<ArgumentException>(() => new AesCbcEnvelopeDecryptor(options));
    }

    [Fact]
    public void Constructor_WithWrongSizedKey_Throws()
    {
        var options = Options(Convert.ToBase64String(RandomNumberGenerator.GetBytes(20)));
        Assert.Throws<ArgumentException>(() => new AesCbcEnvelopeDecryptor(options));
    }

    [Fact]
    public void Constructor_WithHmacEnabledButNoMacKey_Throws()
    {
        var options = Options(CustomerEnvelopeEncryptor.GenerateKeyBase64());
        options.Hmac = new EnvelopeHmacOptions { Enabled = true, KeyBase64 = "" };
        Assert.Throws<ArgumentException>(() => new AesCbcEnvelopeDecryptor(options));
    }

    [Fact]
    public void LoadKeysFromEnvironment_PopulatesKeysAndMacKeyWithoutTouchingAppSettings()
    {
        var options = new EnvelopeOptions();
        options.LoadKeysFromEnvironment(new Dictionary<string, string?>
        {
            [EnvelopeOptions.KeyEnvVarPrefix + "2024"] = "AAAA",
            [EnvelopeOptions.KeyEnvVarPrefix + "2023"] = "BBBB",
            [EnvelopeOptions.HmacKeyEnvVar] = "CCCC",
            ["UNRELATED_VARIABLE"] = "ignored"
        });

        Assert.Equal("AAAA", options.Keys["2024"]);
        Assert.Equal("BBBB", options.Keys["2023"]);
        Assert.Equal("CCCC", options.Hmac.KeyBase64);
        Assert.Equal(2, options.Keys.Count);
    }

    private static void AssertUniformFailure(Action act)
    {
        var ex = Assert.Throws<EnvelopeDecryptionException>(act);
        Assert.Equal(EnvelopeDecryptionException.UniformMessage, ex.Message);
    }
}
