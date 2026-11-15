using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DomainCopilot.Api;
using DomainCopilot.Application;
using DomainCopilot.Domain.Documents;
using DomainCopilot.Domain.Llm;
using DomainCopilot.Domain.Rag;
using DomainCopilot.Domain.Workflow;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

var failures = new List<string>();
var userHash = DomainCopilot.Application.Security.ApplicationSecurity.HashPassword("phase5a-test-password");
var users = new ApiUsers(userHash);
var readiness = new ApiReadiness();
var apiDocuments = new ApiDocuments();
var apiRuns = new ApiRuns();
var apiEmbedder = new ApiEmbedder();
var apiSearch = new ApiSearch();
string technicianToken = "";
string supervisorToken = "";
var app = ApiHost.Build([
    "--urls=http://127.0.0.1:0", "--Copilot:AuthSecret=phase5a-auth-secret-long-enough-012345",
    $"--contentRoot={AppContext.BaseDirectory}", "--Copilot:LlmChain=fake", "--Copilot:EmbeddingProvider=fake", "--Copilot:EmbeddingModel=test-model",
    "--Copilot:EmbeddingDimensions=2", "--Copilot:CorsOrigins=http://allowed.test"
], services =>
{
    services.AddLogging(logging => { logging.ClearProviders(); logging.SetMinimumLevel(LogLevel.Warning); });
    services.AddDataProtection().UseEphemeralDataProtectionProvider();
    services.RemoveAll<IUserRepository>(); services.AddSingleton<IUserRepository>(users);
    services.RemoveAll<IRunRepository>(); services.AddSingleton<IRunRepository>(apiRuns);
    services.RemoveAll<IWorkOrderRepository>(); services.AddSingleton<IWorkOrderRepository, ApiOrders>();
    services.RemoveAll<IAuditLog>(); services.AddSingleton<IAuditLog, ApiAudit>();
    services.RemoveAll<ISafetyPrerequisiteRepository>(); services.AddSingleton<ISafetyPrerequisiteRepository, ApiSafety>();
    services.RemoveAll<IAskLogRepository>(); services.AddSingleton<IAskLogRepository, ApiAskLogs>();
    services.RemoveAll<IDocumentRepository>(); services.AddSingleton<IDocumentRepository>(apiDocuments);
    services.RemoveAll<IDocumentQueryRepository>(); services.AddSingleton<IDocumentQueryRepository>(apiDocuments);
    services.RemoveAll<IUsageRepository>(); services.AddSingleton<IUsageRepository, ApiUsage>();
    services.RemoveAll<IChunkSearch>(); services.AddSingleton<IChunkSearch>(apiSearch);
    services.RemoveAll<IEmbedder>(); services.AddSingleton<IEmbedder>(apiEmbedder);
    services.RemoveAll<ILlmProvider>(); services.AddSingleton<ILlmProvider, ApiProvider>();
    services.RemoveAll<IRetrievalSettings>(); services.AddSingleton<IRetrievalSettings>(new ApiRetrievalSettings());
    services.RemoveAll<IDocumentExtractor>(); services.AddSingleton<IDocumentExtractor, ApiExtractor>();
    services.RemoveAll<IDatabaseReadiness>(); services.AddSingleton<IDatabaseReadiness>(readiness);
});

await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
using var client = new HttpClient { BaseAddress = new Uri(address) };

await Check("health, root fallback, shared headers and CORS", async () =>
{
    using var response = await client.GetAsync("/health");
    Equal(HttpStatusCode.OK, response.StatusCode);
    Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    True(response.Headers.Contains("X-Correlation-ID"));
    using var root = await client.GetAsync("/");
    Equal(HttpStatusCode.OK, root.StatusCode);
    Contains("<!DOCTYPE html>", await root.Content.ReadAsStringAsync());
    using var style = await client.GetAsync("/static/style.css");
    Equal(HttpStatusCode.OK, style.StatusCode);
    Contains("color-scheme", await style.Content.ReadAsStringAsync());
    using var script = await client.GetAsync("/static/app.js");
    Equal(HttpStatusCode.OK, script.StatusCode);
    Contains("domain-copilot-session-token", await script.Content.ReadAsStringAsync());
    using var frontendApi = await client.GetAsync("/static/frontend-api.js");
    Equal(HttpStatusCode.OK, frontendApi.StatusCode);
    Contains("readSse", await frontendApi.Content.ReadAsStringAsync());
    using var preflight = new HttpRequestMessage(HttpMethod.Options, "/health");
    preflight.Headers.Add("Origin", "http://allowed.test");
    preflight.Headers.Add("Access-Control-Request-Method", "POST");
    using var cors = await client.SendAsync(preflight);
    Equal("http://allowed.test", cors.Headers.GetValues("Access-Control-Allow-Origin").Single());
});

