using System.Collections.Immutable;
using DomainCopilot.Application;
using DomainCopilot.Application.Agents;
using DomainCopilot.Application.Answering;
using DomainCopilot.Application.Embedding;
using DomainCopilot.Application.Llm;
using DomainCopilot.Application.Redaction;
using DomainCopilot.Application.Retrieval;
using DomainCopilot.Application.Security;
using DomainCopilot.Application.Text;
using DomainCopilot.Application.Tools;
using DomainCopilot.Application.Workflow;
using DomainCopilot.Domain.Documents;
using DomainCopilot.Domain.Llm;
using DomainCopilot.Domain.Rag;
using DomainCopilot.Domain.Workflow;

var tests = new (string Name, Action Run)[]
{
    ("text cleaning and chunk boundaries", TextBehavior),
    ("numbered safety step extraction", SafetyStepBehavior),
    ("retrieval terms, fusion and tenant search", RetrievalBehavior),
    ("embedding spec and vector guard", EmbeddingGuardBehavior),
    ("LLM fallback and stream behavior", FallbackBehavior),
    ("fallback stream initialization follows provider failure rules", FallbackStreamBehavior),
    ("nested PII redaction", RedactionBehavior),
    ("tool schemas and role gates", ToolBehavior),
    ("application records keep structural collection equality", RecordEqualityBehavior),
    ("revision, safety gate and transitions", WorkflowRulesBehavior),
    ("scrypt and signed token compatibility", SecurityBehavior),
    ("login uses active tenant-scoped users", AuthenticationBehavior),
    ("answer refusal before model call", AnsweringBehavior),
    ("work order repair and degraded fallback", AgentBehavior),
    ("workflow orchestration start records steps and events", OrchestrationBehavior)
};
var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception error) { failed++; Console.WriteLine($"FAIL {test.Name}: {error.GetType().Name}: {error.Message}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} Application tests passed.");
return failed == 0 ? 0 : 1;

static void TextBehavior()
{
    Equal(" a\n\n b", CleanText.Clean(" a \r\n\r\n\n b\u00a0\n"));
    Equal("strasse", PythonString.CaseFold("Straße"));
    var doc = new ExtractedDocument(new DocumentMeta("t", "d", "Manual", "A", "current", "md", "x", "hash"),
        [new Section("Body", "a\n\nbb"), new Section("Safety prerequisites", "one\n\ntwo")]);
    var chunks = DocumentChunker.Chunk(doc, 2);
    Equal(3, chunks.Count); Equal(0, chunks[0].Ordinal); Equal("Manual | Body | A", chunks[0].Context);
    Equal(1, DocumentChunker.Chunk(new ExtractedDocument(doc.Meta, [new Section("Body", "😀\n\nx")]), 4).Count);
    Equal(1, DocumentChunker.Chunk(new ExtractedDocument(doc.Meta, [new Section("Safety prerequisites", "one\n\ntwo")]), 1).Count);
}

static void SafetyStepBehavior()
{
    var doc = new ExtractedDocument(new DocumentMeta("t", "d", "M", "A", "current", "md", "x", "h"),
        [new Section("Other", "1. ignored"), new Section("Safety prerequisites", "Intro\n\n1. Isolate power\n verify zero\n\nclosing")]);
    var steps = SafetyStepExtractor.Extract(doc);
    Equal(1, steps.Count); Equal(1, steps[0].StepNo); Equal("Isolate power verify zero", steps[0].Text);
    var maxStep = SafetyStepExtractor.Extract(new ExtractedDocument(doc.Meta,
        [new Section("Safety prerequisites", "2147483647. Maximum") ]));
    Equal(int.MaxValue, maxStep[0].StepNo);
    Throws<OverflowException>(() => SafetyStepExtractor.Extract(new ExtractedDocument(doc.Meta,
        [new Section("Safety prerequisites", "2147483648. Outside PostgreSQL INTEGER")])));
    Throws<ExtractionError>(() => SafetyStepExtractor.Extract(new ExtractedDocument(doc.Meta,
        [new Section("Safety prerequisites", "1. one\n\n1. two")])));
}

static void RetrievalBehavior()
{
    Equal("300-h,interval,oil,rev,the,what", string.Join(',', HybridRetrieval.KeywordTerms("Rev A: what is the 300-h oil interval?")));
    True(HybridRetrieval.WantsSupersededRevisions("what changed between Rev A and Rev B"));
    var one = Ev(1, dense: .8); var two = Ev(2, dense: .7);
    var fused = HybridRetrieval.ReciprocalRankFuse([[one, two], [two with { KeywordScore = .5 }]]);
    Equal(2, fused[0].ChunkId); True(fused[0].FusedScore > fused[1].FusedScore);
    var search = new FakeSearch([one], [two]);
    var hits = HybridRetrieval.Retrieve("pump interval", "tenant-a", new FakeEmbedder(), search, new Settings());
    Equal(1, hits.Count); Equal("tenant-a", search.Tenant); Equal(20, search.Limit);
}

static void EmbeddingGuardBehavior()
{
    var registry = new FakeRegistry(); var guard = new EmbeddingGuard(registry); var spec = new EmbeddingSpec("model", 2);
    guard.Ensure(spec); Equal(spec, registry.Spec);
    guard.Verify(new EmbeddingResult([ImmutableArray.Create(1d, 2d)], "model", 2, new TokenUsage(), "provider"));
    Throws<EmbeddingModelMismatchError>(() => guard.Verify(new EmbeddingResult([ImmutableArray.Create(1d)], "model", 2, new TokenUsage(), "provider")));
}

static void FallbackBehavior()
{
    var first = new FakeProvider("first", error: new QuotaExceededError("first", "quota")); var next = new FakeProvider("next");
    var chain = new FallbackChain([first, next], new FakeProvider("embed"), clock: () => 100);
    Equal("next", chain.Complete(new CompletionRequest([new Message("user", "hello")])).Provider);
    var streamChain = new FallbackChain([new FakeProvider("stream-first", error: new ProviderUnavailableError("stream-first", "down")), new FakeProvider("stream-next")], new FakeProvider("embed"), clock: () => 100);
    Equal("x", streamChain.Stream(new CompletionRequest([new Message("user", "hello")])).First().Text);
    var requestFailure = new FakeProvider("bad", error: new ProviderRequestError("bad", "schema"));
    Throws<ProviderRequestError>(() => new FallbackChain([requestFailure, next], next).Complete(new CompletionRequest([new Message("user")])));
}

static void FallbackStreamBehavior()
{
    var request = new CompletionRequest([new Message("user", "hello")]);
    var fallback = new FakeProvider("fallback");

    foreach (var first in new ILlmProvider[]
    {
        new StreamProbeProvider("stream-call", () => throw new ProviderUnavailableError("stream-call", "down")),
        new StreamProbeProvider("get-enumerator", () => new ProbeEnumerable(() => throw new ProviderUnavailableError("get-enumerator", "down"))),
        new StreamProbeProvider("first-move", () => new ProbeEnumerable(() => new ProbeEnumerator(firstMoveError: new ProviderUnavailableError("first-move", "down"))))
    })
    {
        var chain = new FallbackChain([first, fallback], fallback, clock: () => 100);
        Equal("x", chain.Stream(request).Single().Text);
    }
    var disposedOnEarlyFailure = new ProbeEnumerator(firstMoveError: new ProviderUnavailableError("first-move-dispose", "down"));
    var disposeChain = new FallbackChain([
        new StreamProbeProvider("first-move-dispose", () => new ProbeEnumerable(() => disposedOnEarlyFailure)), fallback
    ], fallback);
    Equal("x", disposeChain.Stream(request).Single().Text);
    True(disposedOnEarlyFailure.Disposed);

    var afterStart = new StreamProbeProvider("after-start", () => new ProbeEnumerable(() => new ProbeEnumerator(
        [new StreamEvent("token", "first")], new ProviderUnavailableError("after-start", "late"))));
    var unused = new FakeProvider("unused");
    var lateChain = new FallbackChain([afterStart, unused], unused);
    using (var events = lateChain.Stream(request).GetEnumerator())
    {
        True(events.MoveNext()); Equal("first", events.Current.Text);
        Throws<ProviderUnavailableError>(() => events.MoveNext());
    }
    Equal(0, unused.CompleteCalls);

    var failing = new FallbackChain([
        new StreamProbeProvider("one", () => throw new ProviderUnavailableError("one", "down")),
        new StreamProbeProvider("two", () => new ProbeEnumerable(() => new ProbeEnumerator(firstMoveError: new ProviderUnavailableError("two", "down"))))
    ], fallback);
    var combined = Throws<AllProvidersFailedError>(() => failing.Stream(request).ToArray());
    Equal(2, combined.Failures.Length);
}

static void RedactionBehavior()
{
    var request = new CompletionRequest([new Message("user", "Contact x@example.com", [new ToolCall("1", "tool", new() { ["nested"] = new List<object?> { "01012345678" } })])]);
    var redacted = PiiRedactor.RedactRequest(request);
    Equal("Contact [REDACTED_PII_1]", redacted.Messages[0].Content);
    var nested = (List<object?>)redacted.Messages[0].ToolCalls[0].Arguments["nested"]!;
    Equal("[REDACTED_PII_2]", nested[0]);
}

static void ToolBehavior()
{
    var registry = ToolSchemas.BuildToolRegistry();
    Equal(5, registry.AllowedFor(Role.TECHNICIAN).Count);
    var schema = registry.GetSchema("search_manuals")!;
    True(ToolRegistry.ValidateArgs(new Dictionary<string, object?> { ["query"] = "x", ["limit"] = 2 }, schema.Schema));
    False(ToolRegistry.ValidateArgs(new Dictionary<string, object?> { ["query"] = "x", ["limit"] = true }, schema.Schema));
    False(ToolRegistry.ValidateArgs(new Dictionary<string, object?> { ["query"] = "x", ["limit"] = 2, ["x"] = 1 }, schema.Schema));
    var acknowledge = registry.GetSchema("acknowledge_safety_steps")!;
    True(ToolRegistry.ValidateArgs(new Dictionary<string, object?> { ["run_id"] = "r", ["step_ids"] = new List<object?> { System.Numerics.BigInteger.Parse("9223372036854775808") } }, acknowledge.Schema));
    Throws<ToolNotAllowedError>(() => registry.Invoke("unknown", new Dictionary<string, object?>(), Role.TECHNICIAN));
}

static void RecordEqualityBehavior()
{
    var left = new RunStepRecord(1, "agent", ["tool"], [3, 4]);
    var right = new RunStepRecord(1, "agent", ["tool"], [3, 4]);
    Equal(left, right); Equal(left.GetHashCode(), right.GetHashCode());
    var first = new WorkOrderRecord("w", "r", 1, "draft", new Dictionary<string, object?>
    { ["title"] = "x", ["nested"] = new Dictionary<string, object?> { ["items"] = new List<object?> { "a", 1 } } });
    var second = new WorkOrderRecord("w", "r", 1, "draft", new Dictionary<string, object?>
    { ["nested"] = new Dictionary<string, object?> { ["items"] = new List<object?> { "a", 1 } }, ["title"] = "x" });
    Equal(first, second); Equal(first.GetHashCode(), second.GetHashCode());
    var changedOrder = new WorkOrderRecord("w", "r", 1, "draft", new Dictionary<string, object?>
    { ["title"] = "x", ["nested"] = new Dictionary<string, object?> { ["items"] = new List<object?> { 1, "a" } } });
    False(first == changedOrder);
    var differentCollectionType = new WorkOrderRecord("w", "r", 1, "draft", new Dictionary<string, object?>
    { ["title"] = "x", ["nested"] = new Dictionary<string, object?> { ["items"] = new object?[] { "a", 1 } } });
    False(first == differentCollectionType);
}

static void WorkflowRulesBehavior()
{
    var match = new EquipmentMatch("EQ", ["doc"], "Rev B", "current", [1]);
    Equal("Rev A", WorkflowRules.ResolveRevision(match, "Rev A").Revision);
    var gate = WorkflowRules.CheckSafetyGate([2, 1], [1, 99]);
    False(gate.Ok); Equal(2, gate.Missing[0]);
    Equal(RunStatus.APPROVED, WorkflowRules.NextState(RunStatus.PENDING_APPROVAL, "approve", Role.SUPERVISOR));
    Throws<WorkflowError>(() => WorkflowRules.NextState(RunStatus.PENDING_APPROVAL, "approve", Role.TECHNICIAN));
}

static void SecurityBehavior()
{
    Throws<PasswordError>(() => ApplicationSecurity.HashPassword(""));
    True(ApplicationSecurity.VerifyPassword("interop-pass", "scrypt$16384$8$1$AAECAwQFBgcICQoLDA0ODw$6BdsIzHROT4E7IUkFcXv-Ln7QyaZI22pzNs3MwVqdKiLwtdv9h6EzdNqGoNnrHaJrU5IIqBtaIdd3GIZS5FVvg"));
    var legacyHash = "scrypt$16384$8$1$AAECAwQFBgcICQoLDA0ODw$6BdsIzHROT4E7IUkFcXv-Ln7QyaZI22pzNs3MwVqdKiLwtdv9h6EzdNqGoNnrHaJrU5IIqBtaIdd3GIZS5FVvg";
    False(ApplicationSecurity.VerifyPassword("interop-pass", "scrypt$999999999999$8$1$AAECAwQFBgcICQoLDA0ODw$6BdsIzHROT4E7IUkFcXv-Ln7QyaZI22pzNs3MwVqdKiLwtdv9h6EzdNqGoNnrHaJrU5IIqBtaIdd3GIZS5FVvg"));
    False(ApplicationSecurity.VerifyPassword("interop-pass", "scrypt$131072$8$1$AAECAwQFBgcICQoLDA0ODw$6BdsIzHROT4E7IUkFcXv-Ln7QyaZI22pzNs3MwVqdKiLwtdv9h6EzdNqGoNnrHaJrU5IIqBtaIdd3GIZS5FVvg"));
    False(ApplicationSecurity.VerifyPassword("interop-pass", "scrypt$16384$17$1$AAECAwQFBgcICQoLDA0ODw$6BdsIzHROT4E7IUkFcXv-Ln7QyaZI22pzNs3MwVqdKiLwtdv9h6EzdNqGoNnrHaJrU5IIqBtaIdd3GIZS5FVvg"));
    False(ApplicationSecurity.VerifyPassword("interop-pass", "scrypt$16384$8$5$AAECAwQFBgcICQoLDA0ODw$6BdsIzHROT4E7IUkFcXv-Ln7QyaZI22pzNs3MwVqdKiLwtdv9h6EzdNqGoNnrHaJrU5IIqBtaIdd3GIZS5FVvg"));
    False(ApplicationSecurity.VerifyPassword("interop-pass", "scrypt$16384$8$1$bad$hash"));
    True(ApplicationSecurity.VerifyPassword("interop-pass", legacyHash));
    var stored = ApplicationSecurity.HashPassword("application-test-password");
    True(ApplicationSecurity.VerifyPassword("application-test-password", stored));
    Equal(6, stored.Split('$').Length); Equal("scrypt", stored.Split('$')[0]);
    False(ApplicationSecurity.VerifyPassword("wrong", stored));
    const string key = "phase5a-python-compat-key-0123456789";
    const string pythonToken = "eyJ0ZW5hbnRfaWQiOiJ0ZW5hbnQtYSIsInVzZXJfaWQiOjcsInJvbGUiOiJ0ZWNobmljaWFuIiwiZXhwIjoyMDAwMDAwMDAwfQ.hmfDfvJsrG9k9YCC6YQV2-HsZeuE02CAcElANF4I3e8";
    var pythonEncodedPayload = pythonToken.Split('.')[0];
    var hmac = System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key), System.Text.Encoding.UTF8.GetBytes(pythonEncodedPayload));
    Equal("hmfDfvJsrG9k9YCC6YQV2-HsZeuE02CAcElANF4I3e8", Convert.ToBase64String(hmac).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
    IReadOnlyDictionary<string, object?> pythonClaims;
    try { pythonClaims = ApplicationSecurity.VerifyToken(pythonToken, key, now: 2_000_000_000); }
    catch (Exception error) { throw new Exception("Python fixture verification failed: " + error.Message, error); }
    Equal("tenant-a", pythonClaims["tenant_id"]); Equal(7L, pythonClaims["user_id"]); Equal("technician", pythonClaims["role"]);
    Equal(2_000_000_000L, pythonClaims["exp"]);
    // Python rejects only exp < int(now), so exp == int(now) remains valid.
    Throws<TokenError>(() => ApplicationSecurity.VerifyToken(pythonToken, key, now: 2_000_000_001));
    var token = ApplicationSecurity.CreateToken("tenant-Å", 4_000_000_000L, "technician", key, 3600);
    Equal(2, token.Split('.').Length);
    var encodedPayload = token.Split('.')[0].Replace('-', '+').Replace('_', '/');
    encodedPayload += new string('=', (4 - encodedPayload.Length % 4) % 4);
    var payloadText = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedPayload));
    True(payloadText.StartsWith("{\"tenant_id\":\"tenant-\\u00c5\",\"user_id\":4000000000,\"role\":\"technician\",\"exp\":", StringComparison.Ordinal));
    IReadOnlyDictionary<string, object?> claims;
    try { claims = ApplicationSecurity.VerifyToken(token, key); }
    catch (Exception error) { throw new Exception(".NET token verification failed: " + error.Message, error); }
    Equal("tenant-Å", claims["tenant_id"]); Equal(4_000_000_000L, claims["user_id"]);
    Throws<TokenError>(() => ApplicationSecurity.VerifyToken(token[..^1] + (token[^1] == 'a' ? "b" : "a"), key));
    Throws<TokenError>(() => ApplicationSecurity.VerifyToken("not-a-token", key));
    Throws<TokenError>(() => ApplicationSecurity.VerifyToken(token, key, now: DateTimeOffset.UtcNow.AddYears(2).ToUnixTimeSeconds()));
    Throws<TokenError>(() => ApplicationSecurity.VerifyToken(pythonToken, "é".PadRight(31, 'é')));
    var unicodeKey = "\u00e9".PadRight(32, '\u00e9');
    var unicodeToken = ApplicationSecurity.CreateToken("tenant-a", 7, "technician", unicodeKey, 3600);
    True(ApplicationSecurity.VerifyToken(unicodeToken, unicodeKey).ContainsKey("user_id"));
    Throws<TokenError>(() => ApplicationSecurity.VerifyToken(token, new string('z', 32)));
}

