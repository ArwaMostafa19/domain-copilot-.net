using DomainCopilot.Infrastructure;

namespace DomainCopilot.Api.Configuration;

/// <summary>Maps the shared Python-compatible configuration parser into API options.</summary>
public static class CopilotConfiguration
{
    public static void Bind(IConfiguration configuration, CopilotOptions options)
    {
        InfrastructureOptions parsed;
        try { parsed = InfrastructureOptions.FromConfiguration(configuration); }
        catch (InvalidOperationException error) { throw new CopilotConfigurationException(error.Message); }

        options.PostgresUser = parsed.PostgresUser;
        options.PostgresPassword = parsed.PostgresPassword;
        options.PostgresDb = parsed.PostgresDb;
        options.DatabaseUrl = parsed.DatabaseUrl;
        options.AdminDatabaseUrl = parsed.AdminDatabaseUrl;
        options.AppDbPassword = parsed.AppDbPassword;
        options.AuthSecret = parsed.AuthSecret;
        options.LlmChain = parsed.Chain;
        options.GroqApiKey = parsed.GroqApiKey;
        options.GroqBaseUrl = parsed.GroqBaseUrl;
        options.GroqModel = parsed.GroqModel;
        options.GroqStreamUsage = parsed.GroqStreamUsage;
        options.GeminiApiKey = parsed.GeminiApiKey;
        options.GeminiBaseUrl = parsed.GeminiBaseUrl;
        options.GeminiModel = parsed.GeminiModel;
        options.GeminiStreamUsage = parsed.GeminiStreamUsage;
        options.OllamaBaseUrl = parsed.OllamaBaseUrl;
        options.OllamaChatModel = parsed.OllamaChatModel;
        options.EmbeddingProvider = parsed.EmbeddingProvider;
        options.EmbeddingModel = parsed.EmbeddingModel;
        options.EmbeddingDimensions = parsed.EmbeddingDimensions;
        options.LlmTimeoutSeconds = parsed.LlmTimeoutSeconds;
        options.LlmCooldownSeconds = parsed.LlmCooldownSeconds;
        options.RetrievalMinSimilarity = parsed.RetrievalMinSimilarity;
        options.RunMaxModelCalls = parsed.RunMaxModelCalls;
        options.RunTokenBudget = parsed.RunTokenBudget;
        options.RunTimeoutSeconds = parsed.RunTimeoutSeconds;
        options.RunStepTimeoutSeconds = parsed.RunStepTimeoutSeconds;
        options.TokenTtlSeconds = parsed.TokenTtlSeconds;
        options.CorsOrigins = parsed.CorsOrigins;
        options.MaxIngestFileBytes = parsed.MaxIngestFileBytes;
        options.DemoUserPassword = parsed.DemoUserPassword;
    }
}

public sealed class CopilotConfigurationException(string message) : Exception(message);
