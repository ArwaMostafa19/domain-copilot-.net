namespace DomainCopilot.Api.Configuration;

/// <summary>
/// Validated settings corresponding to the original Python configuration.
/// Use <see cref="CopilotConfiguration.Bind"/> to parse layered configuration.
/// </summary>
public sealed class CopilotOptions
{
    public const string SectionName = "Copilot";

    public string PostgresUser { get; set; } = string.Empty;
    public string PostgresPassword { get; set; } = string.Empty;
    public string PostgresDb { get; set; } = string.Empty;
    public string DatabaseUrl { get; set; } = string.Empty;
    public string AdminDatabaseUrl { get; set; } = string.Empty;
    public string AppDbPassword { get; set; } = string.Empty;
    public string AuthSecret { get; set; } = string.Empty;

    public IReadOnlyList<string> LlmChain { get; set; } = ["groq", "gemini", "ollama"];
    public string GroqApiKey { get; set; } = string.Empty;
    public string GroqBaseUrl { get; set; } = "https://api.groq.com/openai/v1";
    public string GroqModel { get; set; } = string.Empty;
    public bool GroqStreamUsage { get; set; } = true;
    public string GeminiApiKey { get; set; } = string.Empty;
    public string GeminiBaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/openai";
    public string GeminiModel { get; set; } = string.Empty;
    public bool GeminiStreamUsage { get; set; } = true;
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
    public string OllamaChatModel { get; set; } = string.Empty;

    public string EmbeddingProvider { get; set; } = "ollama";
    public string EmbeddingModel { get; set; } = "nomic-embed-text";
    public int EmbeddingDimensions { get; set; } = 768;
    public double LlmTimeoutSeconds { get; set; } = 180;
    public double LlmCooldownSeconds { get; set; } = 60;
    public double RetrievalMinSimilarity { get; set; } = 0.55;
    public int RunMaxModelCalls { get; set; } = 6;
    public int RunTokenBudget { get; set; } = 20_000;
    public double RunTimeoutSeconds { get; set; } = 120;
    public double RunStepTimeoutSeconds { get; set; } = 45;
    public int TokenTtlSeconds { get; set; } = 28_800;
    public IReadOnlyList<string> CorsOrigins { get; set; } = Array.Empty<string>();
    public int MaxIngestFileBytes { get; set; } = 10 * 1024 * 1024;
    public string DemoUserPassword { get; set; } = string.Empty;

    // Do not include keys, passwords, URLs that may carry credentials, or auth secrets.
    public override string ToString() =>
        $"CopilotOptions {{ LlmChain = [{string.Join(",", LlmChain)}], " +
        $"EmbeddingProvider = {EmbeddingProvider}, EmbeddingModel = {EmbeddingModel}, " +
        $"EmbeddingDimensions = {EmbeddingDimensions}, Secrets = [REDACTED] }}";
}