await Check("readiness fails safely without a configured database", async () =>
{
    readiness.Available = false;
    using var response = await client.GetAsync("/ready");
    Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    Contains("database unavailable", await response.Content.ReadAsStringAsync());
    readiness.Available = true;
    using var ready = await client.GetAsync("/ready");
    Equal(HttpStatusCode.OK, ready.StatusCode);
    Contains("ready", await ready.Content.ReadAsStringAsync());
});

await Check("body-size limit and login throttle", async () =>
{
    using var tooLarge = await client.PostAsync("/ask", new StringContent(new string('x', 1_048_577)));
    Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
});

await Check("Python token login, bearer authentication and reloaded role", async () =>
{
    using var login = await client.PostAsJsonAsync("/auth/login", new { tenant_id = "tenant-a", username = " tech ", password = "phase5a-test-password" });
    Equal(HttpStatusCode.OK, login.StatusCode);
    using var profile = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
    technicianToken = profile.RootElement.GetProperty("access_token").GetString()!;
    Equal("bearer", profile.RootElement.GetProperty("token_type").GetString());
    using var meRequest = Authorized(HttpMethod.Get, "/me", technicianToken);
    using var me = await client.SendAsync(meRequest);
    Equal(HttpStatusCode.OK, me.StatusCode);
    using var meJson = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
    Equal("tenant-a", meJson.RootElement.GetProperty("tenant_id").GetString());
    Equal("technician", meJson.RootElement.GetProperty("role").GetString());
    using var invalid = Authorized(HttpMethod.Get, "/me", "not-a-python-token");
    using var rejected = await client.SendAsync(invalid);
    Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
    using var missing = await client.GetAsync("/me");
    Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
    using var supervisorLogin = await client.PostAsJsonAsync("/auth/login", new { tenant_id = "tenant-a", username = "supervisor", password = "phase5a-test-password" });
    using var supervisorJson = JsonDocument.Parse(await supervisorLogin.Content.ReadAsStringAsync());
    supervisorToken = supervisorJson.RootElement.GetProperty("access_token").GetString()!;
});

await Check("HTTP error envelopes preserve exact fields and correlation ids", async () =>
{
    const string unauthorizedCorrelation = "test-unauthorized-correlation";
    using var missingAuth = new HttpRequestMessage(HttpMethod.Get, "/me");
    missingAuth.Headers.Add("X-Correlation-ID", unauthorizedCorrelation);
    using var unauthorized = await client.SendAsync(missingAuth);
    Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    AssertError(await unauthorized.Content.ReadAsStringAsync(), "unauthorized", unauthorizedCorrelation);

    const string validationCorrelation = "test-validation-correlation";
    using var missingQuestion = Authorized(HttpMethod.Post, "/ask", technicianToken);
    missingQuestion.Headers.Add("X-Correlation-ID", validationCorrelation);
    missingQuestion.Content = JsonContent.Create(new { });
    using var validation = await client.SendAsync(missingQuestion);
    Equal(HttpStatusCode.UnprocessableEntity, validation.StatusCode);
    using (var validationJson = JsonDocument.Parse(await validation.Content.ReadAsStringAsync()))
    {
        var root = validationJson.RootElement;
        Equal("validation error", root.GetProperty("error").GetString());
        Equal(validationCorrelation, root.GetProperty("correlation_id").GetString());
        Equal("question", root.GetProperty("detail")[0].GetProperty("loc")[1].GetString());
        Equal(3, root.EnumerateObject().Count());
    }

    const string notFoundCorrelation = "test-not-found-correlation";
    using var missingDoc = Authorized(HttpMethod.Get, "/documents/missing", technicianToken);
    missingDoc.Headers.Add("X-Correlation-ID", notFoundCorrelation);
    using var notFound = await client.SendAsync(missingDoc);
    Equal(HttpStatusCode.NotFound, notFound.StatusCode);
    AssertError(await notFound.Content.ReadAsStringAsync(), "document not found", notFoundCorrelation);

    const string unmatchedCorrelation = "test-unmatched-correlation";
    using var unmatchedRequest = new HttpRequestMessage(HttpMethod.Get, "/not-an-api-route");
    unmatchedRequest.Headers.Add("X-Correlation-ID", unmatchedCorrelation);
    using var unmatched = await client.SendAsync(unmatchedRequest);
    Equal(HttpStatusCode.NotFound, unmatched.StatusCode);
    AssertError(await unmatched.Content.ReadAsStringAsync(), "not found", unmatchedCorrelation);
});

