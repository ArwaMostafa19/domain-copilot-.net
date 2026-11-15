using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DomainCopilot.Domain;

namespace DomainCopilot.Domain.Workflow;

public class WorkflowError : Exception
{
    public WorkflowError() : base(string.Empty) { }
    public WorkflowError(string? message) : base(message ?? string.Empty) { }
}

[JsonConverter(typeof(RoleJsonConverter))]
public sealed class Role : IEquatable<Role>
{
    private Role(string Value) => this.Value = Value;
    public string Value { get; }
    public static Role TECHNICIAN { get; } = new("technician");
    public static Role SUPERVISOR { get; } = new("supervisor");
    public static Role Parse(string value) => value switch
    {
        "technician" => TECHNICIAN,
        "supervisor" => SUPERVISOR,
        _ => throw new ArgumentException($"{PythonWhitespace.Repr(value)} is not a valid Role")
    };
    public bool Equals(Role? other) => other is not null && Value == other.Value;
    public override bool Equals(object? obj) => obj is Role other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
    public override string ToString() => Value;
    public static bool operator ==(Role? left, Role? right) => Equals(left, right);
    public static bool operator !=(Role? left, Role? right) => !Equals(left, right);
    public static bool operator ==(Role? left, string? right) => left is not null && left.Value == right;
    public static bool operator !=(Role? left, string? right) => !(left == right);
    public static bool operator ==(string? left, Role? right) => right == left;
    public static bool operator !=(string? left, Role? right) => !(right == left);
}

[JsonConverter(typeof(RunStatusJsonConverter))]
public sealed class RunStatus : IEquatable<RunStatus>
{
    private RunStatus(string Value) => this.Value = Value;
    public string Value { get; }
    public static RunStatus RUNNING { get; } = new("running");
    public static RunStatus AWAITING_ACKNOWLEDGEMENT { get; } = new("awaiting_acknowledgement");
    public static RunStatus PENDING_APPROVAL { get; } = new("pending_approval");
    public static RunStatus APPROVED { get; } = new("approved");
    public static RunStatus REJECTED { get; } = new("rejected");
    public static RunStatus CANCELLED { get; } = new("cancelled");
    public static RunStatus FAILED { get; } = new("failed");
    public static RunStatus COMPLETED { get; } = new("completed");

    public static RunStatus Parse(string value) => value switch
    {
        "running" => RUNNING,
        "awaiting_acknowledgement" => AWAITING_ACKNOWLEDGEMENT,
        "pending_approval" => PENDING_APPROVAL,
        "approved" => APPROVED,
        "rejected" => REJECTED,
        "cancelled" => CANCELLED,
        "failed" => FAILED,
        "completed" => COMPLETED,
        _ => throw new ArgumentException($"{PythonWhitespace.Repr(value)} is not a valid RunStatus")
    };

    public bool Equals(RunStatus? other) => other is not null && Value == other.Value;
    public override bool Equals(object? obj) => obj is RunStatus other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
    public override string ToString() => Value;
    public static bool operator ==(RunStatus? left, RunStatus? right) => Equals(left, right);
    public static bool operator !=(RunStatus? left, RunStatus? right) => !Equals(left, right);
    public static bool operator ==(RunStatus? left, string? right) => left is not null && left.Value == right;
    public static bool operator !=(RunStatus? left, string? right) => !(left == right);
    public static bool operator ==(string? left, RunStatus? right) => right == left;
    public static bool operator !=(string? left, RunStatus? right) => !(right == left);
}

public sealed class RoleJsonConverter : JsonConverter<Role>
{
    public override Role Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ReadRole(reader);
    public override void Write(Utf8JsonWriter writer, Role value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);

    private static Role ReadRole(Utf8JsonReader reader)
    {
        try { return Role.Parse(reader.GetString()!); }
        catch (ArgumentException error) { throw new JsonException(error.Message, error); }
        catch (InvalidOperationException error) { throw new JsonException("Role value must be a string.", error); }
    }
}

public sealed class RunStatusJsonConverter : JsonConverter<RunStatus>
{
    public override RunStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ReadStatus(reader);
    public override void Write(Utf8JsonWriter writer, RunStatus value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);

    private static RunStatus ReadStatus(Utf8JsonReader reader)
    {
        try { return RunStatus.Parse(reader.GetString()!); }
        catch (ArgumentException error) { throw new JsonException(error.Message, error); }
        catch (InvalidOperationException error) { throw new JsonException("RunStatus value must be a string.", error); }
    }
}

