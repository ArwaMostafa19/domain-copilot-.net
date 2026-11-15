using System.Collections.Immutable;
using System.Diagnostics;
using DomainCopilot.Application.Agents;
using DomainCopilot.Application.Tools;
using DomainCopilot.Domain.Llm;
using DomainCopilot.Domain.Workflow;

namespace DomainCopilot.Application.Workflow;

public sealed class GateNotSatisfiedError(string message) : WorkflowError(message);
public sealed class WorkflowLimitError(string message) : WorkflowError(message);
public sealed class RunCancelledError(string message) : WorkflowError(message);
public sealed record WorkflowStartInput(string Symptoms, string? InstalledRevision, long UserId, string TenantId);
public sealed record WorkflowStartOutput
{
    public WorkflowStartOutput(string RunId, IReadOnlyList<long> RequiredSafetyStepIds)
    { this.RunId = RunId; this.RequiredSafetyStepIds = RequiredSafetyStepIds; }
    public string RunId { get; }
    public IReadOnlyList<long> RequiredSafetyStepIds { get; }
    public bool Equals(WorkflowStartOutput? other) => other is not null && RunId == other.RunId && ApplicationValueEquality.SequenceEqual(RequiredSafetyStepIds, other.RequiredSafetyStepIds);
    public override int GetHashCode() => HashCode.Combine(RunId, ApplicationValueEquality.SequenceHash(RequiredSafetyStepIds));
}

public sealed class WorkflowOrchestrator : AgentToolAccess
{
    private readonly IRunRepository _runs; private readonly IWorkOrderRepository _orders; private readonly IAuditLog _audit;
    private readonly ISafetyPrerequisiteRepository _safety; private readonly SymptomMatcher _matcher;
    private readonly DiagnosticSafetyPlanner _planner = new(); private readonly WorkOrderGenerator _generator;
    private readonly int _maxCalls, _tokenBudget; private readonly double _timeout, _stepTimeout;
    public override Role Role => RoleValue;
    private static Role RoleValue => global::DomainCopilot.Domain.Workflow.Role.TECHNICIAN;
    public override IReadOnlySet<string> AllowedTools => AgentToolAllowlists.Values["WorkflowOrchestrator"];
    public string StopCondition => "Stop after the run is awaiting acknowledgement or a phase raises a domain error.";
    public Type InputSchema => typeof(WorkflowStartInput);
    public Type OutputSchema => typeof(WorkflowStartOutput);

    public WorkflowOrchestrator(IRunRepository runRepo, IWorkOrderRepository workOrderRepo, IAuditLog auditLog,
        ISafetyPrerequisiteRepository safetyRepo, IEmbedder embedder, IChunkSearch search, ILlmProvider chain,
        IRetrievalSettings settings, int maxModelCalls = 6, int tokenBudget = 20000, double timeoutSeconds = 120, double stepTimeoutSeconds = 45)
    {
        _runs = runRepo; _orders = workOrderRepo; _audit = auditLog; _safety = safetyRepo;
        _matcher = new(embedder, search, chain, settings); _generator = new(chain);
        _maxCalls = maxModelCalls; _tokenBudget = tokenBudget; _timeout = timeoutSeconds; _stepTimeout = stepTimeoutSeconds;
    }

