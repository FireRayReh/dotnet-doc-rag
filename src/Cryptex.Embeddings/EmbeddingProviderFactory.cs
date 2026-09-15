using Cryptex.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Cryptex.Embeddings;

/// <summary>
/// Selects the active <see cref="IEmbeddingProvider"/> implementation based on
/// <c>Embedding:Provider</c> ("AzureOpenAI" or "Local"). Centralizing the switch here means the
/// embedding backend (and model) can change purely through configuration.
/// </summary>
public static class EmbeddingProviderFactory
{
    public static IEmbeddingProvider Create(EmbeddingOptions options, HttpClient localHttpClient, ILoggerFactory? loggerFactory = null)
    {
        return options.Provider.Trim().ToLowerInvariant() switch
        {
            "azureopenai" or "azure" => new AzureOpenAIEmbeddingProvider(
                options.AzureOpenAI,
                loggerFactory?.CreateLogger<AzureOpenAIEmbeddingProvider>()),

            "local" => new LocalEmbeddingProvider(
                localHttpClient,
                options.Local,
                loggerFactory?.CreateLogger<LocalEmbeddingProvider>()),

            var other => throw new NotSupportedException(
                $"Unknown Embedding:Provider '{other}'. Supported values: 'AzureOpenAI', 'Local'.")
        };
    }
}
