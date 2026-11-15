using System.Collections.Concurrent;
using System.Diagnostics;
using DomainCopilot.Application.Text;
using DomainCopilot.Domain.Llm;

namespace DomainCopilot.Application.Llm;

public sealed class FallbackChain : ILlmProvider
{
    private static readonly Type[] CooldownErrors = [typeof(QuotaExceededError), typeof(ProviderAuthError)];
    private readonly ILlmProvider[] _providers;
    private readonly ILlmProvider _embedder;
    private readonly double _cooldownSeconds;
    private readonly Func<double> _clock;
    private readonly Dictionary<string, double> _blockedUntil = new(StringComparer.Ordinal);
    public string Name => "fallback-chain";

    public FallbackChain(IEnumerable<ILlmProvider> providers, ILlmProvider embedder, double cooldownSeconds = 60, Func<double>? clock = null)
    {
        _providers = providers.ToArray(); _embedder = embedder; _cooldownSeconds = cooldownSeconds;
        _clock = clock ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        if (_providers.Length == 0) throw new ConfigError("the fallback chain needs at least one completion provider");
        var names = _providers.Select(p => p.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw new ConfigError($"duplicate provider names in chain: [{string.Join(", ", names.Select(PythonString.Repr))}]");
    }

    public CompletionResult Complete(CompletionRequest request)
    {
        var failures = new List<ProviderError>();
        foreach (var provider in Candidates())
        {
            try { return provider.Complete(request); }
            catch (ProviderError error)
            {
                if (!error.FallbackAllowed) throw;
                RecordFailure(provider, error); failures.Add(error);
            }
        }
        throw new AllProvidersFailedError(failures);
    }

    public IEnumerable<StreamEvent> Stream(CompletionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var failures = new List<ProviderError>();
        foreach (var provider in Candidates())
        {
            var started = false;
            var failedBeforeStart = false;
            IEnumerator<StreamEvent>? enumerator = null;
            try
            {
                enumerator = provider.Stream(request, cancellationToken).GetEnumerator();
            }
            catch (ProviderError error)
            {
                if (!error.FallbackAllowed) throw;
                RecordFailure(provider, error);
                failures.Add(error);
                failedBeforeStart = true;
            }

            if (!failedBeforeStart)
            {
                using (enumerator)
                {
                    while (true)
                    {
                        StreamEvent? item;
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!enumerator!.MoveNext()) break;
                            item = enumerator.Current;
                        }
                        catch (ProviderError error)
                        {
                            if (started || !error.FallbackAllowed) throw;
                            RecordFailure(provider, error);
                            failures.Add(error);
                            failedBeforeStart = true;
                            break;
                        }
                        started = true;
                        yield return item;
                    }
                }
            }
            if (!failedBeforeStart) yield break;
        }
        throw new AllProvidersFailedError(failures);
    }

    public EmbeddingResult Embed(IReadOnlyList<string> texts) => _embedder.Embed(texts);
    public bool Healthcheck() => _providers.Any(p => p.Healthcheck()) && _embedder.Healthcheck();

    private IReadOnlyList<ILlmProvider> Candidates()
    {
        var now = _clock(); var ready = _providers.Where(p => !_blockedUntil.TryGetValue(p.Name, out var until) || until <= now).ToArray();
        return ready.Length > 0 ? ready : _providers;
    }

    private void RecordFailure(ILlmProvider provider, ProviderError error)
    {
        if (CooldownErrors.Any(t => t.IsInstanceOfType(error))) _blockedUntil[provider.Name] = _clock() + _cooldownSeconds;
    }
}