public static class WorkflowState
{
    public static readonly FrozenSet<RunStatus> TERMINAL_RUN_STATUSES = new[]
    {
        RunStatus.APPROVED, RunStatus.REJECTED, RunStatus.CANCELLED, RunStatus.FAILED, RunStatus.COMPLETED
    }.ToFrozenSet();

    public static readonly FrozenSet<string> SUPERVISOR_EVENTS = new[] { "approve", "reject", "edit" }
        .ToFrozenSet(StringComparer.Ordinal);

    public static readonly Dictionary<(RunStatus? Current, string Event), RunStatus> TRANSITIONS =
        new()
        {
            [(null, "create_draft")] = RunStatus.RUNNING,
            [(RunStatus.RUNNING, "await_acknowledgement")] = RunStatus.AWAITING_ACKNOWLEDGEMENT,
            [(RunStatus.AWAITING_ACKNOWLEDGEMENT, "acknowledge")] = RunStatus.RUNNING,
            [(RunStatus.RUNNING, "draft_ready")] = RunStatus.PENDING_APPROVAL,
            [(RunStatus.PENDING_APPROVAL, "approve")] = RunStatus.APPROVED,
            [(RunStatus.PENDING_APPROVAL, "reject")] = RunStatus.REJECTED,
            [(RunStatus.PENDING_APPROVAL, "edit")] = RunStatus.PENDING_APPROVAL,
            [(RunStatus.APPROVED, "complete")] = RunStatus.COMPLETED,
            [(RunStatus.RUNNING, "cancel")] = RunStatus.CANCELLED,
            [(RunStatus.AWAITING_ACKNOWLEDGEMENT, "cancel")] = RunStatus.CANCELLED,
            [(RunStatus.PENDING_APPROVAL, "cancel")] = RunStatus.CANCELLED,
            [(RunStatus.RUNNING, "fail")] = RunStatus.FAILED,
            [(RunStatus.AWAITING_ACKNOWLEDGEMENT, "fail")] = RunStatus.FAILED,
            [(RunStatus.PENDING_APPROVAL, "fail")] = RunStatus.FAILED
        };
}

public sealed record SymptomReport
{
    public SymptomReport(string Symptoms, string? InstalledRevision = null)
    {
        if (PythonWhitespace.IsBlank(Symptoms)) throw new WorkflowError("symptoms must not be empty");
        this.Symptoms = Symptoms;
        this.InstalledRevision = InstalledRevision;
    }
    public string Symptoms { get; }
    public string? InstalledRevision { get; }
}

public sealed record EquipmentMatch
{
    public EquipmentMatch(
        string EquipmentId,
        ImmutableArray<string> DocIds,
        string Revision,
        string RevisionStatus,
        ImmutableArray<long> EvidenceIds)
    {
        DocIds = DomainValueEquality.Normalize(DocIds);
        EvidenceIds = DomainValueEquality.Normalize(EvidenceIds);
        if (string.IsNullOrEmpty(EquipmentId)) throw new WorkflowError("equipment_id must not be empty");
        if (DocIds.IsEmpty) throw new WorkflowError("equipment match needs at least one document");
        if (string.IsNullOrEmpty(Revision)) throw new WorkflowError("equipment match needs a revision");
        if (RevisionStatus is not ("current" or "superseded"))
            throw new WorkflowError($"revision_status must be 'current' or 'superseded', got {PythonWhitespace.Repr(RevisionStatus)}");
        if (EvidenceIds.IsEmpty) throw new WorkflowError("equipment match needs at least one evidence id");
        this.EquipmentId = EquipmentId;
        this.DocIds = DocIds;
        this.Revision = Revision;
        this.RevisionStatus = RevisionStatus;
        this.EvidenceIds = EvidenceIds;
    }
    public string EquipmentId { get; }
    public ImmutableArray<string> DocIds { get; }
    public string Revision { get; }
    public string RevisionStatus { get; }
    public ImmutableArray<long> EvidenceIds { get; }
    public bool Equals(EquipmentMatch? other) => other is not null && EquipmentId == other.EquipmentId &&
        DomainValueEquality.SequenceEqual(DocIds, other.DocIds) && Revision == other.Revision &&
        RevisionStatus == other.RevisionStatus && DomainValueEquality.SequenceEqual(EvidenceIds, other.EvidenceIds);
    public override int GetHashCode() => HashCode.Combine(EquipmentId, DomainValueEquality.SequenceHash(DocIds),
        Revision, RevisionStatus, DomainValueEquality.SequenceHash(EvidenceIds));
}