await Check("read APIs preserve tenant/user projections", async () =>
{
    using var docsRequest = Authorized(HttpMethod.Get, "/documents", technicianToken);
    using var docs = await client.SendAsync(docsRequest);
    Equal(HttpStatusCode.OK, docs.StatusCode);
    Contains("chunk_count", await docs.Content.ReadAsStringAsync());
    using var docRequest = Authorized(HttpMethod.Get, "/documents/MAN-1", technicianToken);
    using var doc = await client.SendAsync(docRequest);
    Equal(HttpStatusCode.OK, doc.StatusCode);
    using var usageRequest = Authorized(HttpMethod.Get, "/usage", technicianToken);
    using var usage = await client.SendAsync(usageRequest);
    Contains("daily_asks", await usage.Content.ReadAsStringAsync());
    using var asksRequest = Authorized(HttpMethod.Get, "/asks", technicianToken);
    using var asks = await client.SendAsync(asksRequest);
    Equal(HttpStatusCode.OK, asks.StatusCode);
});

await Check("cross-tenant API reads and supervisor scope are isolated", async () =>
{
    var betaToken = DomainCopilot.Application.Security.ApplicationSecurity.CreateToken("tenant-b", 9, "technician",
        "phase5a-auth-secret-long-enough-012345", 3600);
    var betaSupervisorToken = DomainCopilot.Application.Security.ApplicationSecurity.CreateToken("tenant-b", 10, "supervisor",
        "phase5a-auth-secret-long-enough-012345", 3600);

    using var betaMeRequest = Authorized(HttpMethod.Get, "/me", betaToken);
    using var betaMe = await client.SendAsync(betaMeRequest);
    using var betaMeJson = JsonDocument.Parse(await betaMe.Content.ReadAsStringAsync());
    Equal("tenant-b", betaMeJson.RootElement.GetProperty("tenant_id").GetString());

    using var alphaDocsRequest = Authorized(HttpMethod.Get, "/documents", technicianToken);
    using var alphaDocs = await client.SendAsync(alphaDocsRequest);
    var alphaDocsBody = await alphaDocs.Content.ReadAsStringAsync();
    Contains("MAN-1", alphaDocsBody);
    True(!alphaDocsBody.Contains("B-MAN", StringComparison.Ordinal));
    using var betaDocsRequest = Authorized(HttpMethod.Get, "/documents", betaToken);
    using var betaDocs = await client.SendAsync(betaDocsRequest);
    Contains("B-MAN", await betaDocs.Content.ReadAsStringAsync());
    using var crossDocRequest = Authorized(HttpMethod.Get, "/documents/B-MAN", technicianToken);
    using var crossDoc = await client.SendAsync(crossDocRequest);
    Equal(HttpStatusCode.NotFound, crossDoc.StatusCode);

    using var alphaUsageRequest = Authorized(HttpMethod.Get, "/usage", technicianToken);
    using var alphaUsage = await client.SendAsync(alphaUsageRequest);
    using var alphaUsageJson = JsonDocument.Parse(await alphaUsage.Content.ReadAsStringAsync());
    Equal("tenant-a", alphaUsageJson.RootElement.GetProperty("tenant_id").GetString());
    Equal(1L, alphaUsageJson.RootElement.GetProperty("daily_asks")[0].GetProperty("ask_count").GetInt64());
    using var betaUsageRequest = Authorized(HttpMethod.Get, "/usage", betaToken);
    using var betaUsage = await client.SendAsync(betaUsageRequest);
    using var betaUsageJson = JsonDocument.Parse(await betaUsage.Content.ReadAsStringAsync());
    Equal("tenant-b", betaUsageJson.RootElement.GetProperty("tenant_id").GetString());
    Equal(9L, betaUsageJson.RootElement.GetProperty("daily_asks")[0].GetProperty("ask_count").GetInt64());

    using var alphaAsksRequest = Authorized(HttpMethod.Get, "/asks", technicianToken);
    using var alphaAsks = await client.SendAsync(alphaAsksRequest);
    var alphaAskBody = await alphaAsks.Content.ReadAsStringAsync();
    Contains("alpha question", alphaAskBody);
    True(!alphaAskBody.Contains("beta secret question", StringComparison.Ordinal));
    using var betaAsksRequest = Authorized(HttpMethod.Get, "/asks", betaToken);
    using var betaAsks = await client.SendAsync(betaAsksRequest);
    Contains("beta secret question", await betaAsks.Content.ReadAsStringAsync());

    using var alphaRunListRequest = Authorized(HttpMethod.Get, "/runs", technicianToken);
    using var alphaRunList = await client.SendAsync(alphaRunListRequest);
    var alphaRunBody = await alphaRunList.Content.ReadAsStringAsync();
    Contains("run-own", alphaRunBody);
    True(!alphaRunBody.Contains("run-beta-own", StringComparison.Ordinal));
    using var foreignRunRequest = Authorized(HttpMethod.Get, "/runs/run-beta-own", technicianToken);
    using var foreignRun = await client.SendAsync(foreignRunRequest);
    Equal(HttpStatusCode.NotFound, foreignRun.StatusCode);
    using var alphaSupervisorForeignRequest = Authorized(HttpMethod.Get, "/runs/run-beta-own", supervisorToken);
    using var alphaSupervisorForeign = await client.SendAsync(alphaSupervisorForeignRequest);
    Equal(HttpStatusCode.NotFound, alphaSupervisorForeign.StatusCode);
    using var alphaSupervisorSameTenantRequest = Authorized(HttpMethod.Get, "/runs/run-other", supervisorToken);
    using var alphaSupervisorSameTenant = await client.SendAsync(alphaSupervisorSameTenantRequest);
    Equal(HttpStatusCode.OK, alphaSupervisorSameTenant.StatusCode);
    using var betaSupervisorRequest = Authorized(HttpMethod.Get, "/runs/run-beta-own", betaSupervisorToken);
    using var betaSupervisor = await client.SendAsync(betaSupervisorRequest);
    Equal(HttpStatusCode.OK, betaSupervisor.StatusCode);
});

