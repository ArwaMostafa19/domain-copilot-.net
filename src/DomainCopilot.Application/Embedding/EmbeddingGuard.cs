using DomainCopilot.Domain.Llm;

namespace DomainCopilot.Application.Embedding;

public sealed class EmbeddingGuard(IEmbeddingIndexRegistry registry)
{
    private EmbeddingSpec? _spec;
    public void Ensure(EmbeddingSpec configured)
    {
        var recorded = registry.Get();
        if (recorded is null) { registry.Register(configured); recorded = registry.Get(); }
        if (recorded != configured)
            throw new EmbeddingModelMismatchError($"index was built with {recorded!.Model} ({recorded.Dimensions}d) but configuration asks for {configured.Model} ({configured.Dimensions}d); restore the configuration or re-index everything");
        _spec = configured;
    }
    public void Verify(EmbeddingResult result)
    {
        if (_spec is null) throw new ConfigError("EmbeddingGuard.ensure() must be called before verify()");
        var spec = _spec;
        if (result.Model != spec.Model || result.Dimensions != spec.Dimensions || result.Vectors.Any(v => v.Length != spec.Dimensions))
            throw new EmbeddingModelMismatchError($"provider {result.Provider} returned {result.Model} ({result.Dimensions}d), expected {spec.Model} ({spec.Dimensions}d)");
    }
}

public sealed class GuardedEmbedder(ILlmProvider provider, EmbeddingGuard guard, EmbeddingSpec spec) : IEmbedder
{
    private TokenUsage _total = new();
    public string ModelName { get; } = spec.Model;
    public int Dimension { get; } = spec.Dimensions;
    public TokenUsage TotalUsage => _total;
    public IReadOnlyList<IReadOnlyList<double>> Embed(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0) return Array.Empty<IReadOnlyList<double>>();
        var result = provider.Embed(texts);
        guard.Verify(result);
        _total = new TokenUsage(_total.PromptTokens + result.Usage.PromptTokens,
            _total.CompletionTokens + result.Usage.CompletionTokens, _total.Estimated || result.Usage.Estimated);
        return result.Vectors.Select(v => (IReadOnlyList<double>)v.ToArray()).ToArray();
    }
}
