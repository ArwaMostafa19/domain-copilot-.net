using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using DomainCopilot.Application.Retrieval;
using DomainCopilot.Application.Tools;
using DomainCopilot.Application.Workflow;
using DomainCopilot.Application.Text;
using DomainCopilot.Domain.Llm;
using DomainCopilot.Domain.Rag;
using DomainCopilot.Domain.Workflow;

namespace DomainCopilot.Application.Agents;

public sealed class EquipmentNotFoundError(string message) : ArgumentException(message);
public sealed class WorkflowInputError(string message) : ArgumentException(message);
public sealed class AgentMetrics { public int TotalCalls { get; set; } public int RepairedCalls { get; set; } public int DegradedCalls { get; set; } public double DegradationRate => TotalCalls == 0 ? 0 : (double)DegradedCalls / TotalCalls; }
public static class AgentMetricsGlobal { public static AgentMetrics GLOBAL_METRICS { get; } = new(); }

public sealed record SymptomMatcherResult(EquipmentMatch Match, ImmutableArray<Evidence> Evidence, TokenUsage Usage)
{
    public bool Equals(SymptomMatcherResult? other) => other is not null && Match == other.Match && ApplicationValueEquality.SequenceEqual(Evidence, other.Evidence) && Usage == other.Usage;
    public override int GetHashCode() => HashCode.Combine(Match, ApplicationValueEquality.SequenceHash(Evidence), Usage);
}
public sealed record SymptomMatcherInput(string Symptoms, string TenantId);
public sealed record DiagnosticSafetyPlannerInput(EquipmentMatch Match, string? InstalledRevision, string TenantId,
    ImmutableArray<Evidence> Evidence, string Symptoms = "")
{
    public bool Equals(DiagnosticSafetyPlannerInput? other) => other is not null && Match == other.Match && InstalledRevision == other.InstalledRevision && TenantId == other.TenantId && ApplicationValueEquality.SequenceEqual(Evidence, other.Evidence) && Symptoms == other.Symptoms;
    public override int GetHashCode() => HashCode.Combine(Match, InstalledRevision, TenantId, ApplicationValueEquality.SequenceHash(Evidence), Symptoms);
}
public sealed record DiagnosticSafetyPlannerResult(RevisionResolution Revision, ImmutableArray<SafetyStepRecord> SafetySteps)
{
    public bool Equals(DiagnosticSafetyPlannerResult? other) => other is not null && Revision == other.Revision && ApplicationValueEquality.SequenceEqual(SafetySteps, other.SafetySteps);
    public override int GetHashCode() => HashCode.Combine(Revision, ApplicationValueEquality.SequenceHash(SafetySteps));
}
public sealed record WorkOrderGeneratorInput(string Symptoms, EquipmentMatch Match, RevisionResolution Revision,
    ImmutableArray<Evidence> Evidence, ImmutableArray<SafetyStepRecord> SafetySteps)
{
    public bool Equals(WorkOrderGeneratorInput? other) => other is not null && Symptoms == other.Symptoms && Match == other.Match && Revision == other.Revision && ApplicationValueEquality.SequenceEqual(Evidence, other.Evidence) && ApplicationValueEquality.SequenceEqual(SafetySteps, other.SafetySteps);
    public override int GetHashCode() => HashCode.Combine(Symptoms, Match, Revision, ApplicationValueEquality.SequenceHash(Evidence), ApplicationValueEquality.SequenceHash(SafetySteps));
}
public sealed record WorkOrderGeneratorResult(IReadOnlyDictionary<string, object?> WorkOrder, TokenUsage Usage, bool Degraded)
{
    public bool Equals(WorkOrderGeneratorResult? other) => other is not null && ApplicationValueEquality.DictionaryEqual(WorkOrder, other.WorkOrder) && Usage == other.Usage && Degraded == other.Degraded;
    public override int GetHashCode() => HashCode.Combine(ApplicationValueEquality.DictionaryHash(WorkOrder), Usage, Degraded);
}

