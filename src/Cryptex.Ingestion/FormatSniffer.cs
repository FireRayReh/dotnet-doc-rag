using System.IO.Compression;
using System.Text;

namespace Cryptex.Ingestion;

/// <summary>Thrown when the real format of a decrypted blob cannot be established.</summary>
public sealed class FormatDetectionException : Exception
{
    public FormatDetectionException(string message) : base(message) { }
}

/// <summary>How <see cref="FormatSniffer"/> arrived at its answer - useful for logs and metadata.</summary>
public enum FormatDetectionSource
{
    MagicBytes,
    InnerFileName,
    TextHeuristic
}

/// <summary>The detected format of a blob.</summary>
/// <param name="Extension">
/// Lower-case extension without a dot, as <c>DocumentParserFactory</c> understands it: <c>pdf</c>,
/// <c>docx</c>, <c>xlsx</c>, <c>pptx</c>, <c>html</c>, <c>txt</c>, or the pseudo-format
/// <see cref="FormatSniffer.CompoundFileFormat"/>.
/// </param>
/// <param name="Source">Which detection rule matched.</param>
public readonly record struct SniffedFormat(string Extension, FormatDetectionSource Source);

/// <summary>
/// Works out what a blob of bytes actually is, for content that arrives with no usable extension -
/// notably the plaintext recovered from a customer-encrypted envelope, where the only file name on
/// record was something like <c>report.pdf.enc</c> (or just <c>blob-00417</c>).
/// </summary>
/// <remarks>
/// Detection runs in this order, first match wins:
/// <list type="number">
///   <item><description>Magic bytes (<c>%PDF-</c>, <c>PK\x03\x04</c> + <c>[Content_Types].xml</c>, CFB, HTML).</description></item>
///   <item><description>The inner file name, for the <c>report.pdf.enc</c> convention.</description></item>
///   <item><description>A UTF-8/UTF-16 printable-text heuristic.</description></item>
///   <item><description>Otherwise <see cref="FormatDetectionException"/>.</description></item>
/// </list>
/// </remarks>
public static class FormatSniffer
{
    /// <summary>
    /// Pseudo-format for an OLE2/CFB compound file. For Office content that means an
    /// MS-OFFCRYPTO-encrypted OOXML container, which must be handed to <c>OfficeCryptoDecryptor</c>
    /// (with a password) and then sniffed again.
    /// </summary>
    public const string CompoundFileFormat = "cfb";

    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();
    private static readonly byte[] ZipMagic = { 0x50, 0x4B, 0x03, 0x04 };
    private static readonly byte[] CfbMagic = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

    private static readonly string[] HtmlPrefixes = { "<!doctype html", "<html", "<?xml", "<!--" };