    public WorkflowStartOutput StartRun(WorkflowStartInput request, string? correlationId = null,
        Action<IReadOnlyDictionary<string, object?>>? emit = null)
    {
        var cid = correlationId ?? "orchestrator-start"; var started = Stopwatch.GetTimestamp();
        var runId = _runs.Create(request.TenantId, request.UserId, request.Symptoms);
        emit?.Invoke(Event("status", ("run_id", runId), ("status", "running")));
        CheckRun(request.TenantId, runId, started);
        var stepStarted = Stopwatch.GetTimestamp();
        var match = _matcher.Run(new(request.Symptoms, request.TenantId));
        CheckStep(stepStarted, "symptom matching step timed out", request.TenantId, runId);
        var first = new RunStepRecord(1, "SymptomMatcher", ["search_manuals"], match.Evidence.Select(e => e.ChunkId).ToImmutableArray(),
            match.Usage.PromptTokens, match.Usage.CompletionTokens, match.Usage.Estimated, ElapsedMs(stepStarted), "matched",
            $"Matched equipment {match.Match.EquipmentId} revision {match.Match.Revision}");
        _runs.AddStep(request.TenantId, runId, first);
        emit?.Invoke(Event("step", ("run_id", runId), ("ordinal", 1), ("agent", "SymptomMatcher"), ("summary", first.Summary)));

        CheckRun(request.TenantId, runId, started); stepStarted = Stopwatch.GetTimestamp();
        var plan = _planner.Run(new(match.Match, request.InstalledRevision, request.TenantId, match.Evidence, request.Symptoms), _safety);
        CheckStep(stepStarted, "safety planning step timed out", request.TenantId, runId);
        var second = new RunStepRecord(2, "DiagnosticSafetyPlanner", ["get_safety_prerequisites"], PromptTokens: 0, CompletionTokens: 0,
            Estimated: false, DurationMs: ElapsedMs(stepStarted), Outcome: "planned",
            Summary: $"Selected revision {plan.Revision.Revision} with {plan.SafetySteps.Length} safety prerequisites.");
        _runs.AddStep(request.TenantId, runId, second);
        emit?.Invoke(Event("step", ("run_id", runId), ("ordinal", 2), ("agent", "DiagnosticSafetyPlanner"), ("summary", second.Summary)));
        _runs.SetStatus(request.TenantId, runId, "awaiting_acknowledgement");
        emit?.Invoke(Event("status", ("run_id", runId), ("status", "awaiting_acknowledgement")));
        _audit.Record(request.TenantId, request.UserId, "start_run", "run", runId,
            new Dictionary<string, object?> { ["equipment_id"] = match.Match.EquipmentId, ["correlation_id"] = cid });
        return new WorkflowStartOutput(runId, plan.SafetySteps.Select(s => s.Id).ToArray());
    }

    public bool Acknowledge(string runId, IReadOnlyList<long> acknowledgedStepIds, long userId, string tenantId, string? correlationId = null)
    {
        var cid = correlationId ?? "orchestrator-ack";
        if (_runs.Get(tenantId, runId) is null) throw new WorkflowError($"run {runId} not found");
        _audit.Record(tenantId, userId, "acknowledge_safety", "run", runId,
            new Dictionary<string, object?> { ["acknowledged_step_ids"] = acknowledgedStepIds, ["correlation_id"] = cid });
        return true;
    }

    public IReadOnlyDictionary<string, object?> Generate(string runId, long userId, string tenantId,
        IReadOnlyList<long>? acknowledgedStepIds = null, string? correlationId = null,
        Action<IReadOnlyDictionary<string, object?>>? emit = null)
    {
        var cid = correlationId ?? "orchestrator-gen"; var started = Stopwatch.GetTimestamp();
        var run = _runs.Get(tenantId, runId) ?? throw new WorkflowError($"run {runId} not found");
        CheckRun(tenantId, runId, started);
        var symptoms = run.Question;
        var match = _matcher.Run(new(symptoms, tenantId));
        var plan = _planner.Run(new(match.Match, null, tenantId, match.Evidence, symptoms), _safety);
        var required = plan.SafetySteps.Select(s => s.Id).ToArray();
        var gate = WorkflowRules.CheckSafetyGate(required, acknowledgedStepIds ?? Array.Empty<long>());
        if (!gate.Ok) throw new GateNotSatisfiedError($"missing safety step acknowledgements: [{string.Join(", ", gate.Missing)}]");
        CheckRun(tenantId, runId, started);
        var stepStarted = Stopwatch.GetTimestamp(); WorkOrderGeneratorResult generated;
        try { generated = _generator.Generate(new(symptoms, match.Match, plan.Revision, match.Evidence, plan.SafetySteps), cid, _maxCalls, stepTimeoutSeconds: _stepTimeout); }
        catch { _runs.SetStatus(tenantId, runId, "failed", finished: true); throw; }
        if (ElapsedSeconds(stepStarted) > _stepTimeout) { _runs.SetStatus(tenantId, runId, "failed", finished: true); throw new WorkflowLimitError("work-order generation step timed out"); }
        CheckRun(tenantId, runId, started);
        var workOrder = new Dictionary<string, object?>(generated.WorkOrder, StringComparer.Ordinal);
        _orders.CreateDraft(tenantId, runId, workOrder);
        var third = new RunStepRecord(3, "WorkOrderGenerator", ["create_work_order_draft"], PromptTokens: generated.Usage.PromptTokens,
            CompletionTokens: generated.Usage.CompletionTokens, Estimated: generated.Usage.Estimated, DurationMs: ElapsedMs(stepStarted),
            Outcome: generated.Degraded ? "degraded" : "generated", Summary: $"Generated draft work order v1 ({(generated.Degraded ? "degraded RAG" : "JSON")})");
        _runs.AddStep(tenantId, runId, third);
        var allSteps = _runs.Steps(tenantId, runId); var total = allSteps.Sum(s => s.PromptTokens + s.CompletionTokens); var estimated = allSteps.Any(s => s.Estimated);
        if (total > _tokenBudget) { _runs.SetStatus(tenantId, runId, "failed", total, estimated, finished: true); throw new WorkflowLimitError("workflow token budget exceeded"); }
        _runs.SetStatus(tenantId, runId, "pending_approval", total, estimated);
        emit?.Invoke(Event("work_order", ("run_id", runId), ("content", workOrder)));
        emit?.Invoke(Event("status", ("run_id", runId), ("status", "pending_approval")));
        _audit.Record(tenantId, userId, "generate_work_order", "run", runId, new Dictionary<string, object?> { ["degraded"] = generated.Degraded, ["correlation_id"] = cid });
        return workOrder;
    }