await Check("authentication rejects invalid tokens and reloads current user state", async () =>
{
    const string secret = "phase5a-auth-secret-long-enough-012345";
    var expired = DomainCopilot.Application.Security.ApplicationSecurity.CreateToken("tenant-a", 7, "technician", secret, -5);
    var wrongSecret = DomainCopilot.Application.Security.ApplicationSecurity.CreateToken("tenant-a", 7, "technician",
        "a-different-secret-that-is-long-enough-123", 3600);
    var tampered = technicianToken[..^1] + (technicianToken[^1] == 'A' ? 'B' : 'A');
    foreach (var invalidToken in new[] { expired, tampered, wrongSecret, "malformed-token" })
    {
        using var invalidRequest = Authorized(HttpMethod.Get, "/me", invalidToken);
        const string correlationId = "invalid-token-correlation";
        invalidRequest.Headers.Add("X-Correlation-ID", correlationId);
        using var invalid = await client.SendAsync(invalidRequest);
        Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        AssertError(await invalid.Content.ReadAsStringAsync(), "unauthorized", correlationId);
    }

    users.SetRole("tenant-a", 7, "supervisor");
    using var changedRoleRequest = Authorized(HttpMethod.Get, "/me", technicianToken);
    using var changedRole = await client.SendAsync(changedRoleRequest);
    using var changedRoleJson = JsonDocument.Parse(await changedRole.Content.ReadAsStringAsync());
    Equal("supervisor", changedRoleJson.RootElement.GetProperty("role").GetString());
    users.SetRole("tenant-a", 7, "technician");

    users.SetActive("tenant-a", 7, false);
    using var inactiveRequest = Authorized(HttpMethod.Get, "/me", technicianToken);
    using var inactive = await client.SendAsync(inactiveRequest);
    Equal(HttpStatusCode.Unauthorized, inactive.StatusCode);
    users.SetActive("tenant-a", 7, true);
});

await Check("database exceptions map to safe generic responses", async () =>
{
    apiDocuments.Fail = true;
    using var request = Authorized(HttpMethod.Get, "/documents", technicianToken);
    using var response = await client.SendAsync(request);
    Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    var body = await response.Content.ReadAsStringAsync();
    Contains("database service unavailable", body);
    True(!body.Contains("test connection detail", StringComparison.Ordinal));
    apiDocuments.Fail = false;

    const string correlationId = "unexpected-failure-correlation";
    using var failingRequest = Authorized(HttpMethod.Get, "/documents", technicianToken);
    failingRequest.Headers.Add("X-Correlation-ID", correlationId);
    apiDocuments.UnexpectedFail = true;
    using var failed = await client.SendAsync(failingRequest);
    Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
    AssertError(await failed.Content.ReadAsStringAsync(), "internal error", correlationId);
    apiDocuments.UnexpectedFail = false;
});

