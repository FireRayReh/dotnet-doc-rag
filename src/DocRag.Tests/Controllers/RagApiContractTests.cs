using System.Net;
using System.Net.Http.Json;
using DocRag.Api.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DocRag.Tests.Controllers;

/// <summary>
/// Boots the real ASP.NET Core host (via WebApplicationFactory) to verify the parts of the
/// LibreChat rag_api contract that don't require a live Qdrant/embedding backend - i.e. that the
/// service starts, DI wires up, and /health responds with the documented shape. Endpoints that
/// need a running Qdrant instance and embedding provider (/embed, /query, /documents) are exercised
/// by design/integration testing outside this unit-test project; see docker-compose for a full
/// local stack.
/// </summary>
public class RagApiContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RagApiContractTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Embedding:Provider", "Local");
            builder.UseSetting("Embedding:Local:BaseUrl", "http://localhost:1/v1");
            builder.UseSetting("VectorStore:Qdrant:Host", "localhost");
        });
    }

    [Fact]
    public async Task Health_ReturnsUpStatus()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.NotNull(body);
        Assert.Equal("UP", body!.Status);
    }

    [Fact]
    public async Task Health_ReturnsExpectedContentType()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Query_WithEmptyQuery_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/query", new QueryRequest { Query = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeleteMany_WithNoIds_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/documents")
        {
            Content = JsonContent.Create(new DeleteManyRequest { FileIds = new List<string>() })
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