public abstract class AgentToolAccess
{
    public virtual Role Role => global::DomainCopilot.Domain.Workflow.Role.TECHNICIAN;
    public abstract IReadOnlySet<string> AllowedTools { get; }
    public object? InvokeTool(ToolRegistry registry, string name, IReadOnlyDictionary<string, object?> args) => registry.InvokeForAgent(GetType().Name, name, args, Role);
}

public sealed partial class SymptomMatcher(IEmbedder embedder, IChunkSearch search, ILlmProvider chain, IRetrievalSettings settings) : AgentToolAccess
{
    public ILlmProvider Chain { get; } = chain;
    public override IReadOnlySet<string> AllowedTools => AgentToolAllowlists.Values["SymptomMatcher"];
    public string StopCondition => "Stop when evidence identifies a match, or return no-evidence failure.";
    public Type InputSchema => typeof(SymptomMatcherInput);
    public Type OutputSchema => typeof(SymptomMatcherResult);
    [GeneratedRegex(@"\b(?:ALPHA|BETA)-[A-Z]{2,5}-\d+[A-Z]?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex EquipmentPattern();
    public SymptomMatcherResult Run(SymptomMatcherInput request)
    {
        if (PythonString.IsBlank(request.Symptoms)) throw new WorkflowInputError("Describe the equipment and maintenance symptoms to start a workflow.");
        var evidence = HybridRetrieval.Retrieve(request.Symptoms, request.TenantId, embedder, search, settings);
        if (evidence.Count == 0) throw new WorkflowInputError("No maintenance evidence matched this description. Name the equipment or describe its symptoms.");
        var requested = EquipmentPattern().Matches(request.Symptoms).Select(m => m.Value.ToUpperInvariant()).Distinct().ToArray();
        var stop = new HashSet<string>(["about","after","before","could","does","from","have","hello","here","into","just","like","need","please","show","start","that","them","there","these","they","this","those","what","when","where","which","while","with","would","workflow"], StringComparer.Ordinal);
        var terms = Regex.Matches(request.Symptoms.ToLowerInvariant(), @"[a-z0-9]+", RegexOptions.CultureInvariant).Select(m => m.Value).Where(t => t.Length >= 4 && !stop.Contains(t)).ToHashSet(StringComparer.Ordinal);
        var matchedTerms = evidence.SelectMany(e => Regex.Matches(string.Join(' ', e.Equipment ?? "", e.Title, e.Section, e.Text).ToLowerInvariant(), @"[a-z0-9]+", RegexOptions.CultureInvariant).Select(m => m.Value)).ToHashSet(StringComparer.Ordinal);
        if (requested.Length == 0 && (evidence.Max(x => x.DenseScore) < settings.RetrievalMinSimilarity || !terms.Overlaps(matchedTerms)))
            throw new WorkflowInputError("I could not find relevant maintenance evidence for this description. Name the equipment or describe its symptoms.");
        var available = evidence.Where(e => !string.IsNullOrEmpty(e.Equipment)).Select(e => e.Equipment!.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        var missing = requested.FirstOrDefault(code => !available.Contains(code));
        if (missing is not null) throw new EquipmentNotFoundError($"No maintenance information for {missing} is available in this tenant's documents.");
        var top = evidence[0];
        var match = new EquipmentMatch(!string.IsNullOrEmpty(top.Equipment) ? top.Equipment : (top.Title.Length > 0 ? top.Title : "Unidentified equipment"),
            evidence.Where(x => !string.IsNullOrEmpty(x.DocId)).Select(x => x.DocId).Distinct().ToImmutableArray(),
            top.Revision.Length > 0 ? top.Revision : "Rev A", top.DocStatus.Contains("superseded", StringComparison.OrdinalIgnoreCase) ? "superseded" : "current",
            evidence.Select(x => x.ChunkId).ToImmutableArray());
        return new(match, evidence.ToImmutableArray(), new TokenUsage());
    }
}

public sealed class DiagnosticSafetyPlanner : AgentToolAccess
{
    public override IReadOnlySet<string> AllowedTools => AgentToolAllowlists.Values["DiagnosticSafetyPlanner"];
    public string StopCondition => "Stop after resolving one revision and loading prerequisites for matched documents.";
    public Type InputSchema => typeof(DiagnosticSafetyPlannerInput);
    public Type OutputSchema => typeof(DiagnosticSafetyPlannerResult);
    public DiagnosticSafetyPlannerResult Run(DiagnosticSafetyPlannerInput request, ISafetyPrerequisiteRepository safetyRepo)
    {
        var revision = WorkflowRules.ResolveRevision(request.Match, request.InstalledRevision);
        var ids = request.Evidence.Where(x => !string.IsNullOrEmpty(x.DocId)).Select(x => x.DocId).Distinct().ToList();
        foreach (var id in request.Match.DocIds) if (!ids.Contains(id)) ids.Add(id);
        var steps = safetyRepo.RequiredForDocuments(request.TenantId, ids);
        if (steps.Count > 1 && request.Symptoms.Length > 0) steps = MostRelevantSafetySteps(request.Symptoms, request.Evidence, steps);
        return new(revision, steps.ToImmutableArray());
    }

    private static IReadOnlyList<SafetyStepRecord> MostRelevantSafetySteps(string symptoms, IReadOnlyList<Evidence> evidence, IReadOnlyList<SafetyStepRecord> safetySteps)
    {
        var ignored = new HashSet<string>(["about","after","before","could","does","from","have","here","into","just","like","need","please","show","start","that","them","there","these","they","this","those","what","when","where","which","while","with","would","workflow","safety","steps","maintenance","work","order","must","acknowledged","drafting","the","and","for","unit","has"], StringComparer.Ordinal);
        var terms = Regex.Matches(symptoms.ToLowerInvariant(), @"[a-z0-9]+", RegexOptions.CultureInvariant).Select(m => m.Value).Where(t => t.Length >= 4 && !ignored.Contains(t)).ToHashSet(StringComparer.Ordinal);
        if (terms.Count == 0) return safetySteps;
        var textByDoc = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var item in evidence) { if (!textByDoc.TryGetValue(item.DocId, out var list)) textByDoc[item.DocId] = list = []; list.Add(string.Join(' ', item.Equipment ?? "", item.Title, item.Section, item.Text)); }
        foreach (var step in safetySteps) { if (!textByDoc.TryGetValue(step.DocId ?? "", out var list)) textByDoc[step.DocId ?? ""] = list = []; list.Add(step.Text); }
        var docs = safetySteps.Select(s => s.DocId ?? "").Distinct().ToArray();
        var scores = docs.ToDictionary(id => id, id => terms.Intersect(Regex.Matches(string.Join(' ', textByDoc.GetValueOrDefault(id) ?? []).ToLowerInvariant(), @"[a-z0-9]+", RegexOptions.CultureInvariant).Select(m => m.Value)).Count());
        var best = scores.Values.DefaultIfEmpty(0).Max(); if (best == 0) return safetySteps;
        var selected = scores.Where(p => p.Value == best).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        return safetySteps.Where(s => selected.Contains(s.DocId ?? "")).ToArray();
    }
}

public sealed class WorkOrderGenerator(ILlmProvider chain) : AgentToolAccess
{
    public override IReadOnlySet<string> AllowedTools => AgentToolAllowlists.Values["WorkOrderGenerator"];
    public string StopCondition => "Stop after valid JSON, one repair attempt, or deterministic fallback.";
    public Type InputSchema => typeof(WorkOrderGeneratorInput);
    public Type OutputSchema => typeof(WorkOrderGeneratorResult);
    private static readonly Regex JsonBlock = new("```(?:json)?\\s*(\\{.*?\\})\\s*```", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    public WorkOrderGeneratorResult Generate(WorkOrderGeneratorInput request, string? correlationId = null,
        int maxModelCalls = 3, double retryBackoffSeconds = 0.05, double stepTimeoutSeconds = 45)
    {
        AgentMetricsGlobal.GLOBAL_METRICS.TotalCalls++;
        var prompt = $"Generate a maintenance work order in strict JSON format.\nSymptoms: {request.Symptoms}\nEquipment ID: {request.Match.EquipmentId}\nRevision: {request.Revision.Revision}\nEvidence snippets:\n" +
            string.Join('\n', request.Evidence.Take(5).Select(e => $"- [{e.ChunkId}] {e.Text}")) + "\nRespond strictly in valid JSON with fields: 'title', 'summary', 'steps', 'parts'.";
        var req = new CompletionRequest([new Message("system", "You are a maintenance assistant. Output valid JSON only."), new Message("user", prompt)], MaxTokens: 2048, CorrelationId: correlationId);
        var started = Stopwatch.GetTimestamp(); var calls = 0;
        var result = Complete(req, correlationId, maxModelCalls, retryBackoffSeconds, ref calls);
        var parsed = Parse(result.Content);
        if (Valid(parsed)) { CheckTimeout(started, stepTimeoutSeconds); return new(Build(parsed!, request), result.Usage, false); }

        AgentMetricsGlobal.GLOBAL_METRICS.RepairedCalls++;
        var repair = new CompletionRequest([new Message("system", "Fix the invalid JSON text and output ONLY valid JSON."), new Message("user", $"Fix this invalid JSON:\n{result.Content}")], MaxTokens: 2048, CorrelationId: correlationId);
        var repaired = Complete(repair, correlationId, maxModelCalls, retryBackoffSeconds, ref calls);
        var usage = new TokenUsage(result.Usage.PromptTokens + repaired.Usage.PromptTokens, result.Usage.CompletionTokens + repaired.Usage.CompletionTokens, result.Usage.Estimated || repaired.Usage.Estimated);
        parsed = Parse(repaired.Content);
        if (Valid(parsed)) { CheckTimeout(started, stepTimeoutSeconds); return new(Build(parsed!, request), usage, false); }
        AgentMetricsGlobal.GLOBAL_METRICS.DegradedCalls++;
        var fallback = Fallback(request);
        CheckTimeout(started, stepTimeoutSeconds);
        return new(fallback, usage, true);
    }

    private CompletionResult Complete(CompletionRequest request, string? cid, int maxCalls, double backoff, ref int calls)
    {
        ProviderError? last = null;
        var attempts = maxCalls - calls;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try { calls++; return chain.Complete(request); }
            catch (ProviderError error) { last = error; if (!error.FallbackAllowed || calls >= maxCalls) throw; Thread.Sleep(TimeSpan.FromSeconds(backoff * Math.Pow(2, attempt))); }
        }
        throw last is null ? new InvalidOperationException("model call failed") : last;
    }

    private static Dictionary<string, object?>? Parse(string raw)
    {
        var text = raw.Trim(); var match = JsonBlock.Match(text);
        if (match.Success) text = match.Groups[1].Value.Trim();
        else if (text.StartsWith("```", StringComparison.Ordinal))
        { var lines = text.Split('\n'); if (lines.Length >= 2 && lines[^1].StartsWith("```", StringComparison.Ordinal)) text = string.Join('\n', lines.Skip(1).Take(lines.Length - 2)).Trim(); }
        if (TryParse(text, out var data)) return data;
        var first = raw.IndexOf('{'); var last = raw.LastIndexOf('}');
        return first >= 0 && last > first && TryParse(raw[first..(last + 1)], out data) ? data : null;
    }
    private static bool TryParse(string text, out Dictionary<string, object?>? result)
    {
        try { using var doc = JsonDocument.Parse(NormalizePythonConstants(text)); result = doc.RootElement.ValueKind == JsonValueKind.Object ? ReadObject(doc.RootElement) : null; return result is not null; }
        catch (JsonException) { result = null; return false; }
    }
    private static object? ConvertJson(JsonElement e) => e.ValueKind switch
    { JsonValueKind.String => e.GetString(), JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Null => null,
      JsonValueKind.Number when e.TryGetInt64(out var l) => l, JsonValueKind.Number => e.GetDouble(),
      JsonValueKind.Array => e.EnumerateArray().Select(ConvertJson).ToList(), JsonValueKind.Object => ReadObject(e), _ => null };
    private static Dictionary<string, object?> ReadObject(JsonElement element)
    { var result = new Dictionary<string, object?>(StringComparer.Ordinal); foreach (var property in element.EnumerateObject()) result[property.Name] = ConvertJson(property.Value); return result; }
    private static string NormalizePythonConstants(string text)
    {
        var output = new StringBuilder(text.Length); var inString = false; var escaped = false;
        for (var i = 0; i < text.Length;)
        {
            var current = text[i];
            if (inString)
            {
                output.Append(current); i++;
                if (escaped) escaped = false; else if (current == '\\') escaped = true; else if (current == '"') inString = false;
                continue;
            }
            if (current == '"') { inString = true; output.Append(current); i++; continue; }
            var token = text.AsSpan(i).StartsWith("-Infinity", StringComparison.Ordinal) ? "-Infinity" :
                text.AsSpan(i).StartsWith("Infinity", StringComparison.Ordinal) ? "Infinity" :
                text.AsSpan(i).StartsWith("NaN", StringComparison.Ordinal) ? "NaN" : null;
            if (token is not null && (i == 0 || IsJsonBoundary(text[i - 1])) && (i + token.Length == text.Length || IsJsonBoundary(text[i + token.Length])))
            { output.Append("null"); i += token.Length; continue; }
            output.Append(current); i++;
        }
        return output.ToString();
    }
    private static bool IsJsonBoundary(char c) => char.IsWhiteSpace(c) || c is ',' or ':' or '[' or ']' or '{' or '}';
    private static bool Valid(Dictionary<string, object?>? d)
    {
        if (d is null || d.GetValueOrDefault("title") is not string title || d.GetValueOrDefault("summary") is not string summary) return false;
        if (title.EnumerateRunes().Count() > 200 || summary.EnumerateRunes().Count() > 2000) return false;
        var steps = d.GetValueOrDefault("steps"); var parts = d.GetValueOrDefault("parts") ?? new List<object?>();
        return steps is List<object?> sl && sl.Count is >= 1 and <= 30 && sl.All(x => x is string s && s.EnumerateRunes().Count() is > 0 and <= 1000) &&
               parts is List<object?> pl && pl.Count <= 50 && pl.All(x => x is string p && p.EnumerateRunes().Count() <= 200);
    }
    private static IReadOnlyDictionary<string, object?> Build(Dictionary<string, object?> data, WorkOrderGeneratorInput request)
    {
        IEnumerable<string> parts = data.GetValueOrDefault("parts") is List<object?> proposed ? proposed.Cast<string>() : Array.Empty<string>();
        var kept = WorkflowRules.PartsFromEvidence(parts, request.Evidence);
        var steps = ((List<object?>)data["steps"]!).Cast<string>().ToArray();
        return new Dictionary<string, object?> { ["title"] = data["title"]!, ["summary"] = data["summary"]!, ["steps"] = steps,
            ["parts"] = kept, ["safety_prerequisites"] = request.SafetySteps.Select(s => new Dictionary<string, object?> { ["id"] = s.Id, ["step_no"] = s.StepNo, ["text"] = s.Text }).ToArray(), ["degraded"] = false };
    }
    private static IReadOnlyDictionary<string, object?> Fallback(WorkOrderGeneratorInput request)
    {
        var snippet = PythonString.Slice(string.Join(' ', request.Evidence.Take(2).Select(e => e.Text)), 200);
        return new Dictionary<string, object?>
        {
            ["title"] = $"Degraded Work Order for {request.Match.EquipmentId}",
            ["summary"] = $"Plain RAG Fallback: {snippet}",
            ["steps"] = new[] { $"Inspect equipment {request.Match.EquipmentId} per revision {request.Revision.Revision}" }, ["parts"] = Array.Empty<string>(),
            ["safety_prerequisites"] = request.SafetySteps.Select(s => new Dictionary<string, object?> { ["id"] = s.Id, ["step_no"] = s.StepNo, ["text"] = s.Text }).ToArray(), ["degraded"] = true
        };
    }
    private static void CheckTimeout(long started, double seconds)
    { if ((double)(Stopwatch.GetTimestamp() - started) / Stopwatch.Frequency > seconds) throw new TimeoutException("work-order generation step timed out"); }
}
