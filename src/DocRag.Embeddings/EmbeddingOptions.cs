namespace DocRag.Embeddings;

public sealed class EmbeddingOptions
{
    public const string SectionName = "Embedding";

    /// <summary>"AzureOpenAI" or "Local".</summary>
    public string Provider { get; set; } = "Local";

    public AzureOpenAIEmbeddingOptions AzureOpenAI { get; set; } = new();
    public LocalEmbeddingOptions Local { get; set; } = new();
}

public sealed class AzureOpenAIEmbeddingOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>The Azure deployment name (not necessarily the same as the underlying model name).</summary>
    public string Deployment { get; set; } = "text-embedding-3-large";
    public int Dimensions { get; set; } = 3072;
}

public sealed class LocalEmbeddingOptions
{
    /// <summary>Base URL of an OpenAI-compatible server (Ollama, vLLM, llama.cpp server, etc.), e.g. http://localhost:11434/v1</summary>
    public string BaseUrl { get; set; } = "http://localhost:11434/v1";
    public string Model { get; set; } = "bge-m3";
    public int Dimensions { get; set; } = 1024;
    public string? ApiKey { get; set; }
}
