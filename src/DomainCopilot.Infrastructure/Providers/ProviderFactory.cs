using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DomainCopilot.Application;
using DomainCopilot.Application.Embedding;
using DomainCopilot.Application.Llm;
using DomainCopilot.Application.Redaction;
using DomainCopilot.Domain.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using DomainCopilot.Infrastructure.Persistence;

namespace DomainCopilot.Infrastructure.Providers;

public sealed class InfrastructureProviderFactory(InfrastructureOptions options, IHttpClientFactory clients,
    IEmbeddingIndexRegistry registry)
{
    public ILlmProvider BuildChain()
    {
        foreach (var name in options.Chain) RequireChatSettings(name);
        var embeddingProvider = BuildOne(options.EmbeddingProvider);
        var completionProviders = options.Chain.Select(name => (ILlmProvider)new RedactingProvider(BuildOne(name))).ToArray();
        return new FallbackChain(completionProviders, new RedactingProvider(embeddingProvider), options.LlmCooldownSeconds);
    }

    public IEmbedder BuildEmbedder()
    {
        if (options.EmbeddingProvider == "gemini" && string.IsNullOrEmpty(options.GeminiApiKey))
            throw new ConfigError("provider 'gemini' needs GEMINI_API_KEY, but it is not set");
        var provider = new RedactingProvider(BuildOne(options.EmbeddingProvider));
        var spec = new EmbeddingSpec(options.EmbeddingModel, options.EmbeddingDimensions);
        var guard = new EmbeddingGuard(registry);
        guard.Ensure(spec);
        return new GuardedEmbedder(provider, guard, spec);
    }

    public ILlmProvider BuildOne(string name)
    {
        var client = clients.CreateClient("domain-copilot-provider");
        return name switch
        {
            "ollama" => new OllamaProvider(client, options.OllamaBaseUrl, options.EmbeddingModel,
                options.EmbeddingDimensions, NullIfEmpty(options.OllamaChatModel), options.LlmTimeoutSeconds),
            "fake" => new FakeLlmProvider(options.EmbeddingModel, options.EmbeddingDimensions),
            "groq" => new OpenAiCompatibleProvider("groq", options.GroqBaseUrl, options.GroqApiKey,
                NullIfEmpty(options.GroqModel), client, options.LlmTimeoutSeconds,
                streamUsage: options.GroqStreamUsage),
            "gemini" => new OpenAiCompatibleProvider("gemini", options.GeminiBaseUrl, options.GeminiApiKey,
                NullIfEmpty(options.GeminiModel), client, options.LlmTimeoutSeconds,
                options.EmbeddingProvider == "gemini" ? options.EmbeddingModel : null,
                options.GeminiStreamUsage),
            _ => throw new ConfigError($"unknown provider '{name}'")
        };
    }

    private void RequireChatSettings(string name)
    {
        var missing = name switch
        {
            "groq" when string.IsNullOrEmpty(options.GroqApiKey) => "GROQ_API_KEY",
            "groq" when string.IsNullOrEmpty(options.GroqModel) => "GROQ_MODEL",
            "gemini" when string.IsNullOrEmpty(options.GeminiApiKey) => "GEMINI_API_KEY",
            "gemini" when string.IsNullOrEmpty(options.GeminiModel) => "GEMINI_MODEL",
            "ollama" when string.IsNullOrEmpty(options.OllamaChatModel) => "OLLAMA_CHAT_MODEL",
            _ => null
        };
        if (missing is not null) throw new ConfigError($"provider '{name}' needs {missing}, but it is not set");
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
}

public sealed class FakeLlmProvider(string embeddingModel, int dimensions, string name = "fake") : ILlmProvider
{
    public string Name { get; } = name;
    public CompletionResult Complete(CompletionRequest request)
    {
        var content = FallbackText(request);
        return new CompletionResult(content, [], TokenEstimation.EstimateUsage(request.PromptText(), content), Name, Name);
    }

    public IEnumerable<StreamEvent> Stream(CompletionRequest request, CancellationToken cancellationToken = default)
    {
        var content = FallbackText(request);
        var parts = content.Split(' ');
        for (var index = 0; index < parts.Length; index++)
        {
            var piece = index + 1 < parts.Length ? parts[index] + " " : parts[index];
            if (piece.Length > 0) yield return new StreamEvent("token", piece);
        }
        yield return new StreamEvent("done", Result: new CompletionResult(content, [],
            TokenEstimation.EstimateUsage(request.PromptText(), content), Name, Name));
    }

    public EmbeddingResult Embed(IReadOnlyList<string> texts)
    {
        var vectors = texts.Select(VectorFor).ToImmutableArray();
        return new EmbeddingResult(vectors, embeddingModel, dimensions,
            new TokenUsage(texts.Sum(text => TokenEstimation.EstimateTokens(text)), 0, true), Name);
    }

    public bool Healthcheck() => true;

    private static string FallbackText(CompletionRequest request)
    {
        var last = request.Messages.LastOrDefault(message => message.Role == "user")?.Content ?? "";
        return $"fake response to: {last}";
    }

    private ImmutableArray<double> VectorFor(string text)
    {
        var values = new List<double>(dimensions);
        for (var counter = 0; values.Count < dimensions; counter++)
        {
            var block = SHA256.HashData(Encoding.UTF8.GetBytes($"{text}:{counter}"));
            values.AddRange(block.Select(value => value / 127.5 - 1.0));
        }
        values.RemoveRange(dimensions, values.Count - dimensions);
        var norm = Math.Sqrt(values.Sum(value => value * value));
        if (norm == 0) norm = 1;
        return values.Select(value => value / norm).ToImmutableArray();
    }
}

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddDomainCopilotInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var options = InfrastructureOptions.FromConfiguration(configuration);
        services.AddSingleton(options);
        services.AddSingleton<IPostgresConnections, PostgresConnections>();
        services.AddSingleton<IDatabaseReadiness, Persistence.PostgresReadinessProbe>();
        services.AddSingleton<IEmbeddingIndexRegistry, Persistence.PostgresEmbeddingIndexRegistry>();
        services.AddSingleton<IDocumentRepository, Persistence.PostgresDocumentRepository>();
        services.AddSingleton<IDocumentQueryRepository>(provider => (IDocumentQueryRepository)provider.GetRequiredService<IDocumentRepository>());
        services.AddSingleton<IChunkSearch, Persistence.PostgresChunkSearch>();
        services.AddSingleton<IRunRepository, Persistence.PostgresRunRepository>();
        services.AddSingleton<IWorkOrderRepository, Persistence.PostgresWorkOrderRepository>();
        services.AddSingleton<IAuditLog, Persistence.PostgresAuditLog>();
        services.AddSingleton<ISafetyPrerequisiteRepository, Persistence.PostgresSafetyPrerequisiteRepository>();
        services.AddSingleton<IUserRepository, Persistence.PostgresUserRepository>();
        services.AddSingleton<IUserProvisioner, Persistence.PostgresUserProvisioner>();
        services.AddSingleton<IAskLogRepository, Persistence.PostgresAskLogRepository>();
        services.AddSingleton<IUsageRepository, Persistence.PostgresUsageRepository>();
        services.AddSingleton<IRetrievalSettings>(new RetrievalSettings(options.RetrievalMinSimilarity));
        services.AddSingleton<IDocumentExtractor>(_ => new Extraction.FileSystemDocumentExtractor());
        services.AddHttpClient("domain-copilot-provider", client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<InfrastructureProviderFactory>();
        services.AddSingleton<ILlmProvider>(provider => provider.GetRequiredService<InfrastructureProviderFactory>().BuildChain());
        services.AddSingleton<IEmbedder>(provider => provider.GetRequiredService<InfrastructureProviderFactory>().BuildEmbedder());
        services.AddSingleton<MigrationRunner>();
        return services;
    }

    private sealed record RetrievalSettings(double RetrievalMinSimilarity) : IRetrievalSettings;
}






