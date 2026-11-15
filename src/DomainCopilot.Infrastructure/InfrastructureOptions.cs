using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace DomainCopilot.Infrastructure;

public sealed class InfrastructureOptions
{
    public const string SectionName = "Copilot";
    private static readonly string[] ChatProviders = ["groq", "gemini", "ollama", "fake"];
    private static readonly string[] EmbeddingProviders = ["ollama", "gemini", "fake"];

    public string PostgresUser { get; init; } = "";
    public string PostgresPassword { get; init; } = "";
    public string PostgresDb { get; init; } = "";
    public string DatabaseUrl { get; init; } = "";
    public string AdminDatabaseUrl { get; init; } = "";
    public string AppDbPassword { get; init; } = "";
    public string AuthSecret { get; init; } = "";
    public string LlmChain { get; init; } = "groq,gemini,ollama";
    public string GroqApiKey { get; init; } = "";
    public string GroqBaseUrl { get; init; } = "https://api.groq.com/openai/v1";
    public string GroqModel { get; init; } = "openai/gpt-oss-120b";
    public bool GroqStreamUsage { get; init; } = true;
    public string GeminiApiKey { get; init; } = "";
    public string GeminiBaseUrl { get; init; } = "https://generativelanguage.googleapis.com/v1beta/openai";
    public string GeminiModel { get; init; } = "gemini-3.8-flash";
    public bool GeminiStreamUsage { get; init; } = true;
    public string OllamaBaseUrl { get; init; } = "http://localhost:11434";
    public string OllamaChatModel { get; init; } = "llama3.2:3b";
    public string EmbeddingProvider { get; init; } = "ollama";
    public string EmbeddingModel { get; init; } = "nomic-embed-text";
    public int EmbeddingDimensions { get; init; } = 768;
    public double LlmTimeoutSeconds { get; init; } = 180;
    public double LlmCooldownSeconds { get; init; } = 60;
    public double RetrievalMinSimilarity { get; init; } = .55;
    public int RunMaxModelCalls { get; init; } = 6;
    public int RunTokenBudget { get; init; } = 20_000;
    public double RunTimeoutSeconds { get; init; } = 120;
    public double RunStepTimeoutSeconds { get; init; } = 45;
    public int TokenTtlSeconds { get; init; } = 28_800;
    public IReadOnlyList<string> CorsOrigins { get; init; } = Array.Empty<string>();
    public int MaxIngestFileBytes { get; init; } = 10 * 1024 * 1024;
    public string DemoUserPassword { get; init; } = "";

    public IReadOnlyList<string> Chain => LlmChain.Split(',', StringSplitOptions.TrimEntries);

