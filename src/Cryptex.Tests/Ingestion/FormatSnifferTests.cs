using System.IO.Compression;
using System.Text;
using Cryptex.Ingestion;
using Cryptex.Tests.TestSupport;
using Xunit;

namespace Cryptex.Tests.Ingestion;

/// <summary>
/// Covers each detection branch of <see cref="FormatSniffer"/>: magic bytes, the OOXML
/// <c>[Content_Types].xml</c> discrimination, the <c>report.pdf.enc</c> file-name convention, the
/// text heuristic, and the give-up case.
/// </summary>
public class FormatSnifferTests
{
    [Fact]
    public void Sniff_PdfMagicBytes_ReturnsPdf()
    {
        var pdf = DocumentFixtures.CreatePdf("hello from a real PDF");

        var result = FormatSniffer.Sniff(pdf);

        Assert.Equal("pdf", result.Extension);
        Assert.Equal(FormatDetectionSource.MagicBytes, result.Source);
    }

    [Fact]
    public void Sniff_OoxmlZip_ReadsContentTypesToIdentifyDocx()
    {
        var docx = DocumentFixtures.CreateDocx("A real DOCX built in-test.");

        var result = FormatSniffer.Sniff(docx);

        Assert.Equal("docx", result.Extension);
        Assert.Equal(FormatDetectionSource.MagicBytes, result.Source);
    }

    [Theory]
    [InlineData("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml", "xlsx")]
    [InlineData("application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml", "pptx")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml", "docx")]
    public void Sniff_OoxmlZip_DistinguishesEachOfficeFormatByContentType(string contentType, string expected)
    {
        var zip = BuildOoxmlZip(contentType);

        var result = FormatSniffer.Sniff(zip);

        Assert.Equal(expected, result.Extension);
        Assert.Equal(FormatDetectionSource.MagicBytes, result.Source);
    }

    [Fact]
    public void Sniff_CompoundFileMagicBytes_ReturnsCfbPseudoFormat()
    {
        // OLE2/CFB: for Office content this means an MS-OFFCRYPTO-encrypted OOXML container, which
        // the pipeline hands to OfficeCryptoDecryptor and then sniffs again.
        var cfb = new byte[512];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(cfb, 0);

        var result = FormatSniffer.Sniff(cfb);

        Assert.Equal(FormatSniffer.CompoundFileFormat, result.Extension);
        Assert.Equal(FormatDetectionSource.MagicBytes, result.Source);
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><body><p>hi</p></body></html>")]
    [InlineData("<html lang=\"en\"><head><title>t</title></head></html>")]
    [InlineData("\n  <!doctype HTML>\n<html></html>")]
    public void Sniff_HtmlPrefixes_ReturnsHtml(string html)
    {
        var result = FormatSniffer.Sniff(Encoding.UTF8.GetBytes(html));

        Assert.Equal("html", result.Extension);
        Assert.Equal(FormatDetectionSource.MagicBytes, result.Source);
    }

    [Fact]
    public void Sniff_PlainText_FallsBackToTheTextHeuristic()
    {
        var bytes = Encoding.UTF8.GetBytes("Just some ordinary prose,\nacross two lines.\n");

        var result = FormatSniffer.Sniff(bytes);

        Assert.Equal("txt", result.Extension);
        Assert.Equal(FormatDetectionSource.TextHeuristic, result.Source);
    }

    [Fact]
    public void Sniff_Utf16Text_IsRecognisedByItsBom()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("wide chars")).ToArray();

        var result = FormatSniffer.Sniff(bytes);

        Assert.Equal("txt", result.Extension);
        Assert.Equal(FormatDetectionSource.TextHeuristic, result.Source);
    }

    [Fact]
    public void Sniff_UnrecognisableBinary_UsesTheInnerFileNameWhenPresent()
    {
        // No magic bytes, not text - but the name follows the report.xlsx.enc convention.
        var bytes = new byte[] { 0x01, 0x02, 0x00, 0xFF, 0xFE, 0x7F, 0x00, 0x11 };

        var result = FormatSniffer.Sniff(bytes, "quarterly-report.xlsx.enc");

        Assert.Equal("xlsx", result.Extension);
        Assert.Equal(FormatDetectionSource.InnerFileName, result.Source);
    }

    [Fact]
    public void Sniff_MagicBytesWinOverAMisleadingFileName()
    {
        var pdf = DocumentFixtures.CreatePdf("really a PDF");

        var result = FormatSniffer.Sniff(pdf, "mislabelled.docx.enc");

        Assert.Equal("pdf", result.Extension);
        Assert.Equal(FormatDetectionSource.MagicBytes, result.Source);
    }

    [Fact]
    public void Sniff_UnrecognisableBinaryWithNoUsableName_Throws()
    {
        var bytes = new byte[] { 0x01, 0x02, 0x00, 0xFF, 0xFE, 0x7F, 0x00, 0x11 };

        var ex = Assert.Throws<FormatDetectionException>(() => FormatSniffer.Sniff(bytes, "blob-00417.enc"));
        Assert.Contains("Could not determine the format", ex.Message);
    }

    [Theory]
    [InlineData("report.pdf.enc", null, "report.pdf")]
    [InlineData("report.pdf.aes", null, "report.pdf")]
    [InlineData("REPORT.PDF.ENC", null, "REPORT.PDF")]
    [InlineData("report.pdf", null, "report.pdf")]
    [InlineData("archive.pdf.sealed", new[] { ".sealed" }, "archive.pdf")]
    public void StripEncryptedExtension_RemovesOnlyTheConfiguredWrapperExtension(string input, string[]? extensions, string expected)
    {
        Assert.Equal(expected, FormatSniffer.StripEncryptedExtension(input, extensions));
    }

    [Fact]
    public void TrySniff_OnEmptyInput_ReturnsFalse()
    {
        Assert.False(FormatSniffer.TrySniff(Array.Empty<byte>(), "anything.pdf.enc", null, out _));
    }

    private static byte[] BuildOoxmlZip(string overrideContentType)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("[Content_Types].xml");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                $"<Override PartName=\"/part\" ContentType=\"{overrideContentType}\"/>" +
                "</Types>");
        }

        return ms.ToArray();
    }
}