static void AuthenticationBehavior()
{
    var stored = ApplicationSecurity.HashPassword("auth-test-password");
    var users = new FakeUsers(new UserRecord(4_000_000_000L, "tenant-a", "tech", "technician", true, stored));
    var result = Authentication.Login("tenant-a", "tech", "auth-test-password", users, new string('s', 32), 120);
    Equal("bearer", result["token_type"]);
    var loaded = Authentication.VerifyAndLoad((string)result["access_token"]!, new string('s', 32), users);
    Equal(4_000_000_000L, loaded["user_id"]);
    Throws<AuthError>(() => Authentication.Login("tenant-a", "tech", "wrong", users, new string('s', 32), 120));
}

static void AnsweringBehavior()
{
    True(AnswerService.LoadSystemPrompt().Length > 0);
    True(File.Exists(Path.Combine(AppContext.BaseDirectory, "prompts", "answer_system.md")));
    var search = new FakeSearch([], []); var provider = new FakeProvider("provider");
    var deps = new AnswerDependencies(new FakeEmbedder(), search, provider, new Settings());
    var result = AnswerService.AnswerQuestion("unrelated question", "tenant-alpha", deps);
    True(result.Refused); Equal(RefusalReason.NO_EVIDENCE, result.Reason); Equal(0, provider.CompleteCalls);

    var answerProvider = new FakeProvider("provider", scripted: ["Pump check completed [chunk:1]."]);
    var grounded = AnswerService.AnswerQuestion("service pump", "tenant-alpha",
        new AnswerDependencies(new FakeEmbedder(), new FakeSearch([Ev(1)], []), answerProvider, new Settings()));
    False(grounded.Refused); Equal(1, grounded.Citations.Length); Equal(1, grounded.Citations[0].ChunkId);

    var repairProvider = new FakeProvider("provider", scripted: ["No citation.", "Verified [chunk:1]."]);
    var repaired = AnswerService.AnswerQuestion("service pump", "tenant-alpha",
        new AnswerDependencies(new FakeEmbedder(), new FakeSearch([Ev(1)], []), repairProvider, new Settings()));
    False(repaired.Refused); Equal(2, repairProvider.CompleteCalls);

    var invalidCitationCases = new (string Text, string Reason)[]
    {
        ("Negative [chunk:-1]", RefusalReason.MISSING_CITATION),
        ("Nonnumeric [chunk:abc]", RefusalReason.MISSING_CITATION),
        ("Unknown [chunk:999]", RefusalReason.INVALID_CITATION),
        ($"Huge [chunk:{new string('9', 500)}]", RefusalReason.INVALID_CITATION)
    };
    foreach (var item in invalidCitationCases)
    {
        var citeProvider = new FakeProvider("provider", scripted: [item.Text, item.Text]);
        var answer = AnswerService.AnswerQuestion("service pump", "tenant-alpha",
            new AnswerDependencies(new FakeEmbedder(), new FakeSearch([Ev(1)], []), citeProvider, new Settings()));
        True(answer.Refused); Equal(item.Reason, answer.Reason);
    }
}