public sealed record ClarificationRequest
{
    public ClarificationRequest(string Question)
    {
        if (PythonWhitespace.IsBlank(Question)) throw new WorkflowError("a clarification request needs a question");
        this.Question = Question;
    }
    public string Question { get; }
}

public sealed record DiagnosticStep
{
    public DiagnosticStep(string Text, ImmutableArray<long> ChunkIds)
    {
        ChunkIds = DomainValueEquality.Normalize(ChunkIds);
        if (PythonWhitespace.IsBlank(Text)) throw new WorkflowError("a diagnostic step needs text");
        if (ChunkIds.IsEmpty) throw new WorkflowError("every diagnostic step must cite at least one chunk");
        this.Text = Text;
        this.ChunkIds = ChunkIds;
    }
    public string Text { get; }
    public ImmutableArray<long> ChunkIds { get; }
    public bool Equals(DiagnosticStep? other) => other is not null && Text == other.Text && DomainValueEquality.SequenceEqual(ChunkIds, other.ChunkIds);
    public override int GetHashCode() => HashCode.Combine(Text, DomainValueEquality.SequenceHash(ChunkIds));
}

public sealed record SafetyStepRecord
{
    public SafetyStepRecord(long Id, int StepNo, string Text, string? DocId = null)
    {
        if (PythonWhitespace.IsBlank(Text)) throw new WorkflowError("a safety step needs text");
        this.Id = Id;
        this.StepNo = StepNo;
        this.Text = Text;
        this.DocId = DocId;
    }
    public long Id { get; }
    public int StepNo { get; }
    public string Text { get; }
    public string? DocId { get; }
}

public sealed record DiagnosticPlan
{
    public DiagnosticPlan(string Revision, ImmutableArray<DiagnosticStep> Steps, ImmutableArray<SafetyStepRecord> RequiredSafety)
    {
        Steps = DomainValueEquality.Normalize(Steps);
        RequiredSafety = DomainValueEquality.Normalize(RequiredSafety);
        if (string.IsNullOrEmpty(Revision)) throw new WorkflowError("a diagnostic plan needs a revision");
        if (Steps.IsEmpty) throw new WorkflowError("a diagnostic plan needs at least one step");
        this.Revision = Revision;
        this.Steps = Steps;
        this.RequiredSafety = RequiredSafety;
    }
    public string Revision { get; }
    public ImmutableArray<DiagnosticStep> Steps { get; }
    public ImmutableArray<SafetyStepRecord> RequiredSafety { get; }
    public bool Equals(DiagnosticPlan? other) => other is not null && Revision == other.Revision &&
        DomainValueEquality.SequenceEqual(Steps, other.Steps) && DomainValueEquality.SequenceEqual(RequiredSafety, other.RequiredSafety);
    public override int GetHashCode() => HashCode.Combine(Revision, DomainValueEquality.SequenceHash(Steps), DomainValueEquality.SequenceHash(RequiredSafety));
}

public sealed record WorkOrderDraft
{
    public WorkOrderDraft(string Title, string EquipmentId, string Revision, string Symptoms,
        ImmutableArray<DiagnosticStep> DiagnosticSteps, ImmutableArray<long> SafetyStepIds,
        ImmutableArray<string> Parts, ImmutableArray<long> Citations)
    {
        DiagnosticSteps = DomainValueEquality.Normalize(DiagnosticSteps);
        SafetyStepIds = DomainValueEquality.Normalize(SafetyStepIds);
        Parts = DomainValueEquality.Normalize(Parts);
        Citations = DomainValueEquality.Normalize(Citations);
        if (PythonWhitespace.StripIsEmpty(Title)) throw new WorkflowError("a work order needs a title");
        if (string.IsNullOrEmpty(EquipmentId)) throw new WorkflowError("a work order needs an equipment_id");
        if (string.IsNullOrEmpty(Revision)) throw new WorkflowError("a work order needs a revision");
        if (PythonWhitespace.StripIsEmpty(Symptoms)) throw new WorkflowError("a work order needs the reported symptoms");
        if (DiagnosticSteps.IsEmpty) throw new WorkflowError("a work order needs at least one diagnostic step");
        if (Citations.IsEmpty) throw new WorkflowError("a work order needs at least one citation");
        this.Title = Title;
        this.EquipmentId = EquipmentId;
        this.Revision = Revision;
        this.Symptoms = Symptoms;
        this.DiagnosticSteps = DiagnosticSteps;
        this.SafetyStepIds = SafetyStepIds;
        this.Parts = Parts;
        this.Citations = Citations;
    }
    public string Title { get; }
    public string EquipmentId { get; }
    public string Revision { get; }
    public string Symptoms { get; }
    public ImmutableArray<DiagnosticStep> DiagnosticSteps { get; }
    public ImmutableArray<long> SafetyStepIds { get; }
    public ImmutableArray<string> Parts { get; }
    public ImmutableArray<long> Citations { get; }
    public bool Equals(WorkOrderDraft? other) => other is not null && Title == other.Title && EquipmentId == other.EquipmentId &&
        Revision == other.Revision && Symptoms == other.Symptoms && DomainValueEquality.SequenceEqual(DiagnosticSteps, other.DiagnosticSteps) &&
        DomainValueEquality.SequenceEqual(SafetyStepIds, other.SafetyStepIds) && DomainValueEquality.SequenceEqual(Parts, other.Parts) &&
        DomainValueEquality.SequenceEqual(Citations, other.Citations);
    public override int GetHashCode() => HashCode.Combine(Title, EquipmentId, Revision, Symptoms,
        DomainValueEquality.SequenceHash(DiagnosticSteps), DomainValueEquality.SequenceHash(SafetyStepIds),
        DomainValueEquality.SequenceHash(Parts), DomainValueEquality.SequenceHash(Citations));
}

