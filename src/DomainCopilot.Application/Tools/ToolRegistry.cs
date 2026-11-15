using DomainCopilot.Domain.Workflow;
using DomainCopilot.Application.Text;
using System.Numerics;

namespace DomainCopilot.Application.Tools;

public sealed record ToolSchema
{
    public ToolSchema(string name, Role requiredRole, bool read, IReadOnlyDictionary<string, object?> schema)
    {
        if (string.IsNullOrEmpty(name)) throw new WorkflowError("a tool needs a name");
        Name = name; RequiredRole = requiredRole; Read = read; Schema = schema;
    }
    public string Name { get; }
    public Role RequiredRole { get; }
    public bool Read { get; }
    public IReadOnlyDictionary<string, object?> Schema { get; }
    public bool Equals(ToolSchema? other) => other is not null && Name == other.Name && RequiredRole == other.RequiredRole && Read == other.Read && ApplicationValueEquality.DictionaryEqual(Schema, other.Schema);
    public override int GetHashCode() => HashCode.Combine(Name, RequiredRole, Read, ApplicationValueEquality.DictionaryHash(Schema));
}

public sealed class ToolValidationError(string message) : WorkflowError(message);
public sealed class ApprovalRequiredError(string message) : WorkflowError(message);
public sealed class ToolNotAllowedError(string message) : WorkflowError(message);

public static class AgentToolAllowlists
{
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Values = new Dictionary<string, IReadOnlySet<string>>
    {
        ["SymptomMatcher"] = new HashSet<string>(["search_manuals", "get_document_revisions"], StringComparer.Ordinal),
        ["DiagnosticSafetyPlanner"] = new HashSet<string>(["get_safety_prerequisites"], StringComparer.Ordinal),
        ["WorkOrderGenerator"] = new HashSet<string>(["create_work_order_draft"], StringComparer.Ordinal),
        ["WorkflowOrchestrator"] = new HashSet<string>(["acknowledge_safety_steps"], StringComparer.Ordinal)
    };
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> AGENT_TOOL_ALLOWLISTS => Values;
}

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolSchema> _schemas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<IReadOnlyDictionary<string, object?>, object?>> _handlers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _approvedRuns = new(StringComparer.Ordinal);
    public void ApproveRun(string runId, Role role)
    { if (role != Role.SUPERVISOR) throw new ToolNotAllowedError("only a supervisor may authorize a write tool"); _approvedRuns.Add(runId); }
    public void Register(ToolSchema schema, Func<IReadOnlyDictionary<string, object?>, object?>? handler = null)
    { _schemas[schema.Name] = schema; if (handler is not null) _handlers[schema.Name] = handler; }
    public ToolSchema? GetSchema(string name) => _schemas.GetValueOrDefault(name);
    public IReadOnlyDictionary<string, Func<IReadOnlyDictionary<string, object?>, object?>> Handlers => _handlers;

    public object? Invoke(string name, IReadOnlyDictionary<string, object?> args, Role role)
    {
        if (!_schemas.TryGetValue(name, out var schema)) throw new ToolNotAllowedError($"unknown tool: {name}");
        if (role != schema.RequiredRole && !(name == "create_work_order_draft" && role == Role.SUPERVISOR))
            throw new ToolNotAllowedError($"role {role.Value} may not use {name}");
        if (!ValidateArgs(args, schema.Schema)) throw new ToolValidationError($"invalid arguments for {name}");
        if (!schema.Read && name == "create_work_order_draft")
        {
            args.TryGetValue("run_id", out var runId);
            if (role != Role.SUPERVISOR || runId is not string id || !_approvedRuns.Contains(id))
                throw new ApprovalRequiredError("supervisor approval is required before this write");
        }
        if (!_handlers.TryGetValue(name, out var handler)) throw new WorkflowError($"tool {name} is not implemented");
        return handler(args);
    }

    public object? InvokeForAgent(string agent, string name, IReadOnlyDictionary<string, object?> args, Role role)
    {
        if (!AgentToolAllowlists.Values.TryGetValue(agent, out var allowed) || !allowed.Contains(name))
            throw new ToolNotAllowedError($"agent '{agent}' may not use '{name}'");
        return Invoke(name, args, role);
    }

    public IReadOnlyList<string> AllowedFor(Role role) => _schemas.Values.Where(x => x.RequiredRole == role).Select(x => x.Name).Order(StringComparer.Ordinal).ToArray();

    public static bool ValidateArgs(IReadOnlyDictionary<string, object?> args, IReadOnlyDictionary<string, object?> schema) => Match(args, schema);
    private static bool Match(object? value, IReadOnlyDictionary<string, object?> schema)
    {
        var expected = schema.GetValueOrDefault("type") as string;
        switch (expected)
        {
            case "object":
                if (value is not IReadOnlyDictionary<string, object?> map) return false;
                var props = schema.GetValueOrDefault("properties") as IReadOnlyDictionary<string, object?>;
                if (props is null) return false;
                if (schema.GetValueOrDefault("required") is IEnumerable<object?> required && required.Any(x => !map.ContainsKey((string)x!))) return false;
                if (schema.GetValueOrDefault("additionalProperties") is false && map.Keys.Any(k => !props.ContainsKey(k))) return false;
                return map.All(pair => !props.TryGetValue(pair.Key, out var child) || child is not IReadOnlyDictionary<string, object?> childSchema || Match(pair.Value, childSchema));
            case "array":
                if (value is not System.Collections.IList list) return false;
                if (list.Count < Convert.ToInt32(schema.GetValueOrDefault("minItems") ?? 0)) return false;
                if (schema.GetValueOrDefault("maxItems") is not null && list.Count > Convert.ToInt32(schema["maxItems"])) return false;
                return schema.GetValueOrDefault("items") is not IReadOnlyDictionary<string, object?> itemSchema || list.Cast<object?>().All(x => Match(x, itemSchema));
            case "string":
                if (value is not string text) return false;
                var length = PythonString.CodePointLength(text);
                if (length < Convert.ToInt32(schema.GetValueOrDefault("minLength") ?? 0)) return false;
                return schema.GetValueOrDefault("maxLength") is null || length <= Convert.ToInt32(schema["maxLength"]);
            case "integer": if (value is not int and not long and not BigInteger) return false; break;
            case "number": if (value is not int and not long and not BigInteger and not float and not double and not decimal) return false; break;
            case "boolean": if (value is not bool) return false; break;
            case "null": if (value is not null) return false; break;
        }
        if (value is IConvertible numeric && value is not bool)
        {
            try
            {
                var number = Convert.ToDouble(numeric, System.Globalization.CultureInfo.InvariantCulture);
                if (schema.GetValueOrDefault("minimum") is not null && number < Convert.ToDouble(schema["minimum"])) return false;
                if (schema.GetValueOrDefault("maximum") is not null && number > Convert.ToDouble(schema["maximum"])) return false;
            }
            catch (FormatException) { }
        }
        if (value is BigInteger bigInteger)
        {
            if (schema.GetValueOrDefault("minimum") is not null && bigInteger < BigInteger.Parse(Convert.ToString(schema["minimum"], System.Globalization.CultureInfo.InvariantCulture)!)) return false;
            if (schema.GetValueOrDefault("maximum") is not null && bigInteger > BigInteger.Parse(Convert.ToString(schema["maximum"], System.Globalization.CultureInfo.InvariantCulture)!)) return false;
        }
        return schema.GetValueOrDefault("enum") is not IEnumerable<object?> enums || enums.Contains(value);
    }
}

