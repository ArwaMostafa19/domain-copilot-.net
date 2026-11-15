using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using DomainCopilot.Application;
using DomainCopilot.Domain.Workflow;
using Npgsql;
using NpgsqlTypes;

namespace DomainCopilot.Infrastructure.Persistence;

public sealed class PostgresUserRepository(IPostgresConnections connections) : IUserRepository
{
    public UserRecord? GetByUsername(string tenantId, string username) => TenantDatabase.WithTenant(connections, tenantId, (connection, transaction) =>
    {
        using var command = new NpgsqlCommand("""
            SELECT id,tenant_id,username,role,active,password_hash
            FROM users WHERE username=@username
            """, connection, transaction);
        command.Parameters.AddWithValue("username", username);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadUser(reader) : null;
    });

    public UserRecord? Get(string tenantId, long userId) => TenantDatabase.WithTenant(connections, tenantId, (connection, transaction) =>
    {
        using var command = new NpgsqlCommand("""
            SELECT id,tenant_id,username,role,active,password_hash
            FROM users WHERE id=@id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", userId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadUser(reader) : null;
    });

    private static UserRecord ReadUser(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetBoolean(4), reader.IsDBNull(5) ? "" : reader.GetString(5));
}

public sealed class PostgresRunRepository(IPostgresConnections connections) : IRunRepository
{
    public string Create(string tenantId, long userId, string question) =>
        TenantDatabase.WithTenant(connections, tenantId, (connection, transaction) =>
        {
            using var command = new NpgsqlCommand("""
                INSERT INTO runs (tenant_id,user_id,question,status)
                VALUES (@tenant_id,@user_id,@question,'running') RETURNING id
                """, connection, transaction);
            command.Parameters.AddWithValue("tenant_id", tenantId);
            command.Parameters.AddWithValue("user_id", userId);
            command.Parameters.AddWithValue("question", question);
            return ((Guid)command.ExecuteScalar()!).ToString("D", CultureInfo.InvariantCulture);
        });

    public RunRecord? Get(string tenantId, string runId) => TenantDatabase.WithTenant(connections, tenantId, (connection, transaction) =>
    {
        using var command = new NpgsqlCommand("""
            SELECT id,tenant_id,user_id,question,status,total_tokens,estimated_tokens
            FROM runs WHERE id=@id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", Guid.Parse(runId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRun(reader) : null;
    });

    public void SetStatus(string tenantId, string runId, string status, int? totalTokens = null,
        bool? estimatedTokens = null, bool finished = false) =>
        TenantDatabase.WithTenant(connections, tenantId, (connection, transaction) =>
        {
            using var command = new NpgsqlCommand("""
                UPDATE runs SET status=@status,
                  total_tokens=COALESCE(@total_tokens,total_tokens),
                  estimated_tokens=COALESCE(@estimated_tokens,estimated_tokens),
                  finished_at=CASE WHEN @finished THEN now() ELSE finished_at END
                WHERE id=@id
                """, connection, transaction);
            command.Parameters.AddWithValue("status", status);
            command.Parameters.AddWithValue("total_tokens", (object?)totalTokens ?? DBNull.Value);
            command.Parameters.AddWithValue("estimated_tokens", (object?)estimatedTokens ?? DBNull.Value);
            command.Parameters.AddWithValue("finished", finished);
            command.Parameters.AddWithValue("id", Guid.Parse(runId));
            command.ExecuteNonQuery();
        });

    public void AddStep(string tenantId, string runId, RunStepRecord step) =>
        TenantDatabase.WithTenant(connections, tenantId, (connection, transaction) =>
        {
            using var command = new NpgsqlCommand("""
                INSERT INTO run_steps
                    (tenant_id,run_id,ordinal,agent,tools_used,evidence_ids,prompt_tokens,
                     completion_tokens,estimated,duration_ms,outcome,summary)
                VALUES (@tenant_id,@run_id,@ordinal,@agent,@tools,@evidence,@prompt,
                        @completion,@estimated,@duration,@outcome,@summary)
                """, connection, transaction);
            command.Parameters.AddWithValue("tenant_id", tenantId);
            command.Parameters.AddWithValue("run_id", Guid.Parse(runId));
            command.Parameters.AddWithValue("ordinal", step.Ordinal);
            command.Parameters.AddWithValue("agent", step.Agent);
            command.Parameters.AddWithValue("tools", step.ToolsUsed.ToArray());
            command.Parameters.AddWithValue("evidence", step.EvidenceIds.ToArray());
            command.Parameters.AddWithValue("prompt", step.PromptTokens);
            command.Parameters.AddWithValue("completion", step.CompletionTokens);
            command.Parameters.AddWithValue("estimated", step.Estimated);
            command.Parameters.AddWithValue("duration", step.DurationMs);
            command.Parameters.AddWithValue("outcome", step.Outcome);
            command.Parameters.AddWithValue("summary", (object?)step.Summary ?? DBNull.Value);
            command.ExecuteNonQuery();
        });

    public IReadOnlyList<RunStepRecord> Steps(string tenantId, string runId) =>
        TenantDatabase.WithTenant(connections, tenantId, (connection, transaction) =>
        {
            using var command = new NpgsqlCommand("""
                SELECT ordinal,agent,tools_used,evidence_ids,prompt_tokens,completion_tokens,
                       estimated,duration_ms,outcome,summary
                FROM run_steps WHERE run_id=@run_id ORDER BY ordinal
                """, connection, transaction);
            command.Parameters.AddWithValue("run_id", Guid.Parse(runId));
            using var reader = command.ExecuteReader();
            var rows = new List<RunStepRecord>();
            while (reader.Read())
                rows.Add(new RunStepRecord(reader.GetInt32(0), reader.GetString(1),
                    reader.IsDBNull(2) ? [] : reader.GetFieldValue<string[]>(2).ToImmutableArray(),
                    reader.IsDBNull(3) ? [] : reader.GetFieldValue<long[]>(3).ToImmutableArray(),
                    reader.GetInt32(4), reader.GetInt32(5), reader.GetBoolean(6), reader.GetInt32(7),
                    reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9)));
            return (IReadOnlyList<RunStepRecord>)rows;
        });

