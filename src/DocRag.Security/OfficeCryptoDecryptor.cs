using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using OpenMcdf;

namespace DocRag.Security;

/// <summary>
/// Decrypts password-protected OOXML (docx/pptx/xlsx) files that use the MS-OFFCRYPTO container
/// format: a CFB (Compound File Binary, OLE2) file holding an "EncryptionInfo" stream describing
/// the scheme, and an "EncryptedPackage" stream holding the encrypted OOXML zip payload.
///
/// Implements:
///  - Standard Encryption (ECMA-376 / MS-OFFCRYPTO 2.3.4.5-2.3.4.9), used by "Encrypt with Password"
///    in older/CryptoAPI-compatible modes: AES-CBC with a null IV over the whole package, key
///    derived via 50,000 rounds of SHA-1.
///  - Agile Encryption (MS-OFFCRYPTO 2.3.4.10+), the default for modern Office (2010+): key derived
///    via a configurable hash algorithm and spin count, package encrypted per-4096-byte segment
///    with AES-CBC and a segment-specific IV.
///
/// Both schemes are implemented per the publicly documented [MS-OFFCRYPTO] specification. If a
/// future/unsupported scheme is encountered, this throws <see cref="UnsupportedOfficeEncryptionException"/>
/// rather than silently returning garbage bytes.
/// </summary>
public sealed class OfficeCryptoDecryptor
{
    private static readonly byte[] AgileVerifierInputBlockKey = { 0xfe, 0xa7, 0xd2, 0x76, 0x3b, 0x4b, 0x9e, 0x79 };
    private static readonly byte[] AgileVerifierValueBlockKey = { 0xd7, 0xaa, 0x0f, 0x6d, 0x30, 0x61, 0x34, 0x4e };
    private static readonly byte[] AgileKeyValueBlockKey = { 0x14, 0x6e, 0x0b, 0xe7, 0xab, 0xac, 0xd0, 0xd6 };

    private readonly ILogger<OfficeCryptoDecryptor>? _logger;

