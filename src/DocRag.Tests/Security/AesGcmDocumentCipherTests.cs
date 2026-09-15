using System.Text;
using DocRag.Core.Interfaces;
using DocRag.Security;
using Xunit;

namespace DocRag.Tests.Security;

public class AesGcmDocumentCipherTests
{
    private static AesGcmDocumentCipher CreateCipher() => new(AesGcmDocumentCipher.GenerateKey());

    // EncryptText/DecryptText are default interface members on IDocumentCipher, so they must be
    // invoked through the interface (not the concrete class) to resolve.
    private static IDocumentCipher AsCipher(AesGcmDocumentCipher c) => c;

    [Fact]
    public void EncryptDecrypt_RoundTripsBytes()
    {
        var cipher = CreateCipher();
        var plaintext = Encoding.UTF8.GetBytes("Confidential company financial results for Q3.");

        var ciphertext = cipher.Encrypt(plaintext);
        var decrypted = cipher.Decrypt(ciphertext);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void EncryptDecrypt_RoundTripsText()
    {
        var cipher = AsCipher(CreateCipher());
        const string text = "Employee handbook: password rotation policy.";

        var ciphertext = cipher.EncryptText(text);
        var decrypted = cipher.DecryptText(ciphertext);

        Assert.Equal(text, decrypted);
    }

    [Fact]
    public void Encrypt_ProducesDifferentCiphertextEachTime()
    {
        var cipher = CreateCipher();
        var plaintext = Encoding.UTF8.GetBytes("same input");

        var c1 = cipher.Encrypt(plaintext);
        var c2 = cipher.Encrypt(plaintext);

        Assert.NotEqual(c1, c2); // random nonce each call
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_ThrowsCryptographicException()
    {
        var cipher = CreateCipher();
        var ciphertext = cipher.Encrypt(Encoding.UTF8.GetBytes("integrity check"));
        ciphertext[^1] ^= 0xFF; // flip a bit in the GCM tag

        Assert.Throws<System.Security.Cryptography.AuthenticationTagMismatchException>(() => cipher.Decrypt(ciphertext));
    }

    [Fact]
    public void Constructor_RejectsInvalidKeyLength()
    {
        var shortKey = Convert.ToBase64String(new byte[16]); // 128-bit, not 256-bit
        Assert.Throws<ArgumentException>(() => new AesGcmDocumentCipher(shortKey));
    }

    [Fact]
    public void Constructor_RejectsEmptyKey()
    {
        Assert.Throws<ArgumentException>(() => new AesGcmDocumentCipher(""));
    }

    [Fact]
    public void GenerateKey_ProducesValidBase64Of256Bits()
    {
        var key = AesGcmDocumentCipher.GenerateKey();
        var bytes = Convert.FromBase64String(key);
        Assert.Equal(32, bytes.Length);
    }
}
