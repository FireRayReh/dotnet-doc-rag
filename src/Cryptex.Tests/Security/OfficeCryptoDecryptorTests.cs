using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Cryptex.Security;
using OpenMcdf;
using Xunit;

namespace Cryptex.Tests.Security;

/// <summary>
/// Exercises <see cref="OfficeCryptoDecryptor"/>'s Standard-encryption read path (CFB container
/// parsing, EncryptionInfo/EncryptedPackage layout, password verifier check, AES-CBC package
/// decryption) against a synthetic fixture built in-process with the same key-derivation routine
/// the decryptor itself uses (accessed via reflection, since it's a private implementation detail).
///
/// This proves the container/verifier/package decrypt plumbing is correct and that wrong passwords
/// are rejected. It does *not* independently cross-validate the ECMA-376 key-derivation formula
/// against a real Word-produced encrypted file (no Office installation is available in this
/// environment to produce one) - that would need a real fixture checked into the repo. Agile
/// encryption is similarly not covered by a synthetic fixture here for the same reason; both
/// algorithms are implemented per the public MS-OFFCRYPTO spec (see OfficeCryptoDecryptor.cs) and
/// this at least proves internal consistency and that malformed/wrong-password input fails safely
/// rather than silently returning garbage.
/// </summary>
public class OfficeCryptoDecryptorTests
{
    private const string Password = "Cor4ect-Horse-Battery-Staple!";

    [Fact]
    public void Decrypt_StandardEncryption_WithCorrectPassword_RecoversOriginalBytes()
    {
        var originalContent = Encoding.UTF8.GetBytes("PK fake-ooxml-zip-bytes-for-test-purposes-1234567890");
        var container = BuildStandardEncryptedContainer(Password, originalContent);

        var decryptor = new OfficeCryptoDecryptor();
        using var result = decryptor.Decrypt(container, Password);

        Assert.Equal(originalContent, result.ToArray());
    }

    [Fact]
    public void Decrypt_StandardEncryption_WithWrongPassword_ThrowsInvalidDocumentPasswordException()
    {
        var originalContent = Encoding.UTF8.GetBytes("some plaintext content");
        var container = BuildStandardEncryptedContainer(Password, originalContent);

        var decryptor = new OfficeCryptoDecryptor();
        Assert.Throws<InvalidDocumentPasswordException>(() => decryptor.Decrypt(container, "definitely-wrong-password"));
    }

    [Fact]
    public void IsEncryptedOfficeContainer_DetectsCfbMagicNumber()
    {
        var cfbHeader = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0 };
        using var cfbStream = new MemoryStream(cfbHeader);
        Assert.True(OfficeCryptoDecryptor.IsEncryptedOfficeContainer(cfbStream));
        Assert.Equal(0, cfbStream.Position); // must not consume the stream