await Check("ask JSON and SSE contracts", async () =>
{
    using var malformed = Authorized(HttpMethod.Post, "/ask", technicianToken);
    malformed.Headers.Add("X-Correlation-ID", "malformed-json-correlation");
    malformed.Content = new StringContent("{bad", Encoding.UTF8, "application/json");
    using var malformedResponse = await client.SendAsync(malformed);
    Equal(HttpStatusCode.UnprocessableEntity, malformedResponse.StatusCode);
    using (var malformedJson = JsonDocument.Parse(await malformedResponse.Content.ReadAsStringAsync()))
    {
        Equal(1, malformedJson.RootElement.EnumerateObject().Count());
        Equal("json_invalid", malformedJson.RootElement.GetProperty("detail")[0].GetProperty("type").GetString());
        Equal("Invalid JSON body", malformedJson.RootElement.GetProperty("detail")[0].GetProperty("msg").GetString());
    }
    Equal("malformed-json-correlation", malformedResponse.Headers.GetValues("X-Correlation-ID").Single());
    using var askRequest = Authorized(HttpMethod.Post, "/ask", technicianToken);
    askRequest.Content = JsonContent.Create(new { question = "" });
    using var ask = await client.SendAsync(askRequest);
    Equal(HttpStatusCode.OK, ask.StatusCode);
    Contains("citations", await ask.Content.ReadAsStringAsync());
    using var streamRequest = Authorized(HttpMethod.Post, "/ask/stream", technicianToken);
    streamRequest.Content = JsonContent.Create(new { question = "" });
    using var stream = await client.SendAsync(streamRequest);
    Equal("text/event-stream", stream.Content.Headers.ContentType!.MediaType);
    Contains("\"event\":\"done\"", await stream.Content.ReadAsStringAsync());
});

await Check("run ownership, cancel conflicts, approval and SSE", async () =>
{
    using var listRequest = Authorized(HttpMethod.Get, "/runs", technicianToken);
    using var list = await client.SendAsync(listRequest);
    Equal(HttpStatusCode.OK, list.StatusCode);
    using var own = Authorized(HttpMethod.Get, "/runs/run-own", technicianToken);
    using var ownResponse = await client.SendAsync(own);
    Equal(HttpStatusCode.OK, ownResponse.StatusCode);
    using var foreign = Authorized(HttpMethod.Get, "/runs/run-other", technicianToken);
    using var hidden = await client.SendAsync(foreign);
    Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    using var acknowledge = Authorized(HttpMethod.Post, "/runs/run-own/acknowledge", technicianToken);
    acknowledge.Content = JsonContent.Create(new { step_ids = Array.Empty<long>() });
    using var generated = await client.SendAsync(acknowledge);
    Equal("text/event-stream", generated.Content.Headers.ContentType!.MediaType);
    Contains("Work order generation failed", await generated.Content.ReadAsStringAsync());
    using var approveTech = Authorized(HttpMethod.Post, "/runs/run-pending/approval", technicianToken);
    approveTech.Headers.Add("X-Correlation-ID", "forbidden-correlation");
    approveTech.Content = JsonContent.Create(new { decision = "approve" });
    using var forbidden = await client.SendAsync(approveTech);
    Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    AssertError(await forbidden.Content.ReadAsStringAsync(), "forbidden: technician cannot approve", "forbidden-correlation");
    using var approveSupervisor = Authorized(HttpMethod.Post, "/runs/run-pending/approval", supervisorToken);
    approveSupervisor.Content = JsonContent.Create(new { decision = "approve" });
    using var approved = await client.SendAsync(approveSupervisor);
    Equal(HttpStatusCode.OK, approved.StatusCode);
    using var cancel = Authorized(HttpMethod.Post, "/runs/run-own/cancel", technicianToken);
    using var cancelled = await client.SendAsync(cancel);
    Equal(HttpStatusCode.OK, cancelled.StatusCode);
    using var cancelAgain = Authorized(HttpMethod.Post, "/runs/run-own/cancel", technicianToken);
    cancelAgain.Headers.Add("X-Correlation-ID", "conflict-correlation");
    using var conflict = await client.SendAsync(cancelAgain);
    Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    AssertError(await conflict.Content.ReadAsStringAsync(), "run cannot be cancelled", "conflict-correlation");
    using var start = Authorized(HttpMethod.Post, "/runs", technicianToken);
    start.Content = JsonContent.Create(new { symptoms = "" });
    using var started = await client.SendAsync(start);
    Contains("event\":\"status", await started.Content.ReadAsStringAsync());
    Contains("Describe the equipment and maintenance symptoms", await started.Content.ReadAsStringAsync());
});

