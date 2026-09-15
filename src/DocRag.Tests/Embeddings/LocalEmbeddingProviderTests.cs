using System.Net;
using System.Text;
using System.Text.Json;
using DocRag.Embeddings;
using Moq;
using Moq.Protected;
using Xunit;

namespace DocRag.Tests.Embeddings;

public class LocalEmbeddingProviderTests
{
    private static HttpClient CreateMockedClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) => respond(req));

        return new HttpClient(handlerMock.Object);
    }

    [Fact]
    public async Task EmbedAsync_ParsesOpenAICompatibleResponse()
    {
        var responseJson = JsonSerializer.Serialize(new
        {
            data = new[]
            {
                new { index = 1, embedding = new[] { 0.4f, 0.5f } },
                new { index = 0, embedding = new[] { 0.1f, 0.2f, 0.3f } }
            }
        });

        var httpClient = CreateMockedClient(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        });

        var provider = new LocalEmbeddingProvider(httpClient, new LocalEmbeddingOptions { BaseUrl = "http://fake/v1", Model = "bge-m3" });

        var result = await provider.EmbedAsync(new[] { "first text", "second text" });

        Assert.Equal(2, result.Count);
        // Response is index-ordered back to request order regardless of server response order.
        Assert.Equal(new[] { 0.1f, 0.2f, 0.3f }, result[0]);
        Assert.Equal(new[] { 0.4f, 0.5f }, result[1]);
    }

    [Fact]
    public async Task EmbedAsync_EmptyInput_ReturnsEmptyWithoutCallingServer()
    {
        var called = false;
        var httpClient = CreateMockedClient(_ => { called = true; return new HttpResponseMessage(HttpStatusCode.OK); });
        var provider = new LocalEmbeddingProvider(httpClient, new LocalEmbeddingOptions { BaseUrl = "http://fake/v1", Model = "bge-m3" });

        var result = await provider.EmbedAsync(Array.Empty<string>());

        Assert.Empty(result);
        Assert.False(called);
    }

    [Fact]
    public async Task EmbedAsync_ServerError_ThrowsHttpRequestException()
    {
        var httpClient = CreateMockedClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom")
        });
        var provider = new LocalEmbeddingProvider(httpClient, new LocalEmbeddingOptions { BaseUrl = "http://fake/v1", Model = "bge-m3" });

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.EmbedAsync(new[] { "text" }));
    }

    [Fact]
    public async Task EmbedQueryAsync_ReturnsSingleVector()
    {
        var responseJson = JsonSerializer.Serialize(new { data = new[] { new { index = 0, embedding = new[] { 0.9f } } } });
        var httpClient = CreateMockedClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        });
        var provider = new LocalEmbeddingProvider(httpClient, new LocalEmbeddingOptions { BaseUrl = "http://fake/v1", Model = "bge-m3" });

        var result = await provider.EmbedQueryAsync("a query");

        Assert.Single(result);
        Assert.Equal(0.9f, result[0]);
    }
}