static void AgentBehavior()
{
    var provider = new FakeProvider("provider", scripted: ["invalid", "{\"title\":\"Repair\",\"summary\":\"Done\",\"steps\":[\"Inspect\"],\"parts\":[]}"]);
    var evidence = ImmutableArray.Create(Ev(1, text: "inspect valve"));
    var request = new WorkOrderGeneratorInput("leak", new EquipmentMatch("EQ", ["d"], "A", "current", [1]), new RevisionResolution("A", false, "current"), evidence, []);
    var result = new WorkOrderGenerator(provider).Generate(request);
    Equal("Repair", result.WorkOrder["title"]); False(result.Degraded); Equal(2, provider.CompleteCalls);
    var duplicateKey = new WorkOrderGenerator(new FakeProvider("provider", scripted: ["{\"title\":\"old\",\"title\":\"last\",\"summary\":\"ok\",\"steps\":[\"Inspect\"],\"parts\":[]}"])).Generate(request);
    Equal("last", duplicateKey.WorkOrder["title"]); False(duplicateKey.Degraded);
    var nonFiniteExtra = new WorkOrderGenerator(new FakeProvider("provider", scripted: ["{\"title\":\"ok\",\"summary\":\"ok\",\"steps\":[\"Inspect\"],\"parts\":[],\"ignored\":NaN}"])).Generate(request);
    Equal("ok", nonFiniteExtra.WorkOrder["title"]); False(nonFiniteExtra.Degraded);
    var fallback = new WorkOrderGenerator(new FakeProvider("provider", scripted: ["bad", "still bad"])) .Generate(request);
    True(fallback.Degraded); True(((string)fallback.WorkOrder["summary"]!).StartsWith("Plain RAG Fallback: ", StringComparison.Ordinal));
}