    public IReadOnlyList<RunRecord> ListForUser(string tenantId, long userId) =>
        List(tenantId, "user_id=@user_id", command => command.Parameters.AddWithValue("user_id", userId));

    public IReadOnlyList<RunRecord> ListForTenant(string tenantId, string? status = null) =>
        string.IsNullOrEmpty(status)
            ? List(tenantId, "TRUE", _ => { })
            : List(tenantId, "status=@status", command => command.Parameters.AddWithValue("status", status));

    private IReadOnlyList<RunRecord> List(string tenantId, string predicate, Action<NpgsqlCommand> bind) =>
        TenantDatabase.WithTenant(connections, tenantId, (connection, transaction) =>
        {
            using var command = new NpgsqlCommand($"""
                SELECT id,tenant_id,user_id,question,status,total_tokens,estimated_tokens
                FROM runs WHERE {predicate} ORDER BY created_at DESC
                """, connection, transaction);
            bind(command);
            using var reader = command.ExecuteReader();
            var rows = new List<RunRecord>();
            while (reader.Read()) rows.Add(ReadRun(reader));
            return (IReadOnlyList<RunRecord>)rows;
        });

    private static RunRecord ReadRun(NpgsqlDataReader reader) => new(
        ((Guid)reader.GetValue(0)).ToString("D", CultureInfo.InvariantCulture), reader.GetString(1),
        reader.GetInt64(2), reader.GetString(3), reader.GetString(4),
        reader.IsDBNull(5) ? 0 : reader.GetInt32(5), reader.IsDBNull(6) ? false : reader.GetBoolean(6));
}

