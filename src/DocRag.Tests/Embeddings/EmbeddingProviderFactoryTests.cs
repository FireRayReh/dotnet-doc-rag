using DocRag.Core.Interfaces;
using DocRag.Embeddings;
using Xunit;

namespace DocRag.Tests.Embeddings;

public class EmbeddingProviderFactoryTests
{
    [Fact]
    public void Create_WithLocalProvider_ReturnsLocalEmbeddingProvider()
    {
        var options = new EmbeddingOptions
        {
            Provider = "Local",
            Local = new LocalEmbeddingOptions { BaseUrl = "http://localhost:11434/v1", Model = "bge-m3", Dimensions = 1024 }
        };

        using var httpClient = new HttpClient();
        IEmbeddingProvider provider = EmbeddingProviderFactory.Create(options, httpClient);

        Assert.IsType<LocalEmbeddingProvider>(provider);
        Assert.Equal("bge-m3", provider.ModelName);
        Assert.Equal(1024, provider.Dimensions);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("Local")]
    [InlineData("LOCAL")]
    public void Create_ProviderNameIsCaseInsensitive(string providerName)
    {
        var options = new EmbeddingOptions
        {
            Provider = providerName,
            Local = new LocalEmbeddingOptions { BaseUrl = "http://localhost:11434/v1", Model = "bge-m3" }
        };

        using var httpClient = new HttpClient();
        var provider = EmbeddingProviderFactory.Create(options, httpClient);

        Assert.IsType<LocalEmbeddingProvider>(provider);
    }

    [Fact]
    public void Create_WithAzureOpenAIProvider_ReturnsAzureProvider()
    {
        var options = new EmbeddingOptions
        {
            Provider = "AzureOpenAI",
            AzureOpenAI = new AzureOpenAIEmbeddingOptions
            {
                Endpoint = "https://example.openai.azure.com/",
                ApiKey = "fake-key-for-test",
                Deployment = "text-embedding-3-large",
                Dimensions = 3072
            }
        };

        using var httpClient = new HttpClient();
        var provider = EmbeddingProviderFactory.Create(options, httpClient);

        Assert.IsType<AzureOpenAIEmbeddingProvider>(provider);
        Assert.Equal("text-embedding-3-large", provider.ModelName);
        Assert.Equal(3072, provider.Dimensions);
    }

    [Fact]
    public void Create_WithUnknownProvider_Throws()
    {
        var options = new EmbeddingOptions { Provider = "SomethingElse" };
        using var httpClient = new HttpClient();

        Assert.Throws<NotSupportedException>(() => EmbeddingProviderFactory.Create(options, httpClient));
    }

    [Fact]
    public void Create_AzureOpenAIWithoutEndpoint_Throws()
    {
        var options = new EmbeddingOptions
        {
            Provider = "AzureOpenAI",
            AzureOpenAI = new AzureOpenAIEmbeddingOptions { Endpoint = "", ApiKey = "key" }
        };
        using var httpClient = new HttpClient();

        Assert.Throws<ArgumentException>(() => EmbeddingProviderFactory.Create(options, httpClient));
    }
}
