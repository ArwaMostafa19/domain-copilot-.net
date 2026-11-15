using System.Collections.Immutable;
using DomainCopilot.Domain;
using DomainCopilot.Domain.Documents;

namespace DomainCopilot.Domain.Llm;

/// <summary>A callable tool description supplied to a completion provider.</summary>
public sealed record ToolSpec
{
    public ToolSpec(string Name, string Description, Dictionary<string, object?> Parameters)
    {
        this.Name = Name;
        this.Description = Description;
        this.Parameters = Parameters;
    }

    public string Name { get; init; }
    public string Description { get; init; }
    public Dictionary<string, object?> Parameters { get; init; }

    public bool Equals(ToolSpec? other) => other is not null &&
        Name == other.Name && Description == other.Description &&
        DomainValueEquality.DictionaryEqual(Parameters, other.Parameters);

    public override int GetHashCode() => HashCode.Combine(Name, Description, DomainValueEquality.DictionaryHash(Parameters));
}

/// <summary>A tool invocation returned by a model.</summary>
public sealed record ToolCall
{
    public ToolCall(string Id, string Name, Dictionary<string, object?> Arguments)
    {
        this.Id = Id;
        this.Name = Name;
        this.Arguments = Arguments;
    }

    public string Id { get; init; }
    public string Name { get; init; }
    public Dictionary<string, object?> Arguments { get; init; }

    public bool Equals(ToolCall? other) => other is not null &&
        Id == other.Id && Name == other.Name &&
        DomainValueEquality.DictionaryEqual(Arguments, other.Arguments);

    public override int GetHashCode() => HashCode.Combine(Id, Name, DomainValueEquality.DictionaryHash(Arguments));
}

/// <summary>A message in a completion request. Role strings follow the provider contract.</summary>
public sealed record Message
{
    public Message(
        string Role,
        string Content = "",
        ImmutableArray<ToolCall> ToolCalls = default,
        string? ToolCallId = null)
    {
        this.Role = Role;
        this.Content = Content;
        this.ToolCalls = DomainValueEquality.Normalize(ToolCalls);
        this.ToolCallId = ToolCallId;
    }

    public string Role { get; init; }
    public string Content { get; init; }
    public ImmutableArray<ToolCall> ToolCalls { get; init; }
    public string? ToolCallId { get; init; }

    public bool Equals(Message? other) => other is not null && Role == other.Role && Content == other.Content &&
        ToolCallId == other.ToolCallId && DomainValueEquality.SequenceEqual(ToolCalls, other.ToolCalls);

    public override int GetHashCode() => HashCode.Combine(Role, Content, ToolCallId, DomainValueEquality.SequenceHash(ToolCalls));
}

public sealed record TokenUsage(int PromptTokens = 0, int CompletionTokens = 0, bool Estimated = false)
{
    public long TotalTokens => (long)PromptTokens + CompletionTokens;
}

public sealed record CompletionRequest
{
    public CompletionRequest(
        ImmutableArray<Message> Messages,
        ImmutableArray<ToolSpec> Tools = default,
        double Temperature = 0.0,
        int? MaxTokens = null,
        string? CorrelationId = null)
    {
        this.Messages = DomainValueEquality.Normalize(Messages);
        this.Tools = DomainValueEquality.Normalize(Tools);
        this.Temperature = Temperature;
        this.MaxTokens = MaxTokens;
        this.CorrelationId = CorrelationId;
    }

    public ImmutableArray<Message> Messages { get; init; }
    public ImmutableArray<ToolSpec> Tools { get; init; }
    public double Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public string? CorrelationId { get; init; }

    public string PromptText() => string.Join("\n", Messages.Select(message =>
        message.Content ?? throw new InvalidOperationException(
            "PromptText cannot join messages because a message has null content.")));

    public bool Equals(CompletionRequest? other) => other is not null &&
        DomainValueEquality.SequenceEqual(Messages, other.Messages) &&
        DomainValueEquality.SequenceEqual(Tools, other.Tools) && Temperature.Equals(other.Temperature) &&
        MaxTokens == other.MaxTokens && CorrelationId == other.CorrelationId;

    public override int GetHashCode() => HashCode.Combine(
        DomainValueEquality.SequenceHash(Messages), DomainValueEquality.SequenceHash(Tools),
        Temperature, MaxTokens, CorrelationId);
}

public sealed record CompletionResult
{
    public CompletionResult(
        string Content,
        ImmutableArray<ToolCall> ToolCalls,
        TokenUsage Usage,
        string Provider,
        string Model,
        string FinishReason = "stop")
    {
        this.Content = Content;
        this.ToolCalls = DomainValueEquality.Normalize(ToolCalls);
        this.Usage = Usage;
        this.Provider = Provider;
        this.Model = Model;
        this.FinishReason = FinishReason;
    }

