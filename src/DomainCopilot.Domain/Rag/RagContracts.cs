using System.Collections.Immutable;
using DomainCopilot.Domain.Llm;

namespace DomainCopilot.Domain.Rag;

public static class RefusalReason
{
    public const string NO_EVIDENCE = "no_evidence";
    public const string WEAK_EVIDENCE = "weak_evidence";
    public const string OUT_OF_SCOPE = "out_of_scope";
    public const string EQUIPMENT_NOT_IN_TENANT = "equipment_not_in_tenant";
    public const string INVALID_CITATION = "invalid_citation";
    public const string MISSING_CITATION = "missing_citation";
    public const string UNSAFE_OUTPUT = "unsafe_output";
}

public sealed record Evidence(
    long ChunkId,
    string DocId,
    string Title,
    string Section,
    string Revision,
    string DocStatus,
    int? Page,
    string Text,
    double DenseScore = 0.0,
    double KeywordScore = 0.0,
    double FusedScore = 0.0,
    string? Equipment = null);

public sealed record Citation(long ChunkId, string DocId, string Section, string Revision, int? Page);

public sealed record Answer
{
    public Answer(
        string Text,
        ImmutableArray<Citation> Citations,
        bool Refused,
        string? Reason,
        TokenUsage Usage,
        ImmutableArray<Evidence> Evidence)
    {
        this.Text = Text;
        this.Citations = DomainValueEquality.Normalize(Citations);
        this.Refused = Refused;
        this.Reason = Reason;
        this.Usage = Usage;
        this.Evidence = DomainValueEquality.Normalize(Evidence);
    }

    public string Text { get; init; }
    public ImmutableArray<Citation> Citations { get; init; }
    public bool Refused { get; init; }
    public string? Reason { get; init; }
    public TokenUsage Usage { get; init; }
    public ImmutableArray<Evidence> Evidence { get; init; }

    public bool Equals(Answer? other) => other is not null && Text == other.Text &&
        DomainValueEquality.SequenceEqual(Citations, other.Citations) && Refused == other.Refused &&
        Reason == other.Reason && Equals(Usage, other.Usage) &&
        DomainValueEquality.SequenceEqual(Evidence, other.Evidence);

    public override int GetHashCode() => HashCode.Combine(
        Text, DomainValueEquality.SequenceHash(Citations), Refused, Reason, Usage,
        DomainValueEquality.SequenceHash(Evidence));
}