    public static InfrastructureOptions FromConfiguration(IConfiguration configuration)
    {
        string? Raw(string legacy, string property) =>
            configuration[$"{SectionName}:Legacy:{legacy}"]
            ?? configuration[$"{SectionName}:{property}"]
            ?? configuration[legacy];

        string Read(string legacy, string property, string fallback = "")
        {
            var value = Raw(legacy, property);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        bool ReadBool(string name, string property, bool fallback)
        {
            var value = Raw(name, property);
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            return value.Trim().ToLowerInvariant() switch
            {
                "true" or "1" => true,
                "false" or "0" => false,
                _ => throw new InvalidOperationException($"{name} must be true, false, 1 or 0")
            };
        }

        int ReadPositiveInt(string name, string property, int fallback)
        {
            var value = Read(name, property, fallback.ToString(CultureInfo.InvariantCulture));
            if (!TryPythonInteger(value, out var parsed) || parsed <= 0)
                throw new InvalidOperationException($"{name} must be an integer greater than 0");
            return parsed;
        }

        double ReadFloat(string name, string property, double fallback, bool positive = false, bool unit = false)
        {
            var value = Read(name, property, fallback.ToString(CultureInfo.InvariantCulture));
            if (!TryPythonFloat(value, out var parsed) ||
                (positive && parsed <= 0) || (unit && !(parsed >= 0 && parsed <= 1)))
                throw new InvalidOperationException(unit ? $"{name} must be a number between 0 and 1" :
                    positive ? $"{name} must be a number greater than 0" : $"{name} must be a number");
            return parsed;
        }

        var rawChain = Raw("LLM_CHAIN", "LlmChain") ?? "groq,gemini,ollama";
        if (string.IsNullOrWhiteSpace(rawChain))
            throw new InvalidOperationException("LLM_CHAIN is empty; allowed provider names are: " + string.Join(", ", ChatProviders));
        var chain = rawChain.Split(',').Select(value => value.Trim()).ToArray();
        foreach (var provider in chain)
            if (!ChatProviders.Contains(provider, StringComparer.Ordinal))
                throw new InvalidOperationException($"unknown provider '{provider}' in LLM_CHAIN; allowed provider names are: {string.Join(", ", ChatProviders)}");

        var embeddingProvider = Read("EMBEDDING_PROVIDER", "EmbeddingProvider", "ollama");
        if (!EmbeddingProviders.Contains(embeddingProvider, StringComparer.Ordinal))
            throw new InvalidOperationException($"EMBEDDING_PROVIDER must be one of: {string.Join(", ", EmbeddingProviders)}");

        return new InfrastructureOptions
        {
            PostgresUser = Read("POSTGRES_USER", "PostgresUser"),
            PostgresPassword = Read("POSTGRES_PASSWORD", "PostgresPassword"),
            PostgresDb = Read("POSTGRES_DB", "PostgresDb"),
            DatabaseUrl = Read("DATABASE_URL", "DatabaseUrl"),
            AdminDatabaseUrl = Read("ADMIN_DATABASE_URL", "AdminDatabaseUrl"),
            AppDbPassword = Read("APP_DB_PASSWORD", "AppDbPassword"),
            AuthSecret = Read("AUTH_SECRET", "AuthSecret"),
            LlmChain = string.Join(",", chain),
            GroqApiKey = Read("GROQ_API_KEY", "GroqApiKey"),
            GroqBaseUrl = Read("GROQ_BASE_URL", "GroqBaseUrl", "https://api.groq.com/openai/v1"),
            GroqModel = Read("GROQ_MODEL", "GroqModel", "openai/gpt-oss-120b"),
            GroqStreamUsage = ReadBool("GROQ_STREAM_USAGE", "GroqStreamUsage", true),
            GeminiApiKey = Read("GEMINI_API_KEY", "GeminiApiKey"),
            GeminiBaseUrl = Read("GEMINI_BASE_URL", "GeminiBaseUrl", "https://generativelanguage.googleapis.com/v1beta/openai"),
            GeminiModel = Read("GEMINI_MODEL", "GeminiModel", "gemini-3.8-flash"),
            GeminiStreamUsage = ReadBool("GEMINI_STREAM_USAGE", "GeminiStreamUsage", true),
            OllamaBaseUrl = Read("OLLAMA_BASE_URL", "OllamaBaseUrl", "http://localhost:11434"),
            OllamaChatModel = Read("OLLAMA_CHAT_MODEL", "OllamaChatModel", "llama3.2:3b"),
            EmbeddingProvider = embeddingProvider,
            EmbeddingModel = Read("EMBEDDING_MODEL", "EmbeddingModel", "nomic-embed-text"),
            EmbeddingDimensions = ReadPositiveInt("EMBEDDING_DIMENSIONS", "EmbeddingDimensions", 768),
            LlmTimeoutSeconds = ReadFloat("LLM_TIMEOUT_SECONDS", "LlmTimeoutSeconds", 180, positive: true),
            LlmCooldownSeconds = ReadFloat("LLM_COOLDOWN_SECONDS", "LlmCooldownSeconds", 60),
            RetrievalMinSimilarity = ReadFloat("RETRIEVAL_MIN_SIMILARITY", "RetrievalMinSimilarity", .55, unit: true),
            RunMaxModelCalls = ReadPositiveInt("RUN_MAX_MODEL_CALLS", "RunMaxModelCalls", 6),
            RunTokenBudget = ReadPositiveInt("RUN_TOKEN_BUDGET", "RunTokenBudget", 20_000),
            RunTimeoutSeconds = ReadFloat("RUN_TIMEOUT_SECONDS", "RunTimeoutSeconds", 120, positive: true),
            RunStepTimeoutSeconds = ReadFloat("RUN_STEP_TIMEOUT_SECONDS", "RunStepTimeoutSeconds", 45, positive: true),
            TokenTtlSeconds = ReadPositiveInt("TOKEN_TTL_SECONDS", "TokenTtlSeconds", 28_800),
            CorsOrigins = Read("CORS_ORIGINS", "CorsOrigins").Split(',')
                .Select(origin => origin.Trim().TrimEnd('/')).Where(origin => origin.Length > 0).ToArray(),
            MaxIngestFileBytes = ReadPositiveInt("MAX_INGEST_FILE_BYTES", "MaxIngestFileBytes", 10 * 1024 * 1024),
            DemoUserPassword = Read("DEMO_USER_PASSWORD", "DemoUserPassword")
        };
    }

    private static bool TryPythonInteger(string value, out int result)
    {
        result = default;
        var normalized = RemoveDigitSeparators(value);
        return normalized is not null &&
            int.TryParse(normalized, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryPythonFloat(string value, out double result)
    {
        result = default;
        var normalized = RemoveDigitSeparators(value);
        return normalized is not null &&
            double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    private static string? RemoveDigitSeparators(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '_') continue;
            if (index == 0 || index == value.Length - 1 ||
                !char.IsDigit(value[index - 1]) || !char.IsDigit(value[index + 1]))
                return null;
        }
        return value.Replace("_", "", StringComparison.Ordinal);
    }

    public override string ToString() =>
        $"InfrastructureOptions {{ DatabaseConfigured = {!string.IsNullOrEmpty(DatabaseUrl)}, " +
        $"AdminDatabaseConfigured = {!string.IsNullOrEmpty(AdminDatabaseUrl)}, LlmChain = [{string.Join(",", Chain)}], " +
        $"EmbeddingProvider = {EmbeddingProvider}, EmbeddingModel = {EmbeddingModel}, Secrets = [REDACTED] }}";
}

public sealed class InfrastructureOptionsValidator : Microsoft.Extensions.Options.IValidateOptions<InfrastructureOptions>
{
    public Microsoft.Extensions.Options.ValidateOptionsResult Validate(string? name, InfrastructureOptions options) =>
        string.IsNullOrWhiteSpace(options.DatabaseUrl)
            ? Microsoft.Extensions.Options.ValidateOptionsResult.Fail("DATABASE_URL is required for PostgreSQL infrastructure.")
            : Microsoft.Extensions.Options.ValidateOptionsResult.Success;
}
