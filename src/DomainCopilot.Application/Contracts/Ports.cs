using System.Collections.Immutable;
using DomainCopilot.Domain.Documents;
using DomainCopilot.Domain.Llm;
using DomainCopilot.Domain.Workflow;

namespace DomainCopilot.Application;

public interface IEmbedder
{
    string ModelName { get; }
    int Dimension { get; }
    IReadOnlyList<IReadOnlyList<double>> Embed(IReadOnlyList<string> texts);
}

public interface ILlmProvider
{
    string Name { get; }
    CompletionResult Complete(CompletionRequest request);
    IEnumerable<StreamEvent> Stream(CompletionRequest request, CancellationToken cancellationToken = default);
    EmbeddingResult Embed(IReadOnlyList<string> texts);
    bool Healthcheck() => true;
}

public interface IEmbeddingIndexRegistry { EmbeddingSpec? Get(); void Register(EmbeddingSpec spec); }
public interface IDocumentRepository
{
    string? StoredFingerprint(DocumentMeta meta);
    void ReplaceDocument(DocumentMeta meta, IReadOnlyList<Chunk> chunks, IReadOnlyList<IReadOnlyList<double>> embeddings, IReadOnlyList<SafetyStep> steps, string embeddingModel);
    void MarkFailed(DocumentMeta meta, string error);
}

public sealed record DocumentSummaryRecord(string Id, string DocId, string Title, string? EquipmentId,
    string Revision, string Status, string? Fingerprint, string? IngestError, long ChunkCount);
public interface IDocumentQueryRepository
{
    IReadOnlyList<DocumentSummaryRecord> List(string tenantId);
    DocumentSummaryRecord? Get(string tenantId, string idOrDocId);
}

public sealed record DailyAskUsage(DateOnly Day, long PromptTokens, long CompletionTokens, long AskCount);
public sealed record DailyRunUsage(DateOnly Day, long TotalTokens, long RunCount);
public sealed record UsageSummary(IReadOnlyList<DailyAskUsage> DailyAsks, IReadOnlyList<DailyRunUsage> DailyRuns);
public interface IUsageRepository
{
    UsageSummary ForTenant(string tenantId);
    UsageSummary ForUser(string tenantId, long userId);
}
public interface IDatabaseReadiness
{
    ValueTask CheckAsync(CancellationToken cancellationToken = default);
}

public sealed record IngestResult(string Path, string Status, int Chunks = 0, int Steps = 0, string? Error = null);
public sealed record UserRecord(long Id, string TenantId, string Username, string Role, bool Active, string PasswordHash = "");
public sealed record RunRecord(string Id, string TenantId, long UserId, string Question, string Status, int TotalTokens, bool EstimatedTokens);
public sealed record RunStepRecord
{
    public RunStepRecord(int Ordinal, string Agent, ImmutableArray<string> ToolsUsed = default, ImmutableArray<long> EvidenceIds = default,
        int PromptTokens = 0, int CompletionTokens = 0, bool Estimated = false, int DurationMs = 0, string Outcome = "", string? Summary = null)
    { this.Ordinal = Ordinal; this.Agent = Agent; this.ToolsUsed = ToolsUsed.IsDefault ? [] : ToolsUsed; this.EvidenceIds = EvidenceIds.IsDefault ? [] : EvidenceIds;
      this.PromptTokens = PromptTokens; this.CompletionTokens = CompletionTokens; this.Estimated = Estimated; this.DurationMs = DurationMs; this.Outcome = Outcome; this.Summary = Summary; }
    public int Ordinal { get; } public string Agent { get; } public ImmutableArray<string> ToolsUsed { get; } public ImmutableArray<long> EvidenceIds { get; }
    public int PromptTokens { get; } public int CompletionTokens { get; } public bool Estimated { get; } public int DurationMs { get; } public string Outcome { get; } public string? Summary { get; }
    public bool Equals(RunStepRecord? other) => other is not null && Ordinal == other.Ordinal && Agent == other.Agent &&
        ApplicationValueEquality.SequenceEqual(ToolsUsed, other.ToolsUsed) && ApplicationValueEquality.SequenceEqual(EvidenceIds, other.EvidenceIds) &&
        PromptTokens == other.PromptTokens && CompletionTokens == other.CompletionTokens && Estimated == other.Estimated && DurationMs == other.DurationMs && Outcome == other.Outcome && Summary == other.Summary;
    public override int GetHashCode() => HashCode.Combine(Ordinal, Agent, ApplicationValueEquality.SequenceHash(ToolsUsed), ApplicationValueEquality.SequenceHash(EvidenceIds),
        PromptTokens, CompletionTokens, Estimated, HashCode.Combine(DurationMs, Outcome, Summary));
}
public sealed record WorkOrderRecord
{
    public WorkOrderRecord(string Id, string RunId, int Version, string Status, IReadOnlyDictionary<string, object?> Content)
    { this.Id = Id; this.RunId = RunId; this.Version = Version; this.Status = Status; this.Content = Content; }
    public string Id { get; } public string RunId { get; } public int Version { get; } public string Status { get; } public IReadOnlyDictionary<string, object?> Content { get; }
    public bool Equals(WorkOrderRecord? other) => other is not null && Id == other.Id && RunId == other.RunId && Version == other.Version && Status == other.Status && ApplicationValueEquality.DictionaryEqual(Content, other.Content);
    public override int GetHashCode() => HashCode.Combine(Id, RunId, Version, Status, ApplicationValueEquality.DictionaryHash(Content));
}

