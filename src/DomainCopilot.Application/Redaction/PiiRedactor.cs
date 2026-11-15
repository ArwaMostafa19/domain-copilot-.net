using System.Text.RegularExpressions;
using System.Collections.Immutable;
using DomainCopilot.Domain.Llm;

namespace DomainCopilot.Application.Redaction;

public static partial class PiiRedactor
{
    private static readonly Regex[] Patterns = [
        new(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"(?<!\w)(?:\+?\d[\d .()-]{7,}\d)(?!\w)", RegexOptions.CultureInvariant),
        new(@"\b\d{14}\b", RegexOptions.CultureInvariant)];
    public static string RedactText(string text)
    {
        for (var i = 0; i < Patterns.Length; i++) text = Patterns[i].Replace(text, $"[REDACTED_PII_{i + 1}]");
        return text;
    }
    public static CompletionRequest RedactRequest(CompletionRequest request) => request with
    {
        Messages = request.Messages.Select(message => message with
        {
            Content = RedactText(message.Content),
            ToolCalls = message.ToolCalls.Select(call => call with { Arguments = RedactDictionary(call.Arguments) }).ToImmutableArray()
        }).ToImmutableArray()
    };

    private static object? RedactValue(object? value) => value switch
    {
        string text => RedactText(text), Dictionary<string, object?> dict => dict.ToDictionary(p => p.Key, p => RedactValue(p.Value)),
        System.Collections.IDictionary dict => dict.Cast<System.Collections.DictionaryEntry>().ToDictionary(e => e.Key.ToString()!, e => RedactValue(e.Value)),
        System.Collections.IList list => list.Cast<object?>().Select(RedactValue).ToList(), _ => value
    };
    private static Dictionary<string, object?> RedactDictionary(Dictionary<string, object?> value) =>
        value.ToDictionary(pair => pair.Key, pair => RedactValue(pair.Value), StringComparer.Ordinal);
}

public sealed class RedactingProvider(ILlmProvider provider) : ILlmProvider
{
    public string Name => provider.Name;
    public CompletionResult Complete(CompletionRequest request) => provider.Complete(PiiRedactor.RedactRequest(request));
    public IEnumerable<StreamEvent> Stream(CompletionRequest request, CancellationToken cancellationToken = default) => provider.Stream(PiiRedactor.RedactRequest(request), cancellationToken);
    public EmbeddingResult Embed(IReadOnlyList<string> texts) => provider.Embed(texts.Select(PiiRedactor.RedactText).ToArray());
    public bool Healthcheck() => provider.Healthcheck();
}
