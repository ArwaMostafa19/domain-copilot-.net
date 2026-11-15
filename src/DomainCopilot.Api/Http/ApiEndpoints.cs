using System.Collections.Immutable;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using DomainCopilot.Api.Configuration;
using DomainCopilot.Application;
using DomainCopilot.Application.Agents;
using DomainCopilot.Application.Answering;
using DomainCopilot.Application.Ingestion;
using DomainCopilot.Application.Security;
using DomainCopilot.Application.Workflow;
using DomainCopilot.Domain.Llm;
using DomainCopilot.Domain.Rag;
using DomainCopilot.Domain.Workflow;
using Microsoft.Extensions.Options;

namespace DomainCopilot.Api.Http;

public static class ApiEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapCopilotEndpoints(this WebApplication app)
    {
        app.MapGet("/", Root).AllowAnonymous();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithName("Health").AllowAnonymous();
        app.MapGet("/ready", Ready).AllowAnonymous();

        app.MapPost("/auth/login", Login).AllowAnonymous();
        app.MapGet("/me", Me).RequireAuthorization();
        app.MapPost("/ask", Ask).RequireAuthorization();
        app.MapPost("/ask/stream", AskStream).RequireAuthorization();
        app.MapGet("/asks", ListAsks).RequireAuthorization();
        app.MapPost("/runs", StartRun).RequireAuthorization();
        app.MapGet("/runs", ListRuns).RequireAuthorization();
        app.MapGet("/runs/{run_id}", GetRun).RequireAuthorization();
        app.MapPost("/runs/{run_id}/acknowledge", AcknowledgeRun).RequireAuthorization();
        app.MapPost("/runs/{run_id}/approval", ApproveRun).RequireAuthorization();
        app.MapPost("/runs/{run_id}/cancel", CancelRun).RequireAuthorization();
        app.MapGet("/documents", ListDocuments).RequireAuthorization();
        app.MapGet("/documents/{doc_id}", GetDocument).RequireAuthorization();
        app.MapPost("/ingest/corpus", IngestCorpus).RequireAuthorization();
        app.MapGet("/usage", Usage).RequireAuthorization();
    }

    private static IResult Root(IWebHostEnvironment environment)
    {
        var index = Path.Combine(environment.ContentRootPath, "static", "index.html");
        return File.Exists(index) ? Results.File(index, "text/html; charset=utf-8") :
            Results.Json(new { status = "ok", message = "Domain Copilot API" });
    }

    private static async Task<IResult> Ready(HttpContext context, IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            await services.GetRequiredService<IDatabaseReadiness>().CheckAsync(cancellationToken);
        }
        catch
        {
            return ApiError(context, StatusCodes.Status503ServiceUnavailable, "database unavailable");
        }
        try
        {
            var chain = services.GetRequiredService<ILlmProvider>();
            if (!chain.Healthcheck()) return ApiError(context, StatusCodes.Status503ServiceUnavailable, "model provider unavailable");
        }
        catch
        {
            return ApiError(context, StatusCodes.Status503ServiceUnavailable, "model provider unavailable");
        }
        return Results.Ok(new { status = "ready" });
    }

    private static IResult Login(LoginRequest? body, HttpContext context, IUserRepository users, IOptions<CopilotOptions> options)
    {
        if (body is null) return ValidationProblem(context, "tenant_id", "username", "password");
        var missing = new List<string>();
        if (body.TenantId is null) missing.Add("tenant_id");
        if (body.Username is null) missing.Add("username");
        if (body.Password is null) missing.Add("password");
        if (missing.Count > 0) return ValidationProblem(context, missing.ToArray());
        try
        {
            var profile = Authentication.Login(body.TenantId!, body.Username!.Trim(), body.Password!, users,
                options.Value.AuthSecret, options.Value.TokenTtlSeconds);
            return Results.Json(profile);
        }
        catch (AuthError)
        {
            return ApiError(context, StatusCodes.Status401Unauthorized, "invalid credentials");
        }
    }

    private static IResult Me(ClaimsPrincipal principal) => Results.Json(new
    {
        tenant_id = principal.FindFirstValue("tenant_id"),
        user_id = UserId(principal),
        role = principal.FindFirstValue(ClaimTypes.Role),
        username = principal.FindFirstValue(ClaimTypes.Name)
    });

    private static IResult Ask(AskRequest? body, HttpContext context, IServiceProvider services, ILoggerFactory loggerFactory)
    {
        if (body?.Question is null) return ValidationProblem(context, "question");
        var user = CurrentUser(context.User);
        var correlationId = CorrelationId(context);
        Answer answer;
        IAskLogRepository askLogs;
        try
        {
            var dependencies = new AnswerDependencies(services.GetRequiredService<IEmbedder>(), services.GetRequiredService<IChunkSearch>(),
                services.GetRequiredService<ILlmProvider>(), services.GetRequiredService<IRetrievalSettings>());
            askLogs = services.GetRequiredService<IAskLogRepository>();
            answer = AnswerService.AnswerQuestion(body.Question, user.TenantId, dependencies, correlationId);
        }
        catch (Exception error) when (error is ConfigError or EmbeddingModelMismatchError)
        { return ApiError(context, StatusCodes.Status503ServiceUnavailable, "model configuration unavailable"); }
        catch (LLMError)
        { return ApiError(context, StatusCodes.Status502BadGateway, "provider unavailable"); }
        var citations = Citations(answer);
        SaveAsk(askLogs, user, correlationId, body.Question, answer, citations, loggerFactory.CreateLogger("AskLog"));
        return Results.Json(AskPayload(answer, citations, includeEvidence: true));
    }

    private static IResult AskStream(AskRequest? body, HttpContext context, IServiceProvider services, ILoggerFactory loggerFactory)
    {
        if (body?.Question is null) return ValidationProblem(context, "question");
        var user = CurrentUser(context.User);
        var correlationId = CorrelationId(context);
        AnswerDependencies dependencies;
        IAskLogRepository askLogs;
        try
        {
            dependencies = new AnswerDependencies(services.GetRequiredService<IEmbedder>(), services.GetRequiredService<IChunkSearch>(),
                services.GetRequiredService<ILlmProvider>(), services.GetRequiredService<IRetrievalSettings>());
            askLogs = services.GetRequiredService<IAskLogRepository>();
        }
        catch (Exception error) when (error is ConfigError or EmbeddingModelMismatchError)
        { return ApiError(context, StatusCodes.Status503ServiceUnavailable, "model configuration unavailable"); }
        catch (LLMError)
        { return ApiError(context, StatusCodes.Status502BadGateway, "provider unavailable"); }
        return Sse(async (stream, cancellationToken) =>
        {
            try
            {
                foreach (var item in AnswerService.StreamAnswerQuestion(body.Question, user.TenantId, dependencies, correlationId, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!item.TryGetValue("event", out var eventValue) || !Equals(eventValue, "done"))
                    {
                        await WriteEventForClient(stream, item, cancellationToken);
                        continue;
                    }
                    if (item.GetValueOrDefault("answer") is not Answer answer) continue;
                    var citations = Citations(answer);
                    SaveAsk(askLogs, user, correlationId, body.Question, answer, citations, loggerFactory.CreateLogger("AskLog"));
                    await WriteEventForClient(stream, AskPayload(answer, citations, includeEvidence: false, eventName: "done"), cancellationToken);
                }
            }
            catch (ClientDisconnectedException) { }
            catch (LLMError)
            {
                try { await WriteEventForClient(stream, new { @event = "error", detail = "provider unavailable" }, cancellationToken); }
                catch (ClientDisconnectedException) { }
            }
        }, noCache: true);
    }

    private static IResult ListAsks(ClaimsPrincipal principal, HttpContext context, IAskLogRepository logs)
    {
        var user = CurrentUser(principal);
        return Results.Json(logs.ListForUser(user.TenantId, user.UserId).Select(row => new
        {
            id = row.Id, correlation_id = row.CorrelationId, question = row.Question, answer_text = row.AnswerText,
            refused = row.Refused, reason = row.Reason, citations = row.Citations,
            prompt_tokens = row.PromptTokens, completion_tokens = row.CompletionTokens, estimated = row.Estimated,
            created_at = row.CreatedAt
        }));
    }

    private static IResult StartRun(RunStartRequest? body, HttpContext context, IServiceProvider services, ILoggerFactory loggerFactory)
    {
        if (body?.Symptoms is null) return ValidationProblem(context, "symptoms");
        var user = CurrentUser(context.User);
        var correlationId = CorrelationId(context);
        var orchestrator = services.GetRequiredService<WorkflowOrchestrator>();
        var safety = services.GetRequiredService<ISafetyPrerequisiteRepository>();
        var runs = services.GetRequiredService<IRunRepository>();
        return Sse(async (stream, cancellationToken) =>
        {
            string? runId = null;
            try
            {
                var output = orchestrator.StartRun(new WorkflowStartInput(body.Symptoms, body.InstalledRevision, user.UserId, user.TenantId),
                    correlationId, item =>
                    {
                        if (item.GetValueOrDefault("run_id") is string startedId) runId = startedId;
                        WriteEventForClient(stream, item, cancellationToken).GetAwaiter().GetResult();
                    });
                var requiredSteps = safety.RequiredByIds(user.TenantId, output.RequiredSafetyStepIds);
                await WriteEventForClient(stream, new
                {
                    @event = "done", run_id = output.RunId, required_safety_step_ids = output.RequiredSafetyStepIds,
                    required_safety_steps = requiredSteps.Select(step => new { id = step.Id, step_no = step.StepNo, text = step.Text, doc_id = step.DocId })
                }, cancellationToken);
            }
            catch (ClientDisconnectedException) { }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (runId is not null)
                {
                    try
                    {
                        var run = runs.Get(user.TenantId, runId);
                        if (run?.Status == "running") runs.SetStatus(user.TenantId, run.Id, "failed", finished: true);
                    }
                    catch (Exception persistenceError)
                    {
                        loggerFactory.CreateLogger("Workflow").LogError("Could not persist failed workflow ({ErrorType})", persistenceError.GetType().Name);
                    }
                }
                loggerFactory.CreateLogger("Workflow").LogError("Workflow start failed ({ErrorType}); correlation {CorrelationId}", error.GetType().Name, correlationId);
                var detail = error is EquipmentNotFoundError or WorkflowInputError ? error.Message : "workflow failed";
                await WriteEvent(stream, new { @event = "error", detail }, cancellationToken);
            }
        });
    }

    private static IResult AcknowledgeRun(string run_id, AcknowledgeRequest? body, HttpContext context, IServiceProvider services, ILoggerFactory loggerFactory)
    {
        if (body?.StepIds is null) return ValidationProblem(context, "step_ids");
        var user = CurrentUser(context.User);
        var runs = services.GetRequiredService<IRunRepository>();
        var run = OwnedRun(runs, user, run_id);
        if (run is null) return NotFound(context, "run not found");
        var orchestrator = services.GetRequiredService<WorkflowOrchestrator>();
        var correlationId = CorrelationId(context);
        try { orchestrator.Acknowledge(run_id, body.StepIds, user.UserId, user.TenantId, correlationId); }
        catch (WorkflowError error) { return NotFound(context, error.Message); }

        return Sse(async (stream, cancellationToken) =>
        {
            try
            {
                var workOrder = orchestrator.Generate(run_id, user.UserId, user.TenantId, body.StepIds, correlationId,
                    item => WriteEventForClient(stream, item, cancellationToken).GetAwaiter().GetResult());
                await WriteEventForClient(stream, new { @event = "done", work_order = workOrder }, cancellationToken);
            }
            catch (ClientDisconnectedException) { }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (GateNotSatisfiedError)
            {
                await WriteEvent(stream, new { @event = "error", detail = "safety acknowledgement required" }, cancellationToken);
            }
            catch (Exception error)
            {
                loggerFactory.CreateLogger("Workflow").LogError("Workflow generation failed ({ErrorType}); run {RunId}; correlation {CorrelationId}", error.GetType().Name, run_id, correlationId);
                await WriteEvent(stream, new { @event = "error", detail = $"Work order generation failed ({error.GetType().Name}). Reference: {correlationId}" }, cancellationToken);
            }
        });
    }

    private static IResult ApproveRun(string run_id, ApprovalRequest? body, HttpContext context, IServiceProvider services)
    {
        var user = CurrentUser(context.User);
        if (body?.Decision is null) return ValidationProblem(context, "decision");
        if (user.Role != "supervisor") return ApiError(context, StatusCodes.Status403Forbidden, "forbidden: technician cannot approve");
        var runs = services.GetRequiredService<IRunRepository>();
        var run = OwnedRun(runs, user, run_id);
        if (run is null) return NotFound(context, "run not found");
        if (run.Status is "running" or "awaiting_acknowledgement")
            return ApiError(context, StatusCodes.Status409Conflict, "approval before acknowledgement is not permitted");
        try
        {
            var orchestrator = services.GetRequiredService<WorkflowOrchestrator>();
            var result = orchestrator.Approval(run_id, body?.Decision ?? "", Role.SUPERVISOR, user.UserId, user.TenantId,
                body?.Comment ?? "", body?.EditedContent, CorrelationId(context));
            return Results.Json(result);
        }
        catch (WorkflowError error) { return ApiError(context, StatusCodes.Status409Conflict, error.Message); }
    }

    private static IResult CancelRun(string run_id, ClaimsPrincipal principal, HttpContext context, IRunRepository runs)
    {
        var user = CurrentUser(principal);
        var run = OwnedRun(runs, user, run_id);
        if (run is null) return NotFound(context, "run not found");
        if (run.Status is "approved" or "rejected" or "cancelled" or "completed" or "failed")
            return ApiError(context, StatusCodes.Status409Conflict, "run cannot be cancelled");
        runs.SetStatus(user.TenantId, run_id, "cancelled", finished: true);
        return Results.Json(new { id = run_id, status = "cancelled" });
    }

    private static IResult ListRuns(ClaimsPrincipal principal, HttpContext context, string? status, IRunRepository runs)
    {
        var user = CurrentUser(principal);
        var items = user.Role == "supervisor" ? runs.ListForTenant(user.TenantId, status) :
            runs.ListForUser(user.TenantId, user.UserId).Where(run => string.IsNullOrEmpty(status) || run.Status == status).ToArray();
        return Results.Json(items.Select(run => new
        {
            id = run.Id, tenant_id = run.TenantId, user_id = run.UserId, question = run.Question, status = run.Status,
            total_tokens = run.TotalTokens, estimated_tokens = run.EstimatedTokens
        }));
    }

    private static IResult GetRun(string run_id, ClaimsPrincipal principal, HttpContext context, IRunRepository runs, IWorkOrderRepository orders)
    {
        var user = CurrentUser(principal);
        var run = OwnedRun(runs, user, run_id);
        if (run is null) return NotFound(context, "run not found");
        var steps = runs.Steps(user.TenantId, run_id);
        var workOrder = orders.Latest(user.TenantId, run_id);
        return Results.Json(new
        {
            id = run.Id, tenant_id = run.TenantId, user_id = run.UserId, question = run.Question, status = run.Status,
            total_tokens = run.TotalTokens, estimated_tokens = run.EstimatedTokens,
            steps = steps.Select(step => new
            {
                ordinal = step.Ordinal, agent = step.Agent, tools_used = step.ToolsUsed, evidence_ids = step.EvidenceIds,
                prompt_tokens = step.PromptTokens, completion_tokens = step.CompletionTokens, estimated = step.Estimated,
                duration_ms = step.DurationMs, outcome = step.Outcome, summary = step.Summary
            }),
            work_order = workOrder is null ? null : new { id = workOrder.Id, version = workOrder.Version, status = workOrder.Status, content = workOrder.Content }
        });
    }

    private static IResult ListDocuments(ClaimsPrincipal principal, HttpContext context, IDocumentQueryRepository documents)
    {
        var user = CurrentUser(principal);
        return Results.Json(documents.List(user.TenantId).Select(DocumentPayload));
    }

    private static IResult GetDocument(string doc_id, ClaimsPrincipal principal, HttpContext context, IDocumentQueryRepository documents)
    {
        var user = CurrentUser(principal);
        var document = documents.Get(user.TenantId, doc_id);
        return document is null ? NotFound(context, "document not found") : Results.Json(DocumentPayload(document));
    }

    private static IResult Usage(ClaimsPrincipal principal, HttpContext context, IUsageRepository repository)
    {
        var user = CurrentUser(principal);
        var summary = user.Role == "supervisor" ? repository.ForTenant(user.TenantId) : repository.ForUser(user.TenantId, user.UserId);
        return Results.Json(new
        {
            tenant_id = user.TenantId, role = user.Role,
            daily_asks = summary.DailyAsks.Select(item => new { day = item.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), prompt_tokens = item.PromptTokens, completion_tokens = item.CompletionTokens, ask_count = item.AskCount }),
            daily_runs = summary.DailyRuns.Select(item => new { day = item.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), total_tokens = item.TotalTokens, run_count = item.RunCount })
        });
    }

    private static IResult IngestCorpus(HttpContext context, IServiceProvider services, IOptions<CopilotOptions> options, IWebHostEnvironment environment)
    {
        var user = CurrentUser(context.User);
        if (user.Role != "supervisor") return ApiError(context, StatusCodes.Status403Forbidden, "forbidden: supervisor role required for ingestion");
        var directory = Path.Combine(environment.ContentRootPath, "corpus", user.TenantId);
        if (string.IsNullOrWhiteSpace(directory)) return ApiError(context, StatusCodes.Status503ServiceUnavailable, "ingestion unavailable");
        IEmbedder embedder;
        IDocumentRepository documents;
        IDocumentExtractor extractor;
        try
        {
            embedder = services.GetRequiredService<IEmbedder>();
            documents = services.GetRequiredService<IDocumentRepository>();
            extractor = services.GetRequiredService<IDocumentExtractor>();
        }
        catch (Exception error)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Ingestion")
                .LogError("Corpus ingestion dependencies unavailable ({ErrorType})", error.GetType().Name);
            return ApiError(context, StatusCodes.Status500InternalServerError, "ingestion unavailable");
        }
        return Sse(async (stream, cancellationToken) =>
        {
            await WriteEventForClient(stream, new { @event = "started", tenant_id = user.TenantId }, cancellationToken);
            try
            {
                if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("tenant corpus directory is missing");
                var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Where(path => (Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) &&
                                   new FileInfo(path).Length <= options.Value.MaxIngestFileBytes)
                    .OrderBy(path => Path.GetRelativePath(directory, path).Replace('\\', '/'), StringComparer.Ordinal)
                    .ToArray();
                var results = DocumentIngestion.IngestFiles(files, embedder, documents, extractor);
                foreach (var result in results)
                    await WriteEventForClient(stream, new { @event = "document", path = result.Path, status = result.Status, chunks = result.Chunks, steps = result.Steps, error = result.Error }, cancellationToken);
                await WriteEventForClient(stream, new { @event = "completed", tenant_id = user.TenantId, total_files = results.Count }, cancellationToken);
            }
            catch (ClientDisconnectedException) { }
            catch (Exception) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Ingestion")
                    .LogError("Corpus ingestion failed for tenant {TenantId} ({ErrorType})", user.TenantId, error.GetType().Name);
                await WriteEvent(stream, new { @event = "error", detail = $"{error.GetType().Name}: {error.Message}" }, cancellationToken);
            }
        });
    }

    private static object AskPayload(Answer answer, ImmutableArray<IReadOnlyDictionary<string, object?>> citations,
        bool includeEvidence, string? eventName = null)
    {
        if (!includeEvidence) return new Dictionary<string, object?>
        {
            ["event"] = eventName!, ["text"] = answer.Text, ["citations"] = citations, ["refused"] = answer.Refused,
            ["reason"] = answer.Reason, ["usage"] = UsagePayload(answer)
        };
        return new Dictionary<string, object?>
        {
            ["text"] = answer.Text, ["citations"] = citations, ["refused"] = answer.Refused, ["reason"] = answer.Reason,
            ["usage"] = UsagePayload(answer),
            ["evidence"] = answer.Evidence.Select(item => new Dictionary<string, object?>
            {
                ["chunk_id"] = item.ChunkId, ["doc_id"] = item.DocId, ["title"] = item.Title, ["section"] = item.Section,
                ["revision"] = item.Revision, ["page"] = item.Page, ["text"] = item.Text,
                ["dense_score"] = item.DenseScore, ["keyword_score"] = item.KeywordScore
            }).ToArray()
        };
    }

    private static object UsagePayload(Answer answer) => new Dictionary<string, object?>
    {
        ["prompt_tokens"] = answer.Usage.PromptTokens, ["completion_tokens"] = answer.Usage.CompletionTokens,
        ["total_tokens"] = answer.Usage.TotalTokens, ["estimated"] = answer.Usage.Estimated
    };

    private static ImmutableArray<IReadOnlyDictionary<string, object?>> Citations(Answer answer) => answer.Citations.Select(item =>
        (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["chunk_id"] = item.ChunkId, ["doc_id"] = item.DocId, ["section"] = item.Section,
            ["revision"] = item.Revision, ["page"] = item.Page
        }).ToImmutableArray();

    private static void SaveAsk(IAskLogRepository repository, AuthenticatedUser user, string correlationId, string question,
        Answer answer, ImmutableArray<IReadOnlyDictionary<string, object?>> citations, ILogger logger)
    {
        try
        {
            repository.Save(user.TenantId, new AskLogRecord(null, user.TenantId, user.UserId, correlationId, question,
                answer.Text, answer.Refused, answer.Reason, citations, answer.Usage.PromptTokens,
                answer.Usage.CompletionTokens, answer.Usage.Estimated));
        }
        catch (Exception error) { logger.LogWarning("Could not save ask log entry ({ErrorType})", error.GetType().Name); }
    }

    private static object DocumentPayload(DocumentSummaryRecord document) => new Dictionary<string, object?>
    {
        ["id"] = document.Id, ["doc_id"] = document.DocId, ["title"] = document.Title, ["equipment_id"] = document.EquipmentId,
        ["revision"] = document.Revision, ["status"] = document.Status, ["fingerprint"] = document.Fingerprint,
        ["ingest_error"] = document.IngestError, ["chunk_count"] = document.ChunkCount
    };

    private static RunRecord? OwnedRun(IRunRepository runs, AuthenticatedUser user, string runId)
    {
        var run = runs.Get(user.TenantId, runId);
        return run is null || (user.Role != "supervisor" && run.UserId != user.UserId) ? null : run;
    }

    private static AuthenticatedUser CurrentUser(ClaimsPrincipal principal) => new(
        principal.FindFirstValue("tenant_id") ?? throw new InvalidOperationException("Authenticated tenant is missing."),
        UserId(principal), principal.FindFirstValue(ClaimTypes.Role) ?? "", principal.FindFirstValue(ClaimTypes.Name) ?? "");

    private static long UserId(ClaimsPrincipal principal) => long.Parse(
        principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Authenticated user is missing."),
        NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static string CorrelationId(HttpContext context) => Convert.ToString(context.Items[ApiSecurityMiddleware.CorrelationItem], CultureInfo.InvariantCulture) ?? Guid.NewGuid().ToString("N");
    private static IResult ValidationProblem(HttpContext context, params string[] fields) => Results.Json(new
    {
        error = "validation error", correlation_id = CorrelationId(context),
        detail = fields.Select(field => new { type = "missing", loc = new[] { "body", field }, msg = "Field required" })
    }, statusCode: StatusCodes.Status422UnprocessableEntity);
    private static IResult NotFound(HttpContext context, string error) => ApiError(context, StatusCodes.Status404NotFound, error);
    private static IResult ApiError(HttpContext context, int statusCode, string error) => Results.Json(new
    {
        error, correlation_id = CorrelationId(context)
    }, statusCode: statusCode);

    private static async Task WriteEvent(Stream stream, object value, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        await stream.WriteAsync("data: "u8.ToArray(), cancellationToken);
        await stream.WriteAsync(json, cancellationToken);
        await stream.WriteAsync("\n\n"u8.ToArray(), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task WriteEventForClient(Stream stream, object value, CancellationToken cancellationToken)
    {
        try { await WriteEvent(stream, value, cancellationToken); }
        catch (Exception error) when (cancellationToken.IsCancellationRequested)
        { throw new ClientDisconnectedException(error); }
    }

    private static IResult Sse(Func<Stream, CancellationToken, Task> write, bool noCache = false) =>
        new ServerSentEventsResult(write, noCache);
}

internal sealed class ClientDisconnectedException(Exception innerException) : Exception("SSE client disconnected.", innerException);

internal sealed class ServerSentEventsResult(Func<Stream, CancellationToken, Task> write, bool noCache) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        httpContext.Response.ContentType = "text/event-stream";
        if (noCache) httpContext.Response.Headers.CacheControl = "no-cache";
        await write(httpContext.Response.Body, httpContext.RequestAborted);
    }
}
