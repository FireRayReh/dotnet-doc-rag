using System.Security.Cryptography;
using Cryptex.Security.Envelope;

namespace Cryptex.Tests.TestSupport;

/// <summary>
/// Mimics the customer's own C# encryptor: read the plaintext file bytes, run
/// <see cref="Aes"/> over them in CBC mode with a random IV, and emit
/// <c>IV || ciphertext</c> as one opaque blob - optionally wrapped in an encrypt-then-MAC HMAC.
/// <para>
/// Deliberately written with nothing but plain <c>System.Security.Cryptography</c>, so the tests
/// exercise <see cref="AesCbcEnvelopeDecryptor"/> against a genuinely independent producer rather
/// than against the decryptor's own inverse.
/// </para>
/// </summary>
internal static class CustomerEnvelopeEncryptor
{
    /// <summary>Generates a random AES key of <paramref name="bits"/> (128/192/256) as base64.</summary>
    public static string GenerateKeyBase64(int bits = 256) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bits / 8));

    /// <summary>Produces <c>IV || ciphertext</c>, PKCS#7 padded, exactly as the customer's code would.</summary>
    public static byte[] Encrypt(byte[] plaintext, string keyBase64, PaddingMode padding = PaddingMode.PKCS7)
    {
        using var aes = Aes.Create();
        aes.Key = Convert.FromBase64String(keyBase64);
        aes.Mode = CipherMode.CBC;
        aes.Padding = padding;
        aes.GenerateIV();

        var ciphertext = aes.EncryptCbc(plaintext, aes.IV, padding);

        var blob = new byte[aes.IV.Length + ciphertext.Length];
        aes.IV.CopyTo(blob, 0);
        ciphertext.CopyTo(blob, aes.IV.Length);
        return blob;
    }

    /// <summary>Wraps an <c>IV || ciphertext</c> blob with an encrypt-then-MAC HMAC tag.</summary>
    public static byte[] WithHmac(byte[] ivAndCiphertext, string macKeyBase64, MacPlacement placement = MacPlacement.Append, int macLength = 0)
    {
        using var hmac = new HMACSHA256(Convert.FromBase64String(macKeyBase64));
        var mac = hmac.ComputeHash(ivAndCiphertext);
        if (macLength > 0) mac = mac[..macLength];

        var result = new byte[mac.Length + ivAndCiphertext.Length];
        if (placement == MacPlacement.Prepend)
        {
            mac.CopyTo(result, 0);
            ivAndCiphertext.CopyTo(result, mac.Length);
        }
        else
        {
            ivAndCiphertext.CopyTo(result, 0);
            mac.CopyTo(result, ivAndCiphertext.Length);
        }

        return result;
    }

    /// <summary>Flips one bit in the byte at <paramref name="index"/> (negative indexes from the end).</summary>
    public static byte[] FlipBit(byte[] blob, int index)
    {
        var copy = (byte[])blob.Clone();
        var i = index < 0 ? copy.Length + index : index;
        copy[i] ^= 0x40;
        return copy;
    }
}