static void OrchestrationBehavior()
{
    var search = new FakeSearch([Ev(1)], []); var runs = new FakeRunRepository();
    var safety = new FakeSafetyRepository([new SafetyStepRecord(81, 1, "Isolate power", "DOC")]);
    var orders = new FakeOrderRepository();
    var provider = new FakeProvider("provider", scripted: ["{\"title\":\"Repair pump\",\"summary\":\"Inspect seal\",\"steps\":[\"Inspect seal\"],\"parts\":[]}"]);
    var orchestrator = new WorkflowOrchestrator(runs, orders, new FakeAudit(), safety,
        new FakeEmbedder(), search, provider, new Settings());
    var events = new List<IReadOnlyDictionary<string, object?>>();
    var result = orchestrator.StartRun(new WorkflowStartInput("pump failure", null, 4_000_000_000L, "tenant-alpha"), emit: events.Add);
    Equal("awaiting_acknowledgement", runs.Status); Equal(2, runs.StepsList.Count); Equal(4, events.Count);
    Equal(result.RunId, runs.Get("tenant-alpha", result.RunId)!.Id);
    Equal("running", events[0]["status"]); Equal("step", events[1]["event"]); Equal("step", events[2]["event"]);
    Equal("awaiting_acknowledgement", events[3]["status"]);
    Equal(1, result.RequiredSafetyStepIds.Count); Equal(81L, result.RequiredSafetyStepIds[0]);

    Throws<GateNotSatisfiedError>(() => orchestrator.Generate(result.RunId, 4_000_000_000L, "tenant-alpha"));
    Equal(0, provider.CompleteCalls);
    var generateEvents = new List<IReadOnlyDictionary<string, object?>>();
    var workOrder = orchestrator.Generate(result.RunId, 4_000_000_000L, "tenant-alpha", [81L], emit: generateEvents.Add);
    Equal("pending_approval", runs.Status); Equal(3, runs.StepsList.Count); Equal(1, provider.CompleteCalls);
    Equal("work_order", generateEvents[0]["event"]); Equal("pending_approval", generateEvents[1]["status"]);
    True(orders.Latest("tenant-alpha", result.RunId) is { Status: "draft" });
    Equal(workOrder["title"], orders.Latest("tenant-alpha", result.RunId)!.Content["title"]);
    Throws<WorkflowError>(() => orchestrator.Approval(result.RunId, "approve", Role.TECHNICIAN, 2, "tenant-alpha"));
    var approved = orchestrator.Approval(result.RunId, "approve", Role.SUPERVISOR, 8, "tenant-alpha", "looks good");
    Equal("approved", approved["status"]); Equal("approved", runs.Status);
    Equal("approved", orders.Latest("tenant-alpha", result.RunId)!.Status);

    var rejectRuns = new FakeRunRepository(); var rejectOrders = new FakeOrderRepository();
    var rejectOrchestrator = new WorkflowOrchestrator(rejectRuns, rejectOrders, new FakeAudit(), safety,
        new FakeEmbedder(), search, new FakeProvider("reject-provider", scripted: ["{\"title\":\"Repair\",\"summary\":\"Check\",\"steps\":[\"Inspect\"],\"parts\":[]}"]), new Settings());
    var rejectStart = rejectOrchestrator.StartRun(new WorkflowStartInput("pump failure", null, 9, "tenant-alpha"));
    rejectOrchestrator.Generate(rejectStart.RunId, 9, "tenant-alpha", [81L]);
    var rejected = rejectOrchestrator.Approval(rejectStart.RunId, "reject", Role.SUPERVISOR, 10, "tenant-alpha", "unsafe");
    Equal("rejected", rejected["status"]); Equal("rejected", rejectRuns.Status);
    Equal("rejected", rejectOrders.Latest("tenant-alpha", rejectStart.RunId)!.Status);

    var callLimitRuns = new FakeRunRepository();
    var callLimitProvider = new FakeProvider("call-limit", scripted:
    [
        "{\"title\":\"R\",\"summary\":\"S\",\"steps\":[\"Inspect\"],\"parts\":[]}",
        "{\"title\":\"R\",\"summary\":\"S\",\"steps\":[\"Inspect\"],\"parts\":[]}"
    ]);
    var callLimited = new WorkflowOrchestrator(callLimitRuns, new FakeOrderRepository(), new FakeAudit(), new FakeSafetyRepository(),
        new FakeEmbedder(), search, callLimitProvider, new Settings(), maxModelCalls: 1);
    var callLimitStart = callLimited.StartRun(new WorkflowStartInput("pump failure", null, 1, "tenant-alpha"));
    callLimited.Generate(callLimitStart.RunId, 1, "tenant-alpha");
    Throws<WorkflowLimitError>(() => callLimited.Generate(callLimitStart.RunId, 1, "tenant-alpha"));
    Equal(1, callLimitProvider.CompleteCalls); Equal("failed", callLimitRuns.Status);

    var cancelledRuns = new FakeRunRepository();
    var cancelledOrchestrator = new WorkflowOrchestrator(cancelledRuns, new FakeOrderRepository(), new FakeAudit(), safety,
        new FakeEmbedder(), search, new FakeProvider("cancelled-provider"), new Settings());
    var stoppedEvents = new List<IReadOnlyDictionary<string, object?>>();
    Throws<RunCancelledError>(() => cancelledOrchestrator.StartRun(new WorkflowStartInput("pump failure", null, 1, "tenant-alpha"), emit: item =>
    {
        stoppedEvents.Add(item);
        if (Equals(item.GetValueOrDefault("status"), "running")) cancelledRuns.SetStatus("tenant-alpha", "run-1", "cancelled");
    }));
    Equal(1, stoppedEvents.Count); Equal("cancelled", cancelledRuns.Status); Equal(0, cancelledRuns.StepsList.Count);

    var cancelledBeforeGenerateRuns = new FakeRunRepository();
    var cancelledBeforeGenerateProvider = new FakeProvider("cancel-before-generate");
    var cancelledBeforeGenerate = new WorkflowOrchestrator(cancelledBeforeGenerateRuns, new FakeOrderRepository(), new FakeAudit(), safety,
        new FakeEmbedder(), search, cancelledBeforeGenerateProvider, new Settings());
    var cancelledStart = cancelledBeforeGenerate.StartRun(new WorkflowStartInput("pump failure", null, 1, "tenant-alpha"));
    cancelledBeforeGenerateRuns.SetStatus("tenant-alpha", cancelledStart.RunId, "cancelled");
    Throws<RunCancelledError>(() => cancelledBeforeGenerate.Generate(cancelledStart.RunId, 1, "tenant-alpha", [81L]));
    Equal(0, cancelledBeforeGenerateProvider.CompleteCalls);

    var timedOutRuns = new FakeRunRepository();
    var timedOut = new WorkflowOrchestrator(timedOutRuns, new FakeOrderRepository(), new FakeAudit(), new FakeSafetyRepository(),
        new FakeEmbedder(), search, new FakeProvider("timed-out"), new Settings(), timeoutSeconds: -1);
    Throws<WorkflowLimitError>(() => timedOut.StartRun(new WorkflowStartInput("pump failure", null, 1, "tenant-alpha")));
    Equal("failed", timedOutRuns.Status);

    var stepTimedOutRuns = new FakeRunRepository();
    var stepTimedOut = new WorkflowOrchestrator(stepTimedOutRuns, new FakeOrderRepository(), new FakeAudit(), new FakeSafetyRepository(),
        new FakeEmbedder(), search, new FakeProvider("step-timed-out"), new Settings(), stepTimeoutSeconds: -1);
    Throws<WorkflowLimitError>(() => stepTimedOut.StartRun(new WorkflowStartInput("pump failure", null, 1, "tenant-alpha")));
    Equal("failed", stepTimedOutRuns.Status);

    var limitedRuns = new FakeRunRepository();
    var limited = new WorkflowOrchestrator(limitedRuns, new FakeOrderRepository(), new FakeAudit(), safety,
        new FakeEmbedder(), search, new FakeProvider("limited-provider", scripted: ["{\"title\":\"R\",\"summary\":\"S\",\"steps\":[\"Inspect\"],\"parts\":[]}"]), new Settings(), tokenBudget: 1);
    var limitedStart = limited.StartRun(new WorkflowStartInput("pump failure", null, 1, "tenant-alpha"));
    Throws<WorkflowLimitError>(() => limited.Generate(limitedStart.RunId, 1, "tenant-alpha", [81L]));
    Equal("failed", limitedRuns.Status);

    var noCallsRuns = new FakeRunRepository();
    var noCalls = new WorkflowOrchestrator(noCallsRuns, new FakeOrderRepository(), new FakeAudit(), new FakeSafetyRepository(),
        new FakeEmbedder(), search, new FakeProvider("no-calls"), new Settings(), maxModelCalls: 0);
    Throws<WorkflowLimitError>(() => noCalls.StartRun(new WorkflowStartInput("pump failure", null, 1, "tenant-alpha")));
    Equal("failed", noCallsRuns.Status);
}