        var zipHeader = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0 };
        using var zipStream = new MemoryStream(zipHeader);
        Assert.False(OfficeCryptoDecryptor.IsEncryptedOfficeContainer(zipStream));
    }

    // ---------------------------------------------------------------------------------------
    // Fixture builder: assembles a minimal-but-structurally-valid MS-OFFCRYPTO Standard
    // Encryption CFB container (EncryptionInfo + EncryptedPackage streams) so the decrypt path
    // can be tested without a real encrypted Office file.
    // ---------------------------------------------------------------------------------------

    private static MemoryStream BuildStandardEncryptedContainer(string password, byte[] plaintextPackage)
    {
        const int keySizeBytes = 16; // AES-128
        var salt = RandomNumberGenerator.GetBytes(16);

        var key = InvokeDeriveStandardKey(password, salt, keySizeBytes);

        var verifierPlain = RandomNumberGenerator.GetBytes(16);
        var encryptedVerifier = AesEcbEncrypt(key, verifierPlain);

        var verifierHashPlain = SHA1.HashData(verifierPlain); // 20 bytes
        var verifierHashPadded = new byte[32]; // padded to next 16-byte boundary
        verifierHashPlain.CopyTo(verifierHashPadded, 0);
        var encryptedVerifierHash = AesEcbEncrypt(key, verifierHashPadded);

        var encryptionInfo = BuildEncryptionInfo(salt, encryptedVerifier, encryptedVerifierHash, keySizeBytes);
        var encryptedPackage = BuildEncryptedPackage(key, plaintextPackage);

        using var cfbStream = new MemoryStream();
        using (var root = RootStorage.Create(cfbStream, OpenMcdf.Version.V3, StorageModeFlags.LeaveOpen))
        {
            // Each stream must be fully written and disposed before the next is created -
            // OpenMcdf only commits a stream's directory entry once its handle is closed.
            using (var s1 = root.CreateStream("EncryptionInfo"))
            {
                s1.Write(encryptionInfo);
            }
            using (var s2 = root.CreateStream("EncryptedPackage"))
            {
                s2.Write(encryptedPackage);
            }
        }

        return new MemoryStream(cfbStream.ToArray());
    }

    private static byte[] BuildEncryptionInfo(byte[] salt, byte[] encryptedVerifier, byte[] encryptedVerifierHash, int keySizeBytes)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write((ushort)3); // VersionMajor
        bw.Write((ushort)2); // VersionMinor
        bw.Write((uint)0);   // EncryptionInfoFlags

        // Minimal EncryptionHeader: only the fields OfficeCryptoDecryptor actually reads
        // (AlgID at header offset 8, KeySize at header offset 16); real files also carry
        // AlgIDHash, ProviderType, Reserved1/2 and a CSPName, which are irrelevant to this test.
        const int headerSize = 32;
        bw.Write((uint)headerSize);

        var headerStart = ms.Position;
        bw.Write((uint)0);          // Flags
        bw.Write((uint)0);          // SizeExtra
        bw.Write((uint)0x0000660E); // AlgID = AES-128
        bw.Write((uint)0x00008004); // AlgIDHash = SHA-1
        bw.Write((uint)(keySizeBytes * 8)); // KeySize in bits
        bw.Write((uint)0);          // ProviderType
        bw.Write((uint)0);          // Reserved1
        bw.Write((uint)0);          // Reserved2
        // pad remaining header bytes to headerSize
        while (ms.Position - headerStart < headerSize) bw.Write((byte)0);

        // EncryptionVerifier
        bw.Write((uint)salt.Length);
        bw.Write(salt);
        bw.Write(encryptedVerifier);
        bw.Write((uint)20); // VerifierHashSize (SHA-1 = 20 bytes)
        bw.Write(encryptedVerifierHash);

        return ms.ToArray();
    }

    private static byte[] BuildEncryptedPackage(byte[] key, byte[] plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.IV = new byte[16];

        // Standard encryption pads the package to a 16-byte boundary; the true length is stored
        // in the 8-byte header the decryptor uses to truncate back to the original size.
        var padded = new byte[((plaintext.Length + 15) / 16) * 16];
        Array.Copy(plaintext, padded, plaintext.Length);

        using var encryptor = aes.CreateEncryptor();
        var cipherBody = encryptor.TransformFinalBlock(padded, 0, padded.Length);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((ulong)plaintext.Length);
        bw.Write(cipherBody);
        return ms.ToArray();
    }

    private static byte[] AesEcbEncrypt(byte[] key, byte[] block)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(block, 0, block.Length);
    }

    private static byte[] InvokeDeriveStandardKey(string password, byte[] salt, int keySizeBytes)
    {
        var method = typeof(OfficeCryptoDecryptor).GetMethod("DeriveStandardKey", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("DeriveStandardKey method not found; has OfficeCryptoDecryptor been refactored?");
        return (byte[])method.Invoke(null, new object[] { password, salt, keySizeBytes })!;
    }
}
