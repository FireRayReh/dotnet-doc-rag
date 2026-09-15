using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Cryptex.Tests.TestSupport;

/// <summary>
/// Builds small, genuinely-valid PDF and DOCX files in-process, so the envelope tests can prove the
/// full decrypt -&gt; sniff -&gt; parse path works on real binary formats rather than on stand-in bytes.
/// </summary>
internal static class DocumentFixtures
{
    /// <summary>Builds a one-page PDF containing <paramref name="text"/>.</summary>
    public static byte[] CreatePdf(string text)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(595, 842);
        page.AddText(text, 12, new UglyToad.PdfPig.Core.PdfPoint(50, 750), font);
        return builder.Build();
    }

    /// <summary>Builds a DOCX whose body contains one paragraph per supplied line.</summary>
    public static byte[] CreateDocx(params string[] paragraphs)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(
                paragraphs.Select(p => new Paragraph(new Run(new Text(p))))));
            main.Document.Save();
        }

        return ms.ToArray();
    }
}