static Evidence Ev(long id, double dense = .9, string text = "pump interval service") => new(id, "DOC", "Manual", "Section", "Rev A", "current", null, text, dense);
static void True(bool value) { if (!value) throw new Exception("expected true"); }
static void False(bool value) => True(!value);
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected {expected}, got {actual}"); }
static T Throws<T>(Action action) where T : Exception { try { action(); } catch (T error) { return error; } throw new Exception($"expected {typeof(T).Name}"); }

sealed class Settings : IRetrievalSettings { public double RetrievalMinSimilarity => .55; }
sealed class FakeEmbedder : IEmbedder
{
    public string ModelName => "model"; public int Dimension => 2;
    public IReadOnlyList<IReadOnlyList<double>> Embed(IReadOnlyList<string> texts) => texts.Select(_ => (IReadOnlyList<double>)new[] { 1d, 2d }).ToArray();
}
sealed class FakeSearch(IReadOnlyList<Evidence> dense, IReadOnlyList<Evidence> keyword) : IChunkSearch
{
    public string? Tenant { get; private set; } public int Limit { get; private set; }
    public IReadOnlyList<Evidence> DenseSearch(string tenantId, IReadOnlyList<double> vector, string model, bool includeSuperseded, int limit) { Tenant = tenantId; Limit = limit; return dense; }
    public IReadOnlyList<Evidence> KeywordSearch(string tenantId, IReadOnlyList<string> terms, string model, bool includeSuperseded, int limit) => keyword;
}
sealed class FakeRegistry : IEmbeddingIndexRegistry { public EmbeddingSpec? Spec { get; private set; } public EmbeddingSpec? Get() => Spec; public void Register(EmbeddingSpec spec) => Spec ??= spec; }
sealed class FakeRunRepository : IRunRepository
{
    private RunRecord? _run; public string Status { get; private set; } = ""; public List<RunStepRecord> StepsList { get; } = [];
    public string Create(string tenantId, long userId, string question) { _run = new("run-1", tenantId, userId, question, "running", 0, false); Status = "running"; return "run-1"; }
    public RunRecord? Get(string tenantId, string runId) => _run is { } run && run.TenantId == tenantId && run.Id == runId ? run with { Status = Status } : null;
    public void SetStatus(string tenantId, string runId, string status, int? totalTokens = null, bool? estimatedTokens = null, bool finished = false) => Status = status;
    public void AddStep(string tenantId, string runId, RunStepRecord step) => StepsList.Add(step);
    public IReadOnlyList<RunStepRecord> Steps(string tenantId, string runId) => StepsList;
    public IReadOnlyList<RunRecord> ListForUser(string tenantId, long userId) => [];
    public IReadOnlyList<RunRecord> ListForTenant(string tenantId, string? status = null) => [];
}
sealed class FakeOrderRepository : IWorkOrderRepository
{
    private WorkOrderRecord? _latest;
    public IReadOnlyDictionary<string, object?> CreateDraft(string tenantId, string runId, IReadOnlyDictionary<string, object?> content)
    { _latest = new WorkOrderRecord("work-1", runId, (_latest?.Version ?? 0) + 1, "draft", new Dictionary<string, object?>(content)); return content; }
    public WorkOrderRecord? Latest(string tenantId, string runId) => _latest is { } latest && latest.RunId == runId ? latest : null;
    public void SetStatus(string tenantId, string workOrderId, string status)
    { if (_latest is { } latest && latest.Id == workOrderId) _latest = new WorkOrderRecord(latest.Id, latest.RunId, latest.Version, status, latest.Content); }
}
sealed class FakeAudit : IAuditLog { public void Record(string tenantId, long? actorUserId, string action, string subjectType, string? subjectId, IReadOnlyDictionary<string, object?>? detail = null) { } }
sealed class FakeSafetyRepository(IReadOnlyList<SafetyStepRecord>? steps = null) : ISafetyPrerequisiteRepository
{ private readonly IReadOnlyList<SafetyStepRecord> _steps = steps ?? []; public IReadOnlyList<SafetyStepRecord> RequiredForDocuments(string tenantId, IReadOnlyList<string> docIds) => _steps; public IReadOnlyList<SafetyStepRecord> RequiredByIds(string tenantId, IReadOnlyList<long> stepIds) => _steps.Where(x => stepIds.Contains(x.Id)).ToArray(); }
sealed class FakeUsers(UserRecord user) : IUserRepository
{ public UserRecord? GetByUsername(string tenantId, string username) => user.TenantId == tenantId && user.Username == username ? user : null; public UserRecord? Get(string tenantId, long userId) => user.TenantId == tenantId && user.Id == userId ? user : null; }
sealed class FakeProvider(string name, Exception? error = null, IReadOnlyList<string>? scripted = null) : ILlmProvider
{
    private readonly Queue<string> _script = new(scripted ?? []); public int CompleteCalls { get; private set; } public string Name => name;
    public CompletionResult Complete(CompletionRequest request)
    { CompleteCalls++; if (error is not null) throw error; var content = _script.Count > 0 ? _script.Dequeue() : "ok"; return new(content, [], new TokenUsage(1, 1), name, "model"); }
    public IEnumerable<StreamEvent> Stream(CompletionRequest request, CancellationToken cancellationToken = default) { if (error is not null) throw error; yield return new("token", "x"); }
    public EmbeddingResult Embed(IReadOnlyList<string> texts) => new(texts.Select(_ => ImmutableArray.Create(1d, 2d)).ToImmutableArray(), "model", 2, new TokenUsage(), name);
}
sealed class StreamProbeProvider(string name, Func<IEnumerable<StreamEvent>> streamFactory) : ILlmProvider
{
    public int StreamCalls { get; private set; }
    public string Name => name;
    public CompletionResult Complete(CompletionRequest request) => throw new NotSupportedException();
    public IEnumerable<StreamEvent> Stream(CompletionRequest request, CancellationToken cancellationToken = default) { StreamCalls++; return streamFactory(); }
    public EmbeddingResult Embed(IReadOnlyList<string> texts) => throw new NotSupportedException();
}
sealed class ProbeEnumerable(Func<IEnumerator<StreamEvent>> enumeratorFactory) : IEnumerable<StreamEvent>
{
    public IEnumerator<StreamEvent> GetEnumerator() => enumeratorFactory();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
sealed class ProbeEnumerator : IEnumerator<StreamEvent>
{
    private readonly IReadOnlyList<StreamEvent> _events;
    private ProviderError? _firstMoveError;
    private ProviderError? _afterEventsError;
    private int _index = -1;
    public bool Disposed { get; private set; }
    public StreamEvent Current { get; private set; } = null!;
    object System.Collections.IEnumerator.Current => Current;
    public ProbeEnumerator(IReadOnlyList<StreamEvent>? events = null, ProviderError? afterEventsError = null, ProviderError? firstMoveError = null)
    { _events = events ?? []; _afterEventsError = afterEventsError; _firstMoveError = firstMoveError; }
    public bool MoveNext()
    {
        if (_firstMoveError is { } first) { _firstMoveError = null; throw first; }
        if (++_index < _events.Count) { Current = _events[_index]; return true; }
        if (_afterEventsError is { } after) { _afterEventsError = null; throw after; }
        return false;
    }
    public void Reset() => throw new NotSupportedException();
    public void Dispose() => Disposed = true;
}
