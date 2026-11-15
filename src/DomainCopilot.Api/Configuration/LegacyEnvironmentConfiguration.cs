namespace DomainCopilot.Api.Configuration;

/// <summary>
/// Maps the existing flat environment variable names into the Copilot options
/// section so the standard .NET configuration binder can bind the options.
/// Values retain their exact presence and contents so blank/invalid values can
/// be handled by the Python-compatible parser. The alias provider has higher
/// precedence than ordinary nested environment configuration.
/// </summary>
public static class LegacyEnvironmentConfiguration
{
    private static readonly string[] EnvironmentKeys =
    [
        "POSTGRES_USER", "POSTGRES_PASSWORD", "POSTGRES_DB", "DATABASE_URL", "ADMIN_DATABASE_URL",
        "APP_DB_PASSWORD", "AUTH_SECRET", "LLM_CHAIN", "GROQ_API_KEY", "GROQ_BASE_URL", "GROQ_MODEL",
        "GROQ_STREAM_USAGE", "GEMINI_API_KEY", "GEMINI_BASE_URL", "GEMINI_MODEL", "GEMINI_STREAM_USAGE",
        "OLLAMA_BASE_URL", "OLLAMA_CHAT_MODEL", "EMBEDDING_PROVIDER", "EMBEDDING_MODEL",
        "EMBEDDING_DIMENSIONS", "LLM_TIMEOUT_SECONDS", "LLM_COOLDOWN_SECONDS",
        "RETRIEVAL_MIN_SIMILARITY", "RUN_MAX_MODEL_CALLS", "RUN_TOKEN_BUDGET", "RUN_TIMEOUT_SECONDS",
        "RUN_STEP_TIMEOUT_SECONDS", "TOKEN_TTL_SECONDS", "CORS_ORIGINS", "MAX_INGEST_FILE_BYTES",
        "DEMO_USER_PASSWORD"
    ];

    public static IConfigurationBuilder AddLegacyEnvironmentAliases(this IConfigurationBuilder configuration)
    {
        var aliases = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var environmentName in EnvironmentKeys)
        {
            var value = Environment.GetEnvironmentVariable(environmentName);
            if (value is not null)
                aliases[$"{CopilotOptions.SectionName}:Legacy:{environmentName}"] = value;
        }

        configuration.AddInMemoryCollection(aliases);
        return configuration;
    }
}