    public IReadOnlyDictionary<string, object?> Approval(string runId, string decision, Role role, long userId, string tenantId,
        string comment = "", IReadOnlyDictionary<string, object?>? editedContent = null, string? correlationId = null)
    {
        var cid = correlationId ?? "orchestrator-approval";
        if (role != Role.SUPERVISOR) throw new WorkflowError("role technician may not 'approve'");
        var run = _runs.Get(tenantId, runId) ?? throw new WorkflowError($"run {runId} not found");
        var target = WorkflowRules.NextState(RunStatus.Parse(run.Status), decision, role);
        var latest = _orders.Latest(tenantId, runId) ?? throw new WorkflowError("no work order found for run");
        var prior = new Dictionary<string, object?>(latest.Content, StringComparer.Ordinal);
        if (editedContent is not null && editedContent.Count > 0) { _orders.CreateDraft(tenantId, runId, editedContent); latest = _orders.Latest(tenantId, runId); }
        var status = decision == "approve" ? "approved" : decision == "reject" ? "rejected" : "draft";
        if (latest is not null) _orders.SetStatus(tenantId, latest.Id, status);
        _runs.SetStatus(tenantId, runId, target.Value, finished: true);
        _audit.Record(tenantId, userId, $"approval_{decision}", "run", runId, new Dictionary<string, object?>
        { ["decision"] = decision, ["comment"] = comment, ["correlation_id"] = cid, ["before"] = prior, ["after"] = latest is null ? null : latest.Content });
        return new Dictionary<string, object?> { ["run_id"] = runId, ["status"] = target.Value, ["decision"] = decision };
    }

    private void CheckRun(string tenantId, string runId, long started)
    {
        var run = _runs.Get(tenantId, runId) ?? throw new WorkflowError("run not found");
        if (run.Status == "cancelled") throw new RunCancelledError("run is cancelled");
        if (ElapsedSeconds(started) > _timeout) { _runs.SetStatus(tenantId, runId, "failed", finished: true); throw new WorkflowLimitError("workflow time budget exceeded"); }
        var steps = _runs.Steps(tenantId, runId);
        if (steps.Count(s => s.Agent == "WorkOrderGenerator") >= _maxCalls) { _runs.SetStatus(tenantId, runId, "failed", finished: true); throw new WorkflowLimitError("workflow model-call budget exceeded"); }
        if (steps.Sum(s => s.PromptTokens + s.CompletionTokens) >= _tokenBudget) { _runs.SetStatus(tenantId, runId, "failed", finished: true); throw new WorkflowLimitError("workflow token budget exceeded"); }
    }
    private void CheckStep(long started, string message, string tenantId, string runId)
    { if (ElapsedSeconds(started) > _stepTimeout) { _runs.SetStatus(tenantId, runId, "failed", finished: true); throw new WorkflowLimitError(message); } }
    private static int ElapsedMs(long started) => (int)(ElapsedSeconds(started) * 1000);
    private static double ElapsedSeconds(long started) => (double)(Stopwatch.GetTimestamp() - started) / Stopwatch.Frequency;
    private static IReadOnlyDictionary<string, object?> Event(string name, params (string Key, object? Value)[] fields)
    { var result = new Dictionary<string, object?> { ["event"] = name }; foreach (var field in fields) result[field.Key] = field.Value; return result; }
}