    public OfficeCryptoDecryptor(ILogger<OfficeCryptoDecryptor>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns true if the given stream looks like an OLE2/CFB compound file (the container used by
    /// both Standard and Agile encrypted OOXML documents), as opposed to a plain OOXML zip.
    /// </summary>
    public static bool IsEncryptedOfficeContainer(Stream stream)
    {
        if (!stream.CanSeek) return false;
        var pos = stream.Position;
        try
        {
            Span<byte> header = stackalloc byte[8];
            if (stream.Read(header) < 8) return false;
            // OLE2 CFB magic number: D0 CF 11 E0 A1 B1 1A E1
            ReadOnlySpan<byte> magic = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
            return header.SequenceEqual(magic);
        }
        finally
        {
            stream.Position = pos;
        }
    }

    /// <summary>
    /// Decrypts an encrypted OOXML container into a plain OOXML (zip) memory stream, ready to be
    /// handed to an OpenXML parser.
    /// </summary>
    public MemoryStream Decrypt(Stream encryptedContainer, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        using var root = RootStorage.Open(encryptedContainer, StorageModeFlags.LeaveOpen);

        if (!root.TryOpenStream("EncryptionInfo", out var encryptionInfoStream))
            throw new UnsupportedOfficeEncryptionException("Not a recognized MS-OFFCRYPTO container: missing 'EncryptionInfo' stream.");
        if (!root.TryOpenStream("EncryptedPackage", out var encryptedPackageStream))
            throw new UnsupportedOfficeEncryptionException("Not a recognized MS-OFFCRYPTO container: missing 'EncryptedPackage' stream.");

        var encryptionInfo = ReadAllBytes(encryptionInfoStream);
        var encryptedPackage = ReadAllBytes(encryptedPackageStream);

        var (versionMajor, versionMinor) = (BinaryPrimitives.ReadUInt16LittleEndian(encryptionInfo.AsSpan(0, 2)),
                                             BinaryPrimitives.ReadUInt16LittleEndian(encryptionInfo.AsSpan(2, 2)));

        var scheme = DetermineScheme(versionMajor, versionMinor);
        _logger?.LogDebug("Detected {Scheme} encryption (version {Major}.{Minor}).", scheme, versionMajor, versionMinor);

        byte[] plaintext = scheme switch
        {
            OfficeEncryptionScheme.Standard => DecryptStandard(encryptionInfo, encryptedPackage, password),
            OfficeEncryptionScheme.Agile => DecryptAgile(encryptionInfo, encryptedPackage, password),
            _ => throw new UnsupportedOfficeEncryptionException($"Unsupported encryption version {versionMajor}.{versionMinor}.")
        };

        return new MemoryStream(plaintext, writable: false);
    }

    private static OfficeEncryptionScheme DetermineScheme(int versionMajor, int versionMinor)
    {
        // MS-OFFCRYPTO 2.1.4: version 4.4 is Agile. Versions 2.2/3.2/4.2 are "Standard"
        // (ECMA-376 / RC4 CryptoAPI variants). This implementation supports the AES-based Standard
        // scheme (major 3 or 4, minor 2); RC4-only legacy documents (major 2) are out of scope.
        if (versionMajor == 4 && versionMinor == 4) return OfficeEncryptionScheme.Agile;
        if ((versionMajor == 3 || versionMajor == 4) && versionMinor == 2) return OfficeEncryptionScheme.Standard;
        throw new UnsupportedOfficeEncryptionException(
            $"Encryption version {versionMajor}.{versionMinor} is not supported. Supported: Standard (3.2/4.2, AES) and Agile (4.4).");
    }

    private static byte[] ReadAllBytes(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    // ----------------------------------------------------------------------------------------
    // Standard Encryption (MS-OFFCRYPTO 2.3.4.5 - 2.3.4.9)
    // ----------------------------------------------------------------------------------------

    private byte[] DecryptStandard(byte[] encryptionInfo, byte[] encryptedPackage, string password)
    {
        // Layout after the 4-byte version+flags header (offset 4):
        //   EncryptionHeaderSize (4 bytes)
        //   EncryptionHeader (EncryptionHeaderSize bytes)
        //   EncryptionVerifier
        int offset = 4; // skip VersionMajor(2)+VersionMinor(2)
        offset += 4;    // skip EncryptionInfoFlags(4)

        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(encryptionInfo.AsSpan(offset, 4));
        offset += 4;
        int headerStart = offset;

        // EncryptionHeader fields we need: AlgID (offset 8 within header) and KeySize (offset 16 within header).
        uint algId = BinaryPrimitives.ReadUInt32LittleEndian(encryptionInfo.AsSpan(headerStart + 8, 4));
        uint keySizeBits = BinaryPrimitives.ReadUInt32LittleEndian(encryptionInfo.AsSpan(headerStart + 16, 4));
        if (keySizeBits == 0) keySizeBits = 128; // MS-OFFCRYPTO: 0 means default (40 for RC4, but for AES header this is always specified; default to 128 defensively)
        int keySizeBytes = (int)(keySizeBits / 8);

        if (algId is not (0x0000660E or 0x0000660F or 0x00006610) && algId != 0x00006801 /* not RC4 */)
        {
            // 0x660E=AES128, 0x660F=AES192, 0x6610=AES256. Anything else (e.g. RC4=0x6801) unsupported here.
        }
        if (algId != 0x0000660E && algId != 0x0000660F && algId != 0x00006610)
            throw new UnsupportedOfficeEncryptionException($"Standard encryption AlgID 0x{algId:X} is not supported (only AES-128/192/256).");

        offset = headerStart + headerSize;

        // EncryptionVerifier
        int saltSize = BinaryPrimitives.ReadInt32LittleEndian(encryptionInfo.AsSpan(offset, 4));
        offset += 4;
        byte[] salt = encryptionInfo.AsSpan(offset, saltSize).ToArray();
        offset += saltSize;
        byte[] encryptedVerifier = encryptionInfo.AsSpan(offset, 16).ToArray();
        offset += 16;
        int verifierHashSize = BinaryPrimitives.ReadInt32LittleEndian(encryptionInfo.AsSpan(offset, 4));
        offset += 4;
        // EncryptedVerifierHash is stored padded to a 16-byte (AES block) boundary.
        int encryptedVerifierHashLen = encryptionInfo.Length - offset;
        byte[] encryptedVerifierHash = encryptionInfo.AsSpan(offset, encryptedVerifierHashLen).ToArray();

        byte[] key = DeriveStandardKey(password, salt, keySizeBytes);

        // Validate password using the verifier: decrypt EncryptedVerifier (ECB, no padding) and hash it;
        // it must equal the decrypted EncryptedVerifierHash (truncated to verifierHashSize).
        byte[] verifierPlain = AesEcbDecryptBlock(key, encryptedVerifier);
        byte[] verifierHashPlain = AesEcbDecrypt(key, encryptedVerifierHash);
        byte[] computedHash = SHA1.HashData(verifierPlain);

        if (!computedHash.AsSpan(0, verifierHashSize).SequenceEqual(verifierHashPlain.AsSpan(0, verifierHashSize)))
            throw new InvalidDocumentPasswordException("The supplied password does not match this document.");

        // The EncryptedPackage stream: first 8 bytes are the LE64 length of the decrypted content,
        // the rest is AES-CBC ciphertext with a null (all-zero) IV, using the same key as above.
        ulong plainLength = BinaryPrimitives.ReadUInt64LittleEndian(encryptedPackage.AsSpan(0, 8));
        byte[] cipherBody = encryptedPackage.AsSpan(8).ToArray();

        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.IV = new byte[16];
        using var decryptor = aes.CreateDecryptor();
        byte[] plain = decryptor.TransformFinalBlock(cipherBody, 0, cipherBody.Length);

        if ((ulong)plain.Length > plainLength)
            plain = plain.AsSpan(0, (int)plainLength).ToArray();

        return plain;
    }

    /// <summary>
    /// MS-OFFCRYPTO 2.3.4.7 key derivation: iterated SHA-1 over salt+password, then expanded/truncated
    /// to the target key size using the 0x36/0x5C padding scheme when the hash is shorter than needed.
    /// </summary>
    private static byte[] DeriveStandardKey(string password, byte[] salt, int keySizeBytes)
    {
        byte[] passwordBytes = Encoding.Unicode.GetBytes(password); // UTF-16LE

        byte[] h = SHA1.HashData(Combine(salt, passwordBytes));
        for (uint i = 0; i < 50000; i++)
        {
            h = SHA1.HashData(Combine(LE32(i), h));
        }
        // Final hash combines with the "block number" (0 for both verifier and package key in Standard scheme).
        byte[] hFinal = SHA1.HashData(Combine(h, LE32(0)));

        if (hFinal.Length >= keySizeBytes)
            return hFinal.AsSpan(0, keySizeBytes).ToArray();

        // Expand using HMAC-like 0x36/0x5C padding (ECMA-376 §2.3.4.7).
        byte[] x1 = SHA1.HashData(XorPad(hFinal, 0x36));
        byte[] x2 = SHA1.HashData(XorPad(hFinal, 0x5C));
        byte[] combined = Combine(x1, x2);
        return combined.AsSpan(0, keySizeBytes).ToArray();
    }

    private static byte[] XorPad(byte[] hash, byte padByte)
    {
        var buf = new byte[64];
        for (int i = 0; i < 64; i++) buf[i] = padByte;
        for (int i = 0; i < hash.Length && i < 64; i++) buf[i] ^= hash[i];
        return buf;
    }

    private static byte[] AesEcbDecryptBlock(byte[] key, byte[] cipherBlock)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(cipherBlock, 0, cipherBlock.Length);
    }

    private static byte[] AesEcbDecrypt(byte[] key, byte[] cipher) => AesEcbDecryptBlock(key, cipher);

    // ----------------------------------------------------------------------------------------
    // Agile Encryption (MS-OFFCRYPTO 2.3.4.10+)
    // ----------------------------------------------------------------------------------------

    private byte[] DecryptAgile(byte[] encryptionInfo, byte[] encryptedPackage, string password)
    {
        // Bytes [0,8) are VersionMajor/Minor/Flags; the rest is a UTF-8 XML descriptor.
        string xml = Encoding.UTF8.GetString(encryptionInfo, 8, encryptionInfo.Length - 8);
        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/office/2006/encryption";
        XNamespace pns = "http://schemas.microsoft.com/office/2006/keyEncryptor/password";

        var keyData = doc.Root!.Element(ns + "keyData")
            ?? throw new UnsupportedOfficeEncryptionException("Agile EncryptionInfo missing <keyData>.");

        var keyEncryptor = doc.Root!.Element(ns + "keyEncryptors")?.Elements(ns + "keyEncryptor")
            .FirstOrDefault(e => (string?)e.Attribute("uri") == "http://schemas.microsoft.com/office/2006/keyEncryptor/password")
            ?? throw new UnsupportedOfficeEncryptionException("Agile EncryptionInfo has no password key encryptor.");

        var encryptedKeyEl = keyEncryptor.Element(pns + "encryptedKey")
            ?? throw new UnsupportedOfficeEncryptionException("Agile EncryptionInfo missing <p:encryptedKey>.");

        int spinCount = (int)encryptedKeyEl.Attribute("spinCount")!;
        int keyBits = (int)encryptedKeyEl.Attribute("keyBits")!;
        int keyBytes = keyBits / 8;
        int blockSize = (int)encryptedKeyEl.Attribute("blockSize")!;
        string hashAlgName = (string)encryptedKeyEl.Attribute("hashAlgorithm")!;
        byte[] pwSalt = Convert.FromBase64String((string)encryptedKeyEl.Attribute("saltValue")!);
        byte[] encryptedVerifierHashInput = Convert.FromBase64String((string)encryptedKeyEl.Attribute("encryptedVerifierHashInput")!);
        byte[] encryptedVerifierHashValue = Convert.FromBase64String((string)encryptedKeyEl.Attribute("encryptedVerifierHashValue")!);
        byte[] encryptedKeyValue = Convert.FromBase64String((string)encryptedKeyEl.Attribute("encryptedKeyValue")!);

        using var hashAlg = CreateHashAlgorithm(hashAlgName);

        byte[] passwordBytes = Encoding.Unicode.GetBytes(password);
        byte[] h = hashAlg.ComputeHash(Combine(pwSalt, passwordBytes));
        for (int i = 0; i < spinCount; i++)
        {
            h = hashAlg.ComputeHash(Combine(LE32((uint)i), h));
        }

        byte[] verifierInputKey = DeriveAgileBlockKey(hashAlg, h, AgileVerifierInputBlockKey, keyBytes);
        byte[] verifierValueKey = DeriveAgileBlockKey(hashAlg, h, AgileVerifierValueBlockKey, keyBytes);
        byte[] keyValueKey = DeriveAgileBlockKey(hashAlg, h, AgileKeyValueBlockKey, keyBytes);

        byte[] iv = ResizeIv(pwSalt, blockSize);

        byte[] verifierHashInput = AesCbcDecrypt(verifierInputKey, iv, encryptedVerifierHashInput);
        byte[] verifierHashValue = AesCbcDecrypt(verifierValueKey, iv, encryptedVerifierHashValue);
        byte[] computedVerifierHash = hashAlg.ComputeHash(verifierHashInput);

        int hashLen = computedVerifierHash.Length;
        if (!computedVerifierHash.AsSpan(0, hashLen).SequenceEqual(verifierHashValue.AsSpan(0, hashLen)))
            throw new InvalidDocumentPasswordException("The supplied password does not match this document.");

        byte[] secretKey = AesCbcDecrypt(keyValueKey, iv, encryptedKeyValue);
        if (secretKey.Length > keyBytes) secretKey = secretKey.AsSpan(0, keyBytes).ToArray();

        // Decrypt the EncryptedPackage stream: first 8 bytes = LE64 plaintext length, then 4096-byte
        // AES-CBC segments, each with its own IV = Hash(keyData.saltValue || LE32(segmentIndex)).
        byte[] keyDataSalt = Convert.FromBase64String((string)keyData.Attribute("saltValue")!);
        int keyDataBlockSize = (int)keyData.Attribute("blockSize")!;

        ulong plainLength = BinaryPrimitives.ReadUInt64LittleEndian(encryptedPackage.AsSpan(0, 8));
        byte[] cipherBody = encryptedPackage.AsSpan(8).ToArray();

        const int segmentSize = 4096;
        using var output = new MemoryStream((int)plainLength);
        int segmentIndex = 0;
        for (int pos = 0; pos < cipherBody.Length; pos += segmentSize)
        {
            int len = Math.Min(segmentSize, cipherBody.Length - pos);
            // Segment ciphertext lengths are always block-aligned in a valid file.
            byte[] segment = cipherBody.AsSpan(pos, len).ToArray();
            byte[] segmentIv = ResizeIv(hashAlg.ComputeHash(Combine(keyDataSalt, LE32((uint)segmentIndex))), keyDataBlockSize);
            byte[] segmentPlain = AesCbcDecrypt(secretKey, segmentIv, segment);
            output.Write(segmentPlain, 0, segmentPlain.Length);
            segmentIndex++;
        }

        byte[] result = output.ToArray();
        if ((ulong)result.Length > plainLength)
            result = result.AsSpan(0, (int)plainLength).ToArray();
        return result;
    }

    private static byte[] DeriveAgileBlockKey(HashAlgorithm hashAlg, byte[] finalIteratedHash, byte[] blockKey, int keyBytes)
    {
        byte[] h = hashAlg.ComputeHash(Combine(finalIteratedHash, blockKey));
        if (h.Length >= keyBytes) return h.AsSpan(0, keyBytes).ToArray();

        // Pad (rare: only if keyBits exceeds the hash output size).
        byte[] padded = new byte[keyBytes];
        Array.Copy(h, padded, h.Length);
        for (int i = h.Length; i < keyBytes; i++) padded[i] = 0x36;
        return padded;
    }

    private static byte[] ResizeIv(byte[] value, int blockSize)
    {
        if (value.Length == blockSize) return value;
        var result = new byte[blockSize];
        Array.Copy(value, result, Math.Min(value.Length, blockSize));
        for (int i = value.Length; i < blockSize; i++) result[i] = 0x36;
        return result;
    }

    private static HashAlgorithm CreateHashAlgorithm(string name) => name.ToUpperInvariant() switch
    {
        "SHA1" => SHA1.Create(),
        "SHA256" => SHA256.Create(),
        "SHA384" => SHA384.Create(),
        "SHA512" => SHA512.Create(),
        "MD5" => MD5.Create(),
        _ => throw new UnsupportedOfficeEncryptionException($"Unsupported hash algorithm '{name}' in Agile EncryptionInfo.")
    };

    private static byte[] AesCbcDecrypt(byte[] key, byte[] iv, byte[] cipher)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
    }

    private static byte[] Combine(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, result, 0, a.Length);
        Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
        return result;
    }

    private static byte[] LE32(uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return b;
    }
}
