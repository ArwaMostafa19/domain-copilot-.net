using System.Collections.Immutable;
using DomainCopilot.Domain.Rag;
using DomainCopilot.Domain.Workflow;
using DomainCopilot.Application.Text;

namespace DomainCopilot.Application.Workflow;

public static class WorkflowRules
{
    public static RevisionResolution ResolveRevision(EquipmentMatch match, string? installedRevision)
    {
        if (!string.IsNullOrEmpty(installedRevision))
        {
            var superseded = installedRevision != match.Revision;
            var note = superseded
                ? $"using the technician's installed revision {installedRevision} instead of the current {match.Revision}; this revision is superseded"
                : $"using the technician's installed revision {installedRevision}";
            return new(installedRevision, superseded, note);
        }
        var old = match.RevisionStatus == "superseded";
        return new(match.Revision, old, old ? $"using the matched revision {match.Revision}, which is superseded" : $"using the current revision {match.Revision}");
    }

    public static GateResult CheckSafetyGate(IEnumerable<long> requiredStepIds, IEnumerable<long> acknowledgedStepIds)
    {
        var acknowledged = acknowledgedStepIds.ToHashSet();
        var missing = requiredStepIds.Distinct().Where(id => !acknowledged.Contains(id)).Order().ToImmutableArray();
        return new GateResult(missing.IsEmpty, missing);
    }

    public static RunStatus NextState(RunStatus? current, string @event, Role role, bool gateSatisfied = true)
    {
        if (WorkflowState.SUPERVISOR_EVENTS.Contains(@event) && role != Role.SUPERVISOR)
            throw new WorkflowError($"role {role.Value} may not '{@event}'");
        if (@event == "approve" && !gateSatisfied) throw new WorkflowError("the safety gate must be satisfied again before approval");
        if (WorkflowState.TRANSITIONS.TryGetValue((current, @event), out var target)) return target;
        throw new WorkflowError($"cannot '{@event}' from {current?.Value ?? "no run"}");
    }

    public static IReadOnlyList<string> PartsFromEvidence(IEnumerable<string> proposed, IEnumerable<Evidence> evidence)
    {
        var joined = string.Join(' ', evidence.Select(item => item.Text)).ToLowerInvariant();
        var kept = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in proposed)
        {
            if (PythonString.IsBlank(part)) continue;
            var normalized = PythonString.Strip(part);
            var key = normalized.ToLowerInvariant();
            if (seen.Add(key) && joined.Contains(key, StringComparison.Ordinal)) kept.Add(normalized);
        }
        return kept;
    }
}
