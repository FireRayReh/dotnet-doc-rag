using System.Text;
using Cryptex.Ingestion.Parsers;
using Xunit;

namespace Cryptex.Tests.Parsers;

public class PlainTextParserTests
{
    [Fact]
    public async Task ParseAsync_ReturnsRawText()
    {
        var parser = new PlainTextParser();
        var text = "Hello world.\nThis is a plain text document.";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));

        var result = await parser.ParseAsync(stream, "notes.txt", password: null);

        Assert.Equal(text, result.Text);
        Assert.Equal("txt", result.Metadata["format"]);
    }

    [Fact]
    public void SupportedExtensions_IncludesTxtAndMarkdown()
    {
        var parser = new PlainTextParser();
        Assert.Contains("txt", parser.SupportedExtensions);
        Assert.Contains("md", parser.SupportedExtensions);
    }
}