await Check("run SSE disconnect does not mark the workflow failed while real failure is persisted", async () =>
{
    apiSearch.ReturnEvidence = true;
    apiEmbedder.BlockNext();
    using (var cancellation = new CancellationTokenSource())
    using (var request = Authorized(HttpMethod.Post, "/runs", technicianToken))
    {
        request.Content = JsonContent.Create(new { symptoms = "pump fault" });
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var firstEvent = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Contains("data:", firstEvent ?? "");
        True(apiEmbedder.Entered.Wait(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();
        apiEmbedder.Release.Set();
        await Task.Delay(300);
        var runId = apiRuns.LastCreatedRunId ?? throw new Exception("expected a created run");
        Equal("awaiting_acknowledgement", apiRuns.Get("tenant-a", runId)?.Status);
    }

    apiSearch.ReturnEvidence = false;
    apiSearch.ThrowSearch = true;
    using var failedRequest = Authorized(HttpMethod.Post, "/runs", technicianToken);
    failedRequest.Content = JsonContent.Create(new { symptoms = "genuine provider failure" });
    using var failedResponse = await client.SendAsync(failedRequest);
    var failedEvents = await failedResponse.Content.ReadAsStringAsync();
    Contains("event", failedEvents);
    Contains("workflow failed", failedEvents);
    var failedRunId = apiRuns.LastCreatedRunId ?? throw new Exception("expected a failed run");
    Equal("failed", apiRuns.Get("tenant-a", failedRunId)?.Status);
    apiSearch.ThrowSearch = false;
});

await Check("ingestion requires supervisor before resolving provider/database services", async () =>
{
    using var technicianRequest = Authorized(HttpMethod.Post, "/ingest/corpus", technicianToken);
    using var denied = await client.SendAsync(technicianRequest);
    Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    using var supervisorRequest = Authorized(HttpMethod.Post, "/ingest/corpus", supervisorToken);
    using var accepted = await client.SendAsync(supervisorRequest);
    Equal("text/event-stream", accepted.Content.Headers.ContentType!.MediaType);
});

await Check("login throttle", async () =>
{
    for (var i = 0; i < 3; i++)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login") { Content = JsonContent.Create(new { }) };
        using var response = await client.SendAsync(request);
        True(response.StatusCode != HttpStatusCode.TooManyRequests);
    }
    using var limited = await client.PostAsJsonAsync("/auth/login", new { tenant_id = "tenant-a", username = "tech", password = "bad" });
    Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    Equal("60", limited.Headers.GetValues("Retry-After").Single());
});

await app.StopAsync();
await app.DisposeAsync();
Console.WriteLine(failures.Count == 0 ? "API tests passed." : $"API tests failed: {failures.Count}");
if (failures.Count > 0) Environment.ExitCode = 1;

async Task Check(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failures.Add(name); Console.WriteLine($"FAIL {name}: {error.GetType().Name}: {error.Message}"); }
}