public sealed record GateResult
{
    public GateResult(bool Ok, ImmutableArray<long> Missing)
    {
        this.Ok = Ok;
        this.Missing = DomainValueEquality.Normalize(Missing);
    }
    public bool Ok { get; init; }
    public ImmutableArray<long> Missing { get; init; }
    public bool Equals(GateResult? other) => other is not null && Ok == other.Ok && DomainValueEquality.SequenceEqual(Missing, other.Missing);
    public override int GetHashCode() => HashCode.Combine(Ok, DomainValueEquality.SequenceHash(Missing));
}

public sealed record RevisionResolution(string Revision, bool Superseded, string Note);

internal static class PythonWhitespace
{
    public static bool IsBlank(string? value)
    {
        if (string.IsNullOrEmpty(value)) return true;
        return StripIsEmpty(value);
    }

    // Python str.strip() recognizes Unicode whitespace and the four ASCII information separators.
    public static bool StripIsEmpty(string value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && IsWhitespace(value[start])) start++;
        while (end > start && IsWhitespace(value[end - 1])) end--;
        return start == end;
    }

    public static string Repr(string? value)
    {
        if (value is null) return "None";
        var quote = value.Contains('\'') && !value.Contains('"') ? '"' : '\'';
        var result = new StringBuilder(value.Length + 2).Append(quote);
        for (var index = 0; index < value.Length;)
        {
            Rune rune;
            var current = value[index];
            if (char.IsHighSurrogate(current) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                rune = new Rune(current, value[index + 1]);
                index += 2;
            }
            else if (char.IsSurrogate(current))
            {
                AppendHexEscape(result, current, 4, 'u');
                index++;
                continue;
            }
            else
            {
                rune = new Rune(current);
                index++;
            }

            var codePoint = rune.Value;
            if (codePoint == '\\') result.Append("\\\\");
            else if (codePoint == quote) result.Append('\\').Append(quote);
            else if (codePoint == '\n') result.Append("\\n");
            else if (codePoint == '\r') result.Append("\\r");
            else if (codePoint == '\t') result.Append("\\t");
            else if (IsPythonPrintable(rune)) result.Append(rune.ToString());
            else if (codePoint <= 0xff) AppendHexEscape(result, codePoint, 2, 'x');
            else if (codePoint <= 0xffff) AppendHexEscape(result, codePoint, 4, 'u');
            else AppendHexEscape(result, codePoint, 8, 'U');
        }

        return result.Append(quote).ToString();
    }

    private static bool IsWhitespace(char value) => char.IsWhiteSpace(value) || value is >= '\u001c' and <= '\u001f';

    private static bool IsPythonPrintable(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return category is not (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or
            UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or
            UnicodeCategory.ParagraphSeparator) &&
            !(category == UnicodeCategory.SpaceSeparator && rune.Value != 0x20);
    }

    private static void AppendHexEscape(StringBuilder result, int codePoint, int digits, char prefix) =>
        result.Append('\\').Append(prefix).Append(codePoint.ToString($"x{digits}", CultureInfo.InvariantCulture));
}