public sealed class PostgresWorkOrderRepository(IPostgresConnections connections) : IWorkOrderRepository
{
    public IReadOnlyDictionary<string, object?> CreateDraft(string tenantId, string runId, IReadOnlyDictionary<string, object?> content) =>
        TenantDatabase.WithTenant(connections, tenantId, (connection, transaction) =>
        {
            using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@run_id, 0))", connection, transaction))
            {
                lockCommand.Parameters.AddWithValue("run_id", runId);
                lockCommand.ExecuteNonQuery();
            }
            int version;
            using (var versionCommand = new NpgsqlCommand(
                "SELECT COALESCE(MAX(version),0)+1 FROM work_orders WHERE run_id=@run_id", connection, transaction))
            {
                versionCommand.Parameters.AddWithValue("run_id", Guid.Parse(runId));
                version = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            using var command = new NpgsqlCommand("""
                INSERT INTO work_orders (tenant_id,run_id,version,status,content)
                VALUES (@tenant_id,@run_id,@version,'draft',@content::jsonb)
                """, connection, transaction);
            command.Parameters.AddWithValue("tenant_id", tenantId);
            command.Parameters.AddWithValue("run_id", Guid.Parse(runId));
            command.Parameters.AddWithValue("version", version);
            command.Parameters.AddWithValue("content", JsonSerializer.Serialize(content, InfrastructureJson.Options));
            command.ExecuteNonQuery();
            return content;
        });

    public WorkOrderRecord? Latest(string tenantId, string runId) => TenantDatabase.WithTenant(connections, tenantId, (connection, transaction) =>
    {
        using var command = new NpgsqlCommand("""
            SELECT id,run_id,version,status,content FROM work_orders
            WHERE run_id=@run_id ORDER BY version DESC LIMIT 1
            """, connection, transaction);
        command.Parameters.AddWithValue("run_id", Guid.Parse(runId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var content = JsonValue.ReadObject(reader.GetString(4));
        return new WorkOrderRecord(((Guid)reader.GetValue(0)).ToString("D", CultureInfo.InvariantCulture),
            ((Guid)reader.GetValue(1)).ToString("D", CultureInfo.InvariantCulture),
            reader.GetInt32(2), reader.GetString(3), content);
    });

    public void SetStatus(string tenantId, string workOrderId, string status) =>
        TenantDatabase.WithTenant(connections, tenantId, (connection, transaction) =>
        {
            using var command = new NpgsqlCommand("UPDATE work_orders SET status=@status WHERE id=@id", connection, transaction);
            command.Parameters.AddWithValue("status", status);
            command.Parameters.AddWithValue("id", Guid.Parse(workOrderId));
            command.ExecuteNonQuery();
        });
}

public sealed class PostgresAuditLog(IPostgresConnections connections) : IAuditLog
{
    public void Record(string tenantId, long? actorUserId, string action, string subjectType,
        string? subjectId, IReadOnlyDictionary<string, object?>? detail = null) =>
        TenantDatabase.WithTenant(connections, tenantId, (connection, transaction) =>
        {
            using var command = new NpgsqlCommand("""
                INSERT INTO audit_log (tenant_id,actor_user_id,action,subject_type,subject_id,detail)
                VALUES (@tenant_id,@actor,@action,@subject_type,@subject_id,@detail::jsonb)
                """, connection, transaction);
            command.Parameters.AddWithValue("tenant_id", tenantId);
            command.Parameters.AddWithValue("actor", (object?)actorUserId ?? DBNull.Value);
            command.Parameters.AddWithValue("action", action);
            command.Parameters.AddWithValue("subject_type", subjectType);
            command.Parameters.AddWithValue("subject_id", (object?)subjectId ?? DBNull.Value);
            command.Parameters.AddWithValue("detail", detail is null ? DBNull.Value : JsonSerializer.Serialize(detail, InfrastructureJson.Options));
            command.ExecuteNonQuery();
        });
}

public sealed class PostgresSafetyPrerequisiteRepository(IPostgresConnections connections) : ISafetyPrerequisiteRepository
{
    public IReadOnlyList<SafetyStepRecord> RequiredForDocuments(string tenantId, IReadOnlyList<string> docIds)
    {
        if (docIds.Count == 0) return Array.Empty<SafetyStepRecord>();
        return Read(tenantId, """
            SELECT sp.id,sp.step_no,sp.step_text,d.doc_id
            FROM safety_prerequisites sp JOIN documents d ON d.id=sp.document_id
            WHERE d.doc_id=ANY(@doc_ids) ORDER BY d.doc_id,sp.step_no
            """, command => command.Parameters.AddWithValue("doc_ids", docIds.ToArray()));
    }

    public IReadOnlyList<SafetyStepRecord> RequiredByIds(string tenantId, IReadOnlyList<long> stepIds)
    {
        if (stepIds.Count == 0) return Array.Empty<SafetyStepRecord>();
        return Read(tenantId, """
            SELECT sp.id,sp.step_no,sp.step_text,d.doc_id
            FROM safety_prerequisites sp JOIN documents d ON d.id=sp.document_id
            WHERE sp.id=ANY(@step_ids)
            ORDER BY array_position(@step_ids::bigint[],sp.id)
            """, command => command.Parameters.AddWithValue("step_ids", stepIds.ToArray()));
    }

    private IReadOnlyList<SafetyStepRecord> Read(string tenantId, string sql, Action<NpgsqlCommand> bind) =>
        TenantDatabase.WithTenant(connections, tenantId, (connection, transaction) =>
        {
            using var command = new NpgsqlCommand(sql, connection, transaction);
            bind(command);
            using var reader = command.ExecuteReader();
            var rows = new List<SafetyStepRecord>();
            while (reader.Read())
                rows.Add(new SafetyStepRecord(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            return (IReadOnlyList<SafetyStepRecord>)rows;
        });
}

public sealed class PostgresAskLogRepository(IPostgresConnections connections) : IAskLogRepository
{
    public long Save(string tenantId, AskLogRecord record) => TenantDatabase.WithTenant(connections, tenantId, (connection, transaction) =>
    {
        using var command = new NpgsqlCommand("""
            INSERT INTO ask_log
                (tenant_id,user_id,correlation_id,question,answer_text,refused,reason,citations,
                 prompt_tokens,completion_tokens,estimated)
            VALUES (@tenant_id,@user_id,@correlation,@question,@answer,@refused,@reason,
                    @citations::jsonb,@prompt,@completion,@estimated) RETURNING id
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("user_id", (object?)record.UserId ?? DBNull.Value);
        command.Parameters.AddWithValue("correlation", (object?)record.CorrelationId ?? DBNull.Value);
        command.Parameters.AddWithValue("question", record.Question);
        command.Parameters.AddWithValue("answer", record.AnswerText);
        command.Parameters.AddWithValue("refused", record.Refused);
        command.Parameters.AddWithValue("reason", (object?)record.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("citations", JsonSerializer.Serialize(record.Citations, InfrastructureJson.Options));
        command.Parameters.AddWithValue("prompt", record.PromptTokens);
        command.Parameters.AddWithValue("completion", record.CompletionTokens);
        command.Parameters.AddWithValue("estimated", record.Estimated);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    public IReadOnlyList<AskLogRecord> ListForUser(string tenantId, long userId) =>
        TenantDatabase.WithTenant(connections, tenantId, (connection, transaction) =>
        {
            using var command = new NpgsqlCommand("""
                SELECT id,tenant_id,user_id,correlation_id,question,answer_text,refused,reason,
                       citations,prompt_tokens,completion_tokens,estimated,created_at
                FROM ask_log WHERE user_id=@user_id ORDER BY created_at DESC
                """, connection, transaction);
            command.Parameters.AddWithValue("user_id", userId);
            using var reader = command.ExecuteReader();
            var rows = new List<AskLogRecord>();
            while (reader.Read())
            {
                var citations = reader.IsDBNull(8) ? ImmutableArray<IReadOnlyDictionary<string, object?>>.Empty :
                    JsonValue.ReadObjectArray(reader.GetString(8));
                rows.Add(new AskLogRecord(reader.GetInt64(0), reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5),
                    reader.GetBoolean(6), reader.IsDBNull(7) ? null : reader.GetString(7), citations,
                    reader.GetInt32(9), reader.GetInt32(10), reader.GetBoolean(11),
                    reader.IsDBNull(12) ? null : JsonValue.ToPythonDateTime(reader.GetDateTime(12))));
            }
            return (IReadOnlyList<AskLogRecord>)rows;
        });
}

public sealed class PostgresUsageRepository(IPostgresConnections connections) : IUsageRepository
{
    public UsageSummary ForTenant(string tenantId) => Read(tenantId, null);
    public UsageSummary ForUser(string tenantId, long userId) => Read(tenantId, userId);

    private UsageSummary Read(string tenantId, long? userId) => TenantDatabase.WithTenant(connections, tenantId, (connection, transaction) =>
    {
        var askPredicate = userId.HasValue ? " WHERE user_id=@user_id" : string.Empty;
        var runPredicate = userId.HasValue ? " WHERE user_id=@user_id" : string.Empty;
        using var asks = new NpgsqlCommand($"""
            SELECT date_trunc('day',created_at)::date,SUM(prompt_tokens)::bigint,SUM(completion_tokens)::bigint,COUNT(*)::bigint
            FROM ask_log{askPredicate} GROUP BY 1 ORDER BY 1 DESC
            """, connection, transaction);
        using var runs = new NpgsqlCommand($"""
            SELECT date_trunc('day',created_at)::date,SUM(total_tokens)::bigint,COUNT(*)::bigint
            FROM runs{runPredicate} GROUP BY 1 ORDER BY 1 DESC
            """, connection, transaction);
        if (userId.HasValue)
        {
            asks.Parameters.AddWithValue("user_id", userId.Value);
            runs.Parameters.AddWithValue("user_id", userId.Value);
        }
        var dailyAsks = new List<DailyAskUsage>();
        using (var reader = asks.ExecuteReader())
            while (reader.Read()) dailyAsks.Add(new DailyAskUsage(reader.GetFieldValue<DateOnly>(0), reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                reader.IsDBNull(2) ? 0 : reader.GetInt64(2), reader.GetInt64(3)));
        var dailyRuns = new List<DailyRunUsage>();
        using (var reader = runs.ExecuteReader())
            while (reader.Read()) dailyRuns.Add(new DailyRunUsage(reader.GetFieldValue<DateOnly>(0), reader.IsDBNull(1) ? 0 : reader.GetInt64(1), reader.GetInt64(2)));
        return new UsageSummary(dailyAsks, dailyRuns);
    });
}

internal static class JsonValue
{
    public static IReadOnlyDictionary<string, object?> ReadObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        return (IReadOnlyDictionary<string, object?>)Read(document.RootElement)!;
    }

    public static ImmutableArray<IReadOnlyDictionary<string, object?>> ReadObjectArray(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray()
            .Select(item => (IReadOnlyDictionary<string, object?>)Read(item)!)
            .ToImmutableArray();
    }

    private static object? Read(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(x => x.Name, x => Read(x.Value), StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(Read).ToArray(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var value) => value,
        JsonValueKind.Number when BigInteger.TryParse(element.GetRawText(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) => integer,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => null
    };

    public static string ToPythonDateTime(DateTime value)
    {
        var utc = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        var fraction = utc.ToString("ffffff", CultureInfo.InvariantCulture).TrimEnd('0');
        return utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
            (fraction.Length == 0 ? "" : "." + fraction) + "+00:00";
    }
}