public static class ToolSchemas
{
    private static IReadOnlyDictionary<string, object?> Obj(params (string Key, object? Value)[] values) => values.ToDictionary(x => x.Key, x => x.Value);
    private static IReadOnlyDictionary<string, object?> Schema(string name, string[] required, IReadOnlyDictionary<string, object?> properties) =>
        Obj(("name", name), ("required_role", "technician"), ("read", name is "search_manuals" or "get_document_revisions" or "get_safety_prerequisites"),
            ("schema", Obj(("type", "object"), ("required", required), ("additionalProperties", false), ("properties", properties))));

    public static IReadOnlyDictionary<string, object?> SearchManuals() => Schema("search_manuals", ["query", "limit"], Obj(
        ("query", Obj(("type", "string"))), ("limit", Obj(("type", "integer"), ("minimum", 1), ("maximum", 50)))));
    public static IReadOnlyDictionary<string, object?> GetDocumentRevisions() => Schema("get_document_revisions", ["doc_id"], Obj(
        ("doc_id", Obj(("type", "string")))));
    public static IReadOnlyDictionary<string, object?> GetSafetyPrerequisites() => Schema("get_safety_prerequisites", ["doc_ids"], Obj(
        ("doc_ids", Obj(("type", "array"), ("items", Obj(("type", "string"))), ("minItems", 0)))));
    public static IReadOnlyDictionary<string, object?> CreateWorkOrderDraft() => Schema("create_work_order_draft", ["run_id", "title", "symptoms", "diagnostic_steps"], Obj(
        ("run_id", Obj(("type", "string"))), ("title", Obj(("type", "string"))), ("symptoms", Obj(("type", "string"))),
        ("diagnostic_steps", Obj(("type", "array"), ("items", Obj(("type", "string"))), ("minItems", 0)))));
    public static IReadOnlyDictionary<string, object?> AcknowledgeSafetySteps() => Schema("acknowledge_safety_steps", ["run_id", "step_ids"], Obj(
        ("run_id", Obj(("type", "string"))), ("step_ids", Obj(("type", "array"), ("items", Obj(("type", "integer"))), ("minItems", 0)))));

    public static ToolRegistry BuildToolRegistry()
    {
        var registry = new ToolRegistry();
        foreach (var item in new[] { SearchManuals(), GetDocumentRevisions(), GetSafetyPrerequisites(), CreateWorkOrderDraft(), AcknowledgeSafetySteps() })
            registry.Register(new ToolSchema((string)item["name"]!, Role.Parse((string)item["required_role"]!), (bool)item["read"]!,
                (IReadOnlyDictionary<string, object?>)item["schema"]!));
        return registry;
    }
}
