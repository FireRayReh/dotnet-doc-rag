using Cryptex.Core.Interfaces;
using Cryptex.Ingestion.Parsers;
using Cryptex.Security;
using Microsoft.Extensions.Logging;

namespace Cryptex.Ingestion;

/// <summary>
/// Resolves the right <see cref="IDocumentParser"/> for a file (by extension/content-type) and,
/// transparently, decrypts password-protected documents before handing them to the underlying
/// format parser:
///  - PDF: password passed straight into PdfParser (PdfPig decrypts natively).
///  - XLSX: encrypted files go to EncryptedXlsxParser (EPPlus decrypts natively);
///          unencrypted files go to OpenXmlParser.
///  - DOCX/PPTX: encrypted files are pre-decrypted into a plain OOXML stream via
///          OfficeCryptoDecryptor, then handed to OpenXmlParser like any unencrypted file.
///  - TXT/MD/HTML: never encrypted; passed straight through.
/// </summary>
public sealed class DocumentParserFactory
{
    private readonly PdfParser _pdfParser;
    private readonly OpenXmlParser _openXmlParser;
    private readonly EncryptedXlsxParser _encryptedXlsxParser;
    private readonly PlainTextParser _plainTextParser;
    private readonly HtmlParser _htmlParser;
    private readonly OfficeCryptoDecryptor _officeCryptoDecryptor;
    private readonly ILogger<DocumentParserFactory>? _logger;

    private static readonly HashSet<string> OoxmlCryptoFormats = new() { "docx", "pptx" };

    public DocumentParserFactory(
        PdfParser pdfParser,
        OpenXmlParser openXmlParser,
        EncryptedXlsxParser encryptedXlsxParser,
        PlainTextParser plainTextParser,
        HtmlParser htmlParser,
        OfficeCryptoDecryptor officeCryptoDecryptor,
        ILogger<DocumentParserFactory>? logger = null)
    {
        _pdfParser = pdfParser;
        _openXmlParser = openXmlParser;
        _encryptedXlsxParser = encryptedXlsxParser;
        _plainTextParser = plainTextParser;
        _htmlParser = htmlParser;
        _officeCryptoDecryptor = officeCryptoDecryptor;
        _logger = logger;
    }

    public static string GetExtension(string fileName) => Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();

    public bool IsSupported(string fileName) => GetExtension(fileName) switch
    {
        "pdf" or "docx" or "xlsx" or "pptx" or "txt" or "md" or "markdown" or "html" or "htm" => true,
        _ => false
    };

    /// <summary>
    /// Parses the given file, resolving the correct parser and transparently handling
    /// encryption/decryption as described in the class summary.
    /// </summary>
    public async Task<ParsedDocument> ParseAsync(Stream content, string fileName, string? password, CancellationToken cancellationToken = default)
    {
        var ext = GetExtension(fileName);

        switch (ext)
        {
            case "pdf":
                return await _pdfParser.ParseAsync(content, fileName, password, cancellationToken);

            case "txt":
            case "md":
            case "markdown":
                return await _plainTextParser.ParseAsync(content, fileName, password, cancellationToken);

            case "html":
            case "htm":
                return await _htmlParser.ParseAsync(content, fileName, password, cancellationToken);

            case "xlsx":
                if (!string.IsNullOrEmpty(password) || IsEncryptedContainer(content))
                    return await _encryptedXlsxParser.ParseAsync(content, fileName, password, cancellationToken);
                return await _openXmlParser.ParseAsync(content, fileName, password, cancellationToken);

            case "docx":
            case "pptx":
                if (!string.IsNullOrEmpty(password) && IsEncryptedContainer(content))
                {
                    _logger?.LogInformation("Decrypting password-protected {Extension} file {FileName}.", ext, fileName);
                    using var decrypted = _officeCryptoDecryptor.Decrypt(content, password);
                    return await _openXmlParser.ParseAsync(decrypted, fileName, null, cancellationToken);
                }
                return await _openXmlParser.ParseAsync(content, fileName, password, cancellationToken);

            default:
                throw new NotSupportedException($"No parser registered for file extension '.{ext}'.");
        }
    }

    private static bool IsEncryptedContainer(Stream content) => OfficeCryptoDecryptor.IsEncryptedOfficeContainer(content);
}
