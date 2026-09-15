using System.Text;
using Cryptex.Ingestion.Parsers;
using Xunit;

namespace Cryptex.Tests.Parsers;

public class HtmlParserTests
{
    [Fact]
    public async Task ParseAsync_StripsTagsAndScripts()
    {
        var parser = new HtmlParser();
        var html = """
            <html>
              <head><title>Employee Handbook</title><style>body{color:red}</style></head>
              <body>
                <script>console.log('should be removed');</script>
                <h1>Welcome</h1>
                <p>This document explains company policy.</p>
              </body>
            </html>
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));

        var result = await parser.ParseAsync(stream, "handbook.html", password: null);

        Assert.Contains("Welcome", result.Text);
        Assert.Contains("company policy", result.Text);
        Assert.DoesNotContain("console.log", result.Text);
        Assert.DoesNotContain("color:red", result.Text);
        Assert.Equal("Employee Handbook", result.Metadata["title"]);
    }
}