    public string Content { get; init; }
    public ImmutableArray<ToolCall> ToolCalls { get; init; }
    public TokenUsage Usage { get; init; }
    public string Provider { get; init; }
    public string Model { get; init; }
    public string FinishReason { get; init; }

    public bool Equals(CompletionResult? other) => other is not null && Content == other.Content &&
        DomainValueEquality.SequenceEqual(ToolCalls, other.ToolCalls) && Equals(Usage, other.Usage) &&
        Provider == other.Provider && Model == other.Model && FinishReason == other.FinishReason;

    public override int GetHashCode() => HashCode.Combine(
        Content, DomainValueEquality.SequenceHash(ToolCalls), Usage, Provider, Model, FinishReason);
}

/// <summary>kind="token" carries text; kind="done" carries the final result.</summary>
public sealed record StreamEvent(string Kind, string Text = "", CompletionResult? Result = null);

public sealed record EmbeddingSpec(string Model, int Dimensions);

public sealed record EmbeddingResult
{
    public EmbeddingResult(
        ImmutableArray<ImmutableArray<double>> Vectors,
        string Model,
        int Dimensions,
        TokenUsage Usage,
        string Provider)
    {
        this.Vectors = DomainValueEquality.Normalize(Vectors).Select(DomainValueEquality.Normalize).ToImmutableArray();
        this.Model = Model;
        this.Dimensions = Dimensions;
        this.Usage = Usage;
        this.Provider = Provider;
    }

    public ImmutableArray<ImmutableArray<double>> Vectors { get; init; }
    public string Model { get; init; }
    public int Dimensions { get; init; }
    public TokenUsage Usage { get; init; }
    public string Provider { get; init; }

    public bool Equals(EmbeddingResult? other) => other is not null &&
        VectorsEqual(Vectors, other.Vectors) && Model == other.Model &&
        Dimensions == other.Dimensions && Equals(Usage, other.Usage) && Provider == other.Provider;

    public override int GetHashCode() => HashCode.Combine(
        VectorHash(Vectors),
        Model, Dimensions, Usage, Provider);

    private static bool VectorsEqual(ImmutableArray<ImmutableArray<double>> left, ImmutableArray<ImmutableArray<double>> right)
    {
        if (left.Length != right.Length) return false;
        for (var index = 0; index < left.Length; index++)
            if (!DomainValueEquality.SequenceEqual(left[index], right[index])) return false;
        return true;
    }

    private static int VectorHash(ImmutableArray<ImmutableArray<double>> vectors)
    {
        var hash = new HashCode();
        hash.Add(vectors.Length);
        foreach (var vector in vectors) hash.Add(DomainValueEquality.SequenceHash(vector));
        return hash.ToHashCode();
    }
}

public static class TokenEstimation
{
    /// <summary>Rough fallback (~4 Unicode code points per token).</summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var codePoints = 0;
        foreach (var _ in text.EnumerateRunes()) codePoints++;
        return Math.Max(1, codePoints / 4);
    }

    public static TokenUsage EstimateUsage(string? promptText, string? completionText) => new(
        EstimateTokens(promptText), EstimateTokens(completionText), Estimated: true);
}

public class LLMError : DomainError
{
    public LLMError() { }
    public LLMError(string? message) : base(message) { }
    public LLMError(string? message, Exception? innerException) : base(message, innerException) { }
}

public class ProviderError : LLMError
{
    public ProviderError(string Provider, string Message) : base($"[{Provider}] {Message}") => this.Provider = Provider;
    public string Provider { get; set; }
    public bool FallbackAllowed { get; set; } = true;
}

public class ProviderUnavailableError(string Provider, string Message) : ProviderError(Provider, Message);
public class QuotaExceededError(string Provider, string Message) : ProviderError(Provider, Message);
public class ProviderAuthError(string Provider, string Message) : ProviderError(Provider, Message);
public class ProviderResponseError(string Provider, string Message) : ProviderError(Provider, Message);

public class ProviderRequestError : ProviderError
{
    public ProviderRequestError(string Provider, string Message) : base(Provider, Message) => FallbackAllowed = false;
}

public sealed class AllProvidersFailedError : LLMError
{
    public AllProvidersFailedError(IEnumerable<ProviderError> Failures)
        : this(DomainValueEquality.Normalize(Failures.ToImmutableArray())) { }

    private AllProvidersFailedError(ImmutableArray<ProviderError> failures)
        : base($"all providers failed: {(failures.Length == 0 ? "no provider configured" : string.Join("; ", failures.Select(failure => failure.Message)))}") =>
        this.Failures = failures;

    public ImmutableArray<ProviderError> Failures { get; set; }
}

public class EmbeddingModelMismatchError(string? Message = null) : LLMError(Message);
public class ConfigError(string? Message = null) : LLMError(Message);
