using System.Text.Json.Serialization;

namespace DomainCopilot.Api.Http;

public sealed record LoginRequest([property: JsonPropertyName("tenant_id")] string? TenantId,
    string? Username, string? Password);
public sealed record AskRequest(string? Question);
public sealed record RunStartRequest(string? Symptoms,
    [property: JsonPropertyName("installed_revision")] string? InstalledRevision = null);
public sealed record AcknowledgeRequest([property: JsonPropertyName("step_ids")] IReadOnlyList<long>? StepIds);
public sealed record ApprovalRequest(string? Decision, string? Comment = null,
    [property: JsonPropertyName("edited_content")] IReadOnlyDictionary<string, object?>? EditedContent = null);

public sealed record AuthenticatedUser(string TenantId, long UserId, string Role, string Username);