    private static readonly HashSet<string> KnownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "pdf", "docx", "xlsx", "pptx", "txt", "md", "markdown", "html", "htm"
    };

    /// <summary>
    /// Resolves the format of <paramref name="data"/>, or throws
    /// <see cref="FormatDetectionException"/> when nothing matches.
    /// </summary>
    /// <param name="data">The (already decrypted) raw bytes.</param>
    /// <param name="fileName">
    /// Optional original file name. When it follows the <c>report.pdf.enc</c> convention, the inner
    /// extension is used as a fallback after magic-byte detection.
    /// </param>
    /// <param name="encryptedExtensions">
    /// Extensions treated as "the encrypted wrapper" when stripping the outer extension off
    /// <paramref name="fileName"/>. Defaults to <c>.enc</c> and <c>.aes</c>.
    /// </param>
    public static SniffedFormat Sniff(
        ReadOnlySpan<byte> data,
        string? fileName = null,
        IReadOnlyCollection<string>? encryptedExtensions = null)
    {
        if (TrySniff(data, fileName, encryptedExtensions, out var format)) return format;

        throw new FormatDetectionException(
            "Could not determine the format of the decrypted content: it matched no known magic bytes " +
            "(PDF, OOXML zip, OLE2/CFB, HTML), the file name carried no usable inner extension, and it " +
            "is not decodable as UTF-8/UTF-16 text.");
    }

    /// <summary>Non-throwing variant of <see cref="Sniff"/>.</summary>
    public static bool TrySniff(
        ReadOnlySpan<byte> data,
        string? fileName,
        IReadOnlyCollection<string>? encryptedExtensions,
        out SniffedFormat format)
    {
        format = default;
        if (data.Length == 0) return false;

        // ---- 1. Magic bytes -------------------------------------------------------------------
        if (data.StartsWith(PdfMagic))
        {
            format = new SniffedFormat("pdf", FormatDetectionSource.MagicBytes);
            return true;
        }

        if (data.StartsWith(CfbMagic))
        {
            format = new SniffedFormat(CompoundFileFormat, FormatDetectionSource.MagicBytes);
            return true;
        }

        if (data.StartsWith(ZipMagic))
        {
            var ooxml = SniffOoxml(data);
            if (ooxml is not null)
            {
                format = new SniffedFormat(ooxml, FormatDetectionSource.MagicBytes);
                return true;
            }
            // A zip that isn't OOXML: fall through to the file-name/text rules below.
        }

        if (LooksLikeHtml(data))
        {
            format = new SniffedFormat("html", FormatDetectionSource.MagicBytes);
            return true;
        }

        // ---- 2. Inner file name (report.pdf.enc -> pdf) ----------------------------------------
        var inner = InnerExtension(fileName, encryptedExtensions);
        if (inner is not null)
        {
            format = new SniffedFormat(inner, FormatDetectionSource.InnerFileName);
            return true;
        }

        // ---- 3. Text heuristic -----------------------------------------------------------------
        if (LooksLikeText(data))
        {
            format = new SniffedFormat("txt", FormatDetectionSource.TextHeuristic);
            return true;
        }

        // ---- 4. Give up ------------------------------------------------------------------------
        return false;
    }

    /// <summary>
    /// Strips a configured encrypted extension off <paramref name="fileName"/> and returns the file
    /// name underneath (e.g. <c>report.pdf.enc</c> -&gt; <c>report.pdf</c>). Returns the name
    /// unchanged when it carries no encrypted extension.
    /// </summary>
    public static string StripEncryptedExtension(string fileName, IReadOnlyCollection<string>? encryptedExtensions = null)
    {
        if (string.IsNullOrEmpty(fileName)) return fileName;

        var outer = Path.GetExtension(fileName);
        if (outer.Length == 0) return fileName;

        foreach (var candidate in encryptedExtensions ?? DefaultEncryptedExtensions)
        {
            var normalized = candidate.StartsWith('.') ? candidate : "." + candidate;
            if (string.Equals(outer, normalized, StringComparison.OrdinalIgnoreCase))
                return fileName[..^outer.Length];
        }

        return fileName;
    }

    private static readonly string[] DefaultEncryptedExtensions = { ".enc", ".aes" };

    private static string? InnerExtension(string? fileName, IReadOnlyCollection<string>? encryptedExtensions)
    {
        if (string.IsNullOrEmpty(fileName)) return null;

        var stripped = StripEncryptedExtension(fileName, encryptedExtensions);
        var ext = Path.GetExtension(stripped).TrimStart('.').ToLowerInvariant();
        return KnownExtensions.Contains(ext) ? ext : null;
    }

    /// <summary>
    /// Opens the OOXML zip and reads <c>[Content_Types].xml</c> to tell docx/xlsx/pptx apart -
    /// the zip magic alone is identical for all three.
    /// </summary>
    private static string? SniffOoxml(ReadOnlySpan<byte> data)
    {
        try
        {
            using var ms = new MemoryStream(data.ToArray(), writable: false);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

            var entry = zip.GetEntry("[Content_Types].xml");
            if (entry is null) return null;

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var contentTypes = reader.ReadToEnd();

            if (contentTypes.Contains("wordprocessingml.document", StringComparison.OrdinalIgnoreCase)) return "docx";
            if (contentTypes.Contains("spreadsheetml.sheet", StringComparison.OrdinalIgnoreCase)) return "xlsx";
            if (contentTypes.Contains("presentationml.presentation", StringComparison.OrdinalIgnoreCase)) return "pptx";
            if (contentTypes.Contains("presentationml.slideshow", StringComparison.OrdinalIgnoreCase)) return "pptx";

            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool LooksLikeHtml(ReadOnlySpan<byte> data)
    {
        var probe = data[..Math.Min(data.Length, 512)];
        string text;
        try
        {
            text = Encoding.UTF8.GetString(probe).TrimStart('﻿').TrimStart();
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (text.Length == 0) return false;

        foreach (var prefix in HtmlPrefixes)
        {
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            // "<?xml"/"<!--" only count as HTML if an <html> tag actually shows up in the probe.
            if (prefix is "<!doctype html" or "<html") return true;
            if (text.Contains("<html", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// Treats the blob as text when it decodes cleanly as UTF-8 (or carries a UTF-16 BOM) and is
    /// overwhelmingly printable. Binary formats with no recognised magic bytes fail this.
    /// </summary>
    private static bool LooksLikeText(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 2 && ((data[0] == 0xFF && data[1] == 0xFE) || (data[0] == 0xFE && data[1] == 0xFF)))
            return true;

        var probeLength = Math.Min(data.Length, 4096);
        if (probeLength < data.Length)
        {
            // Back off to a UTF-8 character boundary so a truncated probe doesn't look invalid.
            while (probeLength > 0 && (data[probeLength] & 0xC0) == 0x80) probeLength--;
        }
        var probe = data[..probeLength];

        // A NUL byte in a UTF-8 stream means binary.
        foreach (var b in probe)
        {
            if (b == 0x00) return false;
        }

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(probe);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (text.Length == 0) return false;

        var printable = 0;
        foreach (var c in text)
        {
            if (!char.IsControl(c) || c is '\r' or '\n' or '\t' or '\f') printable++;
        }

        return (double)printable / text.Length >= 0.95;
    }
}