public interface IRunRepository
{
    string Create(string tenantId, long userId, string question);
    RunRecord? Get(string tenantId, string runId);
    void SetStatus(string tenantId, string runId, string status, int? totalTokens = null, bool? estimatedTokens = null, bool finished = false);
    void AddStep(string tenantId, string runId, RunStepRecord step);
    IReadOnlyList<RunStepRecord> Steps(string tenantId, string runId);
    IReadOnlyList<RunRecord> ListForUser(string tenantId, long userId);
    IReadOnlyList<RunRecord> ListForTenant(string tenantId, string? status = null);
}

public interface IWorkOrderRepository
{
    IReadOnlyDictionary<string, object?> CreateDraft(string tenantId, string runId, IReadOnlyDictionary<string, object?> content);
    WorkOrderRecord? Latest(string tenantId, string runId);
    void SetStatus(string tenantId, string workOrderId, string status);
}

public interface IAuditLog
{
    void Record(string tenantId, long? actorUserId, string action, string subjectType, string? subjectId, IReadOnlyDictionary<string, object?>? detail = null);
}

public interface ISafetyPrerequisiteRepository
{
    IReadOnlyList<SafetyStepRecord> RequiredForDocuments(string tenantId, IReadOnlyList<string> docIds);
    IReadOnlyList<SafetyStepRecord> RequiredByIds(string tenantId, IReadOnlyList<long> stepIds);
}

public interface IUserRepository { UserRecord? GetByUsername(string tenantId, string username); UserRecord? Get(string tenantId, long userId); }
public sealed record AskLogRecord
{
    public AskLogRecord(long? Id, string TenantId, long? UserId, string? CorrelationId, string Question, string AnswerText,
        bool Refused, string? Reason, ImmutableArray<IReadOnlyDictionary<string, object?>> Citations, int PromptTokens,
        int CompletionTokens, bool Estimated, string? CreatedAt = null)
    { this.Id = Id; this.TenantId = TenantId; this.UserId = UserId; this.CorrelationId = CorrelationId; this.Question = Question; this.AnswerText = AnswerText;
      this.Refused = Refused; this.Reason = Reason; this.Citations = Citations; this.PromptTokens = PromptTokens; this.CompletionTokens = CompletionTokens; this.Estimated = Estimated; this.CreatedAt = CreatedAt; }
    public long? Id { get; } public string TenantId { get; } public long? UserId { get; } public string? CorrelationId { get; } public string Question { get; } public string AnswerText { get; }
    public bool Refused { get; } public string? Reason { get; } public ImmutableArray<IReadOnlyDictionary<string, object?>> Citations { get; }
    public int PromptTokens { get; } public int CompletionTokens { get; } public bool Estimated { get; } public string? CreatedAt { get; }
    public bool Equals(AskLogRecord? other) => other is not null && Id == other.Id && TenantId == other.TenantId && UserId == other.UserId && CorrelationId == other.CorrelationId &&
        Question == other.Question && AnswerText == other.AnswerText && Refused == other.Refused && Reason == other.Reason &&
        ApplicationValueEquality.SequenceEqual(Citations, other.Citations) && PromptTokens == other.PromptTokens && CompletionTokens == other.CompletionTokens && Estimated == other.Estimated && CreatedAt == other.CreatedAt;
    public override int GetHashCode() => HashCode.Combine(Id, TenantId, UserId, CorrelationId, Question, AnswerText, Refused,
        HashCode.Combine(Reason, ApplicationValueEquality.SequenceHash(Citations), PromptTokens, CompletionTokens, Estimated, CreatedAt));
}
public interface IAskLogRepository { long Save(string tenantId, AskLogRecord record); IReadOnlyList<AskLogRecord> ListForUser(string tenantId, long userId); }

public interface IChunkSearch
{
    IReadOnlyList<DomainCopilot.Domain.Rag.Evidence> DenseSearch(string tenantId, IReadOnlyList<double> vector, string model, bool includeSuperseded, int limit);
    IReadOnlyList<DomainCopilot.Domain.Rag.Evidence> KeywordSearch(string tenantId, IReadOnlyList<string> terms, string model, bool includeSuperseded, int limit);
}
public interface IRetrievalSettings { double RetrievalMinSimilarity { get; } }
public interface IDocumentExtractor { ExtractedDocument Extract(string path); }

public interface IUserProvisioner
{
    bool CreateIfMissing(string tenantId, string username, string role, string password);
}