static HttpRequestMessage Authorized(HttpMethod method, string path, string token)
{
    var request = new HttpRequestMessage(method, path);
    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    return request;
}
static void True(bool condition) { if (!condition) throw new Exception("expected true"); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected {expected}, got {actual}"); }
static void Contains(string expected, string actual) { if (!actual.Contains(expected, StringComparison.Ordinal)) throw new Exception($"expected text containing {expected}"); }
static void AssertError(string json, string expectedError, string expectedCorrelation)
{
    using var document = JsonDocument.Parse(json);
    var root = document.RootElement;
    Equal(2, root.EnumerateObject().Count());
    Equal(expectedError, root.GetProperty("error").GetString());
    Equal(expectedCorrelation, root.GetProperty("correlation_id").GetString());
}

sealed class ApiUsers(string passwordHash) : IUserRepository
{
    private readonly Dictionary<(string Tenant, long Id), UserRecord> _users = new()
    {
        [("tenant-a", 7)] = new(7, "tenant-a", "tech", "technician", true),
        [("tenant-a", 8)] = new(8, "tenant-a", "supervisor", "supervisor", true),
        [("tenant-b", 9)] = new(9, "tenant-b", "beta-tech", "technician", true),
        [("tenant-b", 10)] = new(10, "tenant-b", "beta-supervisor", "supervisor", true)
    };
    public UserRecord? GetByUsername(string tenantId, string username) => _users.Values
        .FirstOrDefault(user => user.TenantId == tenantId && user.Username == username) is { } user
        ? user with { PasswordHash = passwordHash } : null;
    public UserRecord? Get(string tenantId, long userId) => _users.GetValueOrDefault((tenantId, userId));
    public void SetRole(string tenantId, long userId, string role) => _users[(tenantId, userId)] = _users[(tenantId, userId)] with { Role = role };
    public void SetActive(string tenantId, long userId, bool active) => _users[(tenantId, userId)] = _users[(tenantId, userId)] with { Active = active };
}

sealed class ApiRuns : IRunRepository
{
    private readonly Dictionary<string, RunRecord> _runs = new(StringComparer.Ordinal)
    {
        ["run-own"] = new("run-own", "tenant-a", 7, "pump issue", "pending_approval", 10, false),
        ["run-other"] = new("run-other", "tenant-a", 99, "other user's issue", "pending_approval", 5, false),
        ["run-pending"] = new("run-pending", "tenant-a", 7, "pump issue", "pending_approval", 10, false),
        ["run-beta-own"] = new("run-beta-own", "tenant-b", 9, "beta pump issue", "pending_approval", 10, false),
        ["run-beta-other"] = new("run-beta-other", "tenant-b", 11, "other beta issue", "pending_approval", 5, false)
    };
    private readonly Dictionary<string, List<RunStepRecord>> _steps = [];
    private int _nextId;
    public string? LastCreatedRunId { get; private set; }
    public string Create(string tenantId, long userId, string question) { var id = $"run-created-{++_nextId}"; LastCreatedRunId = id; _runs[id] = new(id, tenantId, userId, question, "running", 0, false); return id; }
    public RunRecord? Get(string tenantId, string runId) => _runs.TryGetValue(runId, out var row) && row.TenantId == tenantId ? row : null;
    public void SetStatus(string tenantId, string runId, string status, int? totalTokens = null, bool? estimatedTokens = null, bool finished = false)
    { if (Get(tenantId, runId) is { } row) _runs[runId] = row with { Status = status, TotalTokens = totalTokens ?? row.TotalTokens, EstimatedTokens = estimatedTokens ?? row.EstimatedTokens }; }
    public void AddStep(string tenantId, string runId, RunStepRecord step) { if (!_steps.TryGetValue(runId, out var items)) _steps[runId] = items = []; items.Add(step); }
    public IReadOnlyList<RunStepRecord> Steps(string tenantId, string runId) => _steps.GetValueOrDefault(runId) ?? [];
    public IReadOnlyList<RunRecord> ListForUser(string tenantId, long userId) => _runs.Values.Where(x => x.TenantId == tenantId && x.UserId == userId).ToArray();
    public IReadOnlyList<RunRecord> ListForTenant(string tenantId, string? status = null) => _runs.Values.Where(x => x.TenantId == tenantId && (status is null || x.Status == status)).ToArray();
}

sealed class ApiOrders : IWorkOrderRepository
{
    private readonly Dictionary<string, WorkOrderRecord> _items = new(StringComparer.Ordinal)
    { ["run-pending"] = new("wo-1", "run-pending", 1, "draft", new Dictionary<string, object?> { ["title"] = "Draft" }) };
    public IReadOnlyDictionary<string, object?> CreateDraft(string tenantId, string runId, IReadOnlyDictionary<string, object?> content)
    { _items[runId] = new("wo-new", runId, 1, "draft", content); return content; }
    public WorkOrderRecord? Latest(string tenantId, string runId) => _items.GetValueOrDefault(runId);
    public void SetStatus(string tenantId, string workOrderId, string status)
    { foreach (var key in _items.Keys.ToArray()) if (_items[key].Id == workOrderId) { var item = _items[key]; _items[key] = new WorkOrderRecord(item.Id, item.RunId, item.Version, status, item.Content); } }
}
sealed class ApiAudit : IAuditLog { public void Record(string tenantId, long? actorUserId, string action, string subjectType, string? subjectId, IReadOnlyDictionary<string, object?>? detail = null) { } }
sealed class ApiSafety : ISafetyPrerequisiteRepository
{
    public IReadOnlyList<SafetyStepRecord> RequiredForDocuments(string tenantId, IReadOnlyList<string> docIds) => [];
    public IReadOnlyList<SafetyStepRecord> RequiredByIds(string tenantId, IReadOnlyList<long> stepIds) => [];
}
sealed class ApiAskLogs : IAskLogRepository
{
    public long Save(string tenantId, AskLogRecord record) => 1;
    public IReadOnlyList<AskLogRecord> ListForUser(string tenantId, long userId) => tenantId == "tenant-a" && userId == 7
        ? [new AskLogRecord(1, tenantId, userId, "alpha-cid", "alpha question", "alpha answer", false, null, [], 1, 2, true)]
        : tenantId == "tenant-b" && userId == 9
            ? [new AskLogRecord(2, tenantId, userId, "beta-cid", "beta secret question", "beta answer", false, null, [], 3, 4, false)] : [];
}
sealed class ApiDocuments : IDocumentRepository, IDocumentQueryRepository
{
    public bool Fail { get; set; }
    public bool UnexpectedFail { get; set; }
    private static DocumentSummaryRecord Alpha => new("doc-a", "MAN-1", "Alpha manual", "P-1", "A", "current", "hash-a", null, 2);
    private static DocumentSummaryRecord Beta => new("doc-b", "B-MAN", "Beta manual", "P-2", "B", "current", "hash-b", null, 4);
    public string? StoredFingerprint(DocumentMeta meta) => null;
    public void ReplaceDocument(DocumentMeta meta, IReadOnlyList<Chunk> chunks, IReadOnlyList<IReadOnlyList<double>> embeddings, IReadOnlyList<SafetyStep> steps, string embeddingModel) { }
    public void MarkFailed(DocumentMeta meta, string error) { }
    public IReadOnlyList<DocumentSummaryRecord> List(string tenantId) { FailIfConfigured(); return tenantId switch { "tenant-a" => [Alpha], "tenant-b" => [Beta], _ => [] }; }
    public DocumentSummaryRecord? Get(string tenantId, string idOrDocId) { FailIfConfigured(); return tenantId switch { "tenant-a" when idOrDocId is "MAN-1" or "doc-a" => Alpha, "tenant-b" when idOrDocId is "B-MAN" or "doc-b" => Beta, _ => null }; }
    private void FailIfConfigured()
    {
        if (Fail) throw new NpgsqlException("test connection detail");
        if (UnexpectedFail) throw new InvalidOperationException("secret-like unexpected detail");
    }
}
sealed class ApiUsage : IUsageRepository
{
    public UsageSummary ForTenant(string tenantId) => new([new DailyAskUsage(new DateOnly(2026, 1, 2), 20, 30, 2)], [new DailyRunUsage(new DateOnly(2026, 1, 2), 40, 2)]);
    public UsageSummary ForUser(string tenantId, long userId) => new([new DailyAskUsage(new DateOnly(2026, 1, 2), userId == 9 ? 9 : 1, 3, userId == 9 ? 9 : 1)], [new DailyRunUsage(new DateOnly(2026, 1, 2), userId == 9 ? 90 : 4, userId == 9 ? 9 : 1)]);
}
sealed class ApiSearch : IChunkSearch
{
    public bool ThrowSearch { get; set; }
    public bool ReturnEvidence { get; set; }
    public IReadOnlyList<Evidence> DenseSearch(string tenantId, IReadOnlyList<double> vector, string model, bool includeSuperseded, int limit)
    {
        if (ThrowSearch) throw new InvalidOperationException("synthetic workflow failure");
        return ReturnEvidence ? [new Evidence(1, "MAN-1", "Pump Manual", "Hydraulic Pump", "A", "current", 1,
            "pump hydraulic fault", DenseScore: .95, FusedScore: .95, Equipment: "P-1")] : [];
    }
    public IReadOnlyList<Evidence> KeywordSearch(string tenantId, IReadOnlyList<string> terms, string model, bool includeSuperseded, int limit) => [];
}
sealed class ApiEmbedder : IEmbedder
{
    private volatile bool _blockNext;
    public ManualResetEventSlim Entered { get; } = new(false);
    public ManualResetEventSlim Release { get; } = new(false);
    public string ModelName => "test-model"; public int Dimension => 2;
    public void BlockNext() { Entered.Reset(); Release.Reset(); _blockNext = true; }
    public IReadOnlyList<IReadOnlyList<double>> Embed(IReadOnlyList<string> texts)
    {
        if (_blockNext)
        {
            _blockNext = false;
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test embedding gate timed out");
        }
        return texts.Select(_ => (IReadOnlyList<double>)new[] { 1d, 0d }).ToArray();
    }
}
sealed class ApiProvider : ILlmProvider
{
    public string Name => "fake";
    public CompletionResult Complete(CompletionRequest request) => new("ok", [], new TokenUsage(), Name, "fake");
    public IEnumerable<StreamEvent> Stream(CompletionRequest request, CancellationToken cancellationToken = default) { yield return new("token", "ok"); }
    public EmbeddingResult Embed(IReadOnlyList<string> texts) => new(texts.Select(_ => ImmutableArray.Create(1d, 0d)).ToImmutableArray(), "test-model", 2, new TokenUsage(), Name);
}
sealed class ApiRetrievalSettings : IRetrievalSettings { public double RetrievalMinSimilarity => .55; }
sealed class ApiExtractor : IDocumentExtractor { public ExtractedDocument Extract(string path) => throw new ExtractionError("test fixture extractor"); }
sealed class ApiReadiness : IDatabaseReadiness
{
    public bool Available { get; set; }
    public ValueTask CheckAsync(CancellationToken cancellationToken = default) => Available ? ValueTask.CompletedTask : ValueTask.FromException(new InvalidOperationException("database not configured"));
}
