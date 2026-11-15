using System.Collections.Immutable;
using System.Text.Json;
using DomainCopilot.Domain.Documents;
using DomainCopilot.Domain.Llm;
using DomainCopilot.Domain.Rag;
using DomainCopilot.Domain.Workflow;

var tests = new (string Name, Action Run)[]
{
    ("DomainError and ExtractionError", ExceptionBehavior),
    ("DocumentMeta fields, optional values, and equality", DocumentMetaBehavior),
    ("Section fields, default, null, and equality", SectionBehavior),
    ("ExtractedDocument fields and structural equality", ExtractedDocumentBehavior),
    ("Chunk fields, default, null, and equality", ChunkBehavior),
    ("SafetyStep fields, null, and equality", SafetyStepBehavior),
    ("LLM contracts and structural equality", LlmContractsBehavior),
    ("token estimation counts Unicode code points", TokenEstimationBehavior),
    ("LLM exception hierarchy and fallback", LlmExceptionsBehavior),
    ("RAG contracts and refusal reasons", RagContractsBehavior),
    ("workflow contracts and equality", WorkflowContractsBehavior),
    ("workflow validations cannot be bypassed after construction", WorkflowImmutabilityBehavior),
    ("workflow constructor validation", WorkflowValidationBehavior),
    ("Python-compatible repr for invalid status values", PythonReprBehavior),
    ("workflow state strings and transitions", WorkflowStateBehavior)
};

var failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.WriteLine($"FAIL {name}: {exception.GetType().Name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failed}/{tests.Length} Domain tests passed.");
return failed == 0 ? 0 : 1;

static void ExceptionBehavior()
{
    var empty = new DomainError();
    Equal(string.Empty, empty.Message);

    var domain = new DomainError("domain message");
    Equal("domain message", domain.Message);

    var noMessage = new ExtractionError();
    Equal(string.Empty, noMessage.Message);
    var extraction = new ExtractionError("expected extraction message");
    Equal("expected extraction message", extraction.Message);
    IsType<DomainError>(extraction);
    IsType<Exception>(extraction);

    var cause = new InvalidOperationException("cause");
    var chained = new ExtractionError("wrapped", cause);
    Equal("wrapped", chained.Message);
    Same(cause, chained.InnerException);
    // Python's Exception(None) stores None in args but renders as an empty string.
    Equal(string.Empty, new DomainError(null).Message);
    Equal(string.Empty, new ExtractionError(null).Message);
    Equal(string.Empty, new DomainError(null, cause).Message);
    Equal(string.Empty, new ExtractionError(null, cause).Message);
    Same(cause, new DomainError(null, cause).InnerException);
    Same(cause, new ExtractionError(null, cause).InnerException);
}

static void DocumentMetaBehavior()
{
    var value = new DocumentMeta(
        "tenant-a", "DOC-1", "Manual", "Rev A", "current", "markdown", "manual.md", "abc123");
    Equal("tenant-a", value.TenantId);
    Equal("DOC-1", value.DocId);
    Equal("Manual", value.Title);
    Equal("Rev A", value.Revision);
    Equal("current", value.Status);
    Equal("markdown", value.SourceFormat);
    Equal("manual.md", value.SourcePath);
    Equal("abc123", value.ContentSha256);
    Equal<string?>(null, value.Equipment);
    Equal<string?>(null, value.DocumentType);

    var withOptional = value with { Equipment = "PRESS-1", DocumentType = "manual" };
    Equal("PRESS-1", withOptional.Equipment);
    Equal("manual", withOptional.DocumentType);
    True(value == new DocumentMeta("tenant-a", "DOC-1", "Manual", "Rev A", "current", "markdown", "manual.md", "abc123"));
    False(value == withOptional);
    True(value.Equals(new DocumentMeta("tenant-a", "DOC-1", "Manual", "Rev A", "current", "markdown", "manual.md", "abc123")));

    var noValidation = new DocumentMeta(null!, null!, null!, null!, null!, null!, null!, null!, null, null);
    Equal<string?>(null, noValidation.TenantId);
}

static void SectionBehavior()
{
    var section = new Section("Purpose", "Instructions");
    Equal("Purpose", section.Title);
    Equal("Instructions", section.Text);
    Equal<int?>(null, section.Page);
    True(section == new Section("Purpose", "Instructions"));
    False(section == new Section("Purpose", "Instructions", 1));

    var paged = new Section("Purpose", "Instructions", 4);
    Equal<int?>(4, paged.Page);
    Equal<string?>(null, new Section(null!, null!).Title);
}

static void ExtractedDocumentBehavior()
{
    var meta = new DocumentMeta("tenant-a", "DOC-1", "Manual", "Rev A", "current", "markdown", "manual.md", "abc");
    var first = new Section("Purpose", "Use safely");
    var second = new Section("Maintenance", "Inspect monthly", 2);
    var source = ImmutableArray.Create(first, second);
    var document = new ExtractedDocument(meta, source);

    Equal(source, document.Sections);
    Same(meta, document.Meta);
    var sameContents = new ExtractedDocument(meta, ImmutableArray.Create(
        new Section("Purpose", "Use safely"), new Section("Maintenance", "Inspect monthly", 2)));
    True(document == sameContents);
    True(document.Equals(sameContents));
    Equal(document.GetHashCode(), sameContents.GetHashCode());

    False(document == new ExtractedDocument(meta, ImmutableArray.Create(second, first)));
    False(document == new ExtractedDocument(meta, ImmutableArray.Create(first, new Section("Maintenance", "Inspect yearly", 2))));
    False(document == new ExtractedDocument(meta, ImmutableArray.Create(first)));

    var itemProperty = typeof(ImmutableArray<Section>).GetProperty("Item");
    True(itemProperty is not null && !itemProperty.CanWrite);
    var changedCopy = document.Sections.SetItem(0, new Section("Changed", "Copy only"));
    Equal(first, document.Sections[0]);
    False(document == new ExtractedDocument(meta, changedCopy));

    var emptyDocument = new ExtractedDocument(null!, default);
    True(emptyDocument == new ExtractedDocument(null!, ImmutableArray<Section>.Empty));
}

static void ChunkBehavior()
{
    var chunk = new Chunk(0, "Purpose", "Manual | Purpose | Rev A", "Read the manual.", "Rev A");
    Equal(0, chunk.Ordinal);
    Equal("Purpose", chunk.Section);
    Equal("Manual | Purpose | Rev A", chunk.Context);
    Equal("Read the manual.", chunk.Text);
    Equal("Rev A", chunk.Revision);
    Equal<int?>(null, chunk.Page);
    True(chunk == new Chunk(0, "Purpose", "Manual | Purpose | Rev A", "Read the manual.", "Rev A"));
    False(chunk == chunk with { Page = 1 });
    Equal<string?>(null, new Chunk(0, null!, null!, null!, null!).Text);
}

static void SafetyStepBehavior()
{
    var step = new SafetyStep(3, "Isolate power");
    Equal(3, step.StepNo);
    Equal("Isolate power", step.Text);
    True(step == new SafetyStep(3, "Isolate power"));
    False(step == new SafetyStep(4, "Isolate power"));
    Equal<string?>(null, new SafetyStep(0, null!).Text);
}

static void LlmContractsBehavior()
{
    var emptyParameters = new Dictionary<string, object?>();
    var equalParameters = new Dictionary<string, object?>
    {
        ["nested"] = new List<object?> { "value", 1 },
        ["enabled"] = true
    };
    var equalParametersOtherOrder = new Dictionary<string, object?>
    {
        ["enabled"] = 1,
        ["nested"] = new List<object?> { "value", 1.0 }
    };
    var tool = new ToolSpec("lookup", "Look up a value", equalParameters);
    var equalTool = new ToolSpec("lookup", "Look up a value", equalParametersOtherOrder);
    True(tool == equalTool);
    Equal(tool.GetHashCode(), equalTool.GetHashCode());
    False(tool == new ToolSpec("lookup", "Look up a value", emptyParameters));
    False(new ToolSpec("lookup", "d", new Dictionary<string, object?>
        { ["nested"] = new Dictionary<string, object?> { ["Name"] = 1 } }) ==
        new ToolSpec("lookup", "d", new Dictionary<string, object?>
        { ["nested"] = new Dictionary<string, object?> { ["name"] = 1 } }));
    var nestedListA = new ToolSpec("nested", "d", new Dictionary<string, object?>
        { ["values"] = new List<object?> { 1, new Dictionary<string, object?> { ["inner"] = new List<int> { 2, 3 } } } });
    var nestedListB = new ToolSpec("nested", "d", new Dictionary<string, object?>
        { ["values"] = new List<object?> { 1.0, new Dictionary<string, object?> { ["inner"] = new List<int> { 2, 3 } } } });
    True(nestedListA == nestedListB);
    Equal(nestedListA.GetHashCode(), nestedListB.GetHashCode());
    False(new ToolSpec("nested", "d", new Dictionary<string, object?>
        { ["values"] = new List<int> { 1, 2 } }) ==
        new ToolSpec("nested", "d", new Dictionary<string, object?>
        { ["values"] = new[] { 1, 2 } }));
    False(new ToolSpec("nested", "d", new Dictionary<string, object?>
        { ["values"] = new[] { 1, 2 } }) ==
        new ToolSpec("nested", "d", new Dictionary<string, object?>
        { ["values"] = ImmutableArray.Create(1, 2) }));
    False(new ToolSpec("nested", "d", new Dictionary<string, object?>
        { ["values"] = new List<int> { 1, 2 } }) ==
        new ToolSpec("nested", "d", new Dictionary<string, object?>
        { ["values"] = new List<int> { 2, 1 } }));
    var tupleLikeA = new ToolCall("tuple", "nested", new Dictionary<string, object?>
        { ["values"] = ImmutableArray.Create<object?>(1, new List<int> { 2, 3 }) });
    var tupleLikeB = new ToolCall("tuple", "nested", new Dictionary<string, object?>
        { ["values"] = ImmutableArray.Create<object?>(1.0, new List<int> { 2, 3 }) });
    True(tupleLikeA == tupleLikeB);
    Equal(tupleLikeA.GetHashCode(), tupleLikeB.GetHashCode());
    False(tupleLikeA == new ToolCall("tuple", "nested", new Dictionary<string, object?>
        { ["values"] = ImmutableArray.Create<object?>(new List<int> { 2, 3 }, 1) }));
    False(new ToolSpec("lookup", "d", new Dictionary<string, object?> { ["n"] = 9007199254740993L }) ==
        new ToolSpec("lookup", "d", new Dictionary<string, object?> { ["n"] = 9007199254740992d }));

    var call = new ToolCall("call-1", "lookup", new Dictionary<string, object?> { ["id"] = 7 });
    True(call == new ToolCall("call-1", "lookup", new Dictionary<string, object?> { ["id"] = 7 }));
    Equal("call-1", call.Id);
    Equal("lookup", call.Name);
    Equal(7, call.Arguments["id"]);
    var defaultMessage = new Message("user");
    Equal("user", defaultMessage.Role);
    Equal("", defaultMessage.Content);
    True(defaultMessage.ToolCalls.IsEmpty);
    Equal<string?>(null, defaultMessage.ToolCallId);
    Equal("custom-role", new Message("custom-role").Role); // Literal is a typing constraint in Python, not runtime validation.
    var toolMessage = new Message("tool", "done", ImmutableArray.Create(call), "call-1");
    Equal("call-1", toolMessage.ToolCallId);
    Equal("done", toolMessage.Content);
    Equal(call, toolMessage.ToolCalls[0]);

    var emptyRequest = new CompletionRequest(ImmutableArray<Message>.Empty);
    True(emptyRequest.Tools.IsEmpty);
    Equal("", emptyRequest.PromptText());
    var request = new CompletionRequest(ImmutableArray.Create(
        new Message("system", "first"), new Message("user", "second")),
        ImmutableArray.Create(tool));
    Equal("first\nsecond", request.PromptText());
    Equal(0.0, request.Temperature);
    Equal<int?>(null, request.MaxTokens);
    Equal<string?>(null, request.CorrelationId);
    True(request == new CompletionRequest(ImmutableArray.Create(
        new Message("system", "first"), new Message("user", "second")), ImmutableArray.Create(equalTool)));
    // Python raises TypeError when str.join sees None; .NET uses InvalidOperationException here.
    Throws<InvalidOperationException>(() => new CompletionRequest(
        ImmutableArray.Create(new Message("user", null!))).PromptText(),
        "PromptText cannot join messages because a message has null content.");

    var usage = new TokenUsage();
    Equal(0, usage.PromptTokens);
    Equal(0, usage.CompletionTokens);
    False(usage.Estimated);
    Equal(0L, usage.TotalTokens);
    Equal(11L, new TokenUsage(7, 4).TotalTokens);
    var result = new CompletionResult("answer", ImmutableArray.Create(call), usage, "provider", "model");
    Equal("answer", result.Content);
    Equal(call, result.ToolCalls[0]);
    Equal(usage, result.Usage);
    Equal("provider", result.Provider);
    Equal("model", result.Model);
    Equal("stop", result.FinishReason);
    var completedEvent = new StreamEvent("done", "tail", result);
    Equal("done", completedEvent.Kind);
    Equal("tail", completedEvent.Text);
    Equal(result, completedEvent.Result);
    True(new StreamEvent("token").Result is null);
    Equal("", new StreamEvent("token").Text);

    var spec = new EmbeddingSpec("model", 3);
    True(spec == new EmbeddingSpec("model", 3));
    Equal(spec.GetHashCode(), new EmbeddingSpec("model", 3).GetHashCode());
    False(spec == new EmbeddingSpec("model", 4));
    var vectors = ImmutableArray.Create(ImmutableArray.Create(1.0, 2.0), ImmutableArray.Create(3.0));
    var embeddings = new EmbeddingResult(vectors, "model", 2, usage, "provider");
    Equal("model", embeddings.Model);
    Equal(2, embeddings.Dimensions);
    Equal(usage, embeddings.Usage);
    Equal("provider", embeddings.Provider);
    Equal(1.0, embeddings.Vectors[0][0]);
    var sameEmbeddings = new EmbeddingResult(
        ImmutableArray.Create(ImmutableArray.Create(1.0, 2.0), ImmutableArray.Create(3.0)), "model", 2, usage, "provider");
    True(embeddings == sameEmbeddings);
    Equal(embeddings.GetHashCode(), sameEmbeddings.GetHashCode());
}

static void TokenEstimationBehavior()
{
    Equal(0, TokenEstimation.EstimateTokens(null));
    Equal(0, TokenEstimation.EstimateTokens(""));
    Equal(1, TokenEstimation.EstimateTokens("a"));
    Equal(1, TokenEstimation.EstimateTokens("😀"));
    Equal(1, TokenEstimation.EstimateTokens("😀😀😀😀"));
    Equal(2, TokenEstimation.EstimateTokens("😀😀😀😀😀😀😀😀"));
    Equal(1, TokenEstimation.EstimateTokens("e\u0301"));
    var usage = TokenEstimation.EstimateUsage("😀😀😀😀", "abcdefgh");
    Equal(1, usage.PromptTokens);
    Equal(2, usage.CompletionTokens);
    True(usage.Estimated);
}

static void LlmExceptionsBehavior()
{
    IsType<DomainError>(new LLMError("llm"));
    var unavailable = new ProviderUnavailableError("alpha", "offline");
    IsType<LLMError>(unavailable);
    IsType<ProviderError>(unavailable);
    Equal("alpha", unavailable.Provider);
    Equal("[alpha] offline", unavailable.Message);
    True(unavailable.FallbackAllowed);
    unavailable.FallbackAllowed = false;
    False(unavailable.FallbackAllowed);
    unavailable.Provider = "renamed";
    Equal("renamed", unavailable.Provider);
    True(new QuotaExceededError("p", "q").FallbackAllowed);
    True(new ProviderAuthError("p", "a").FallbackAllowed);
    True(new ProviderResponseError("p", "r").FallbackAllowed);
    False(new ProviderRequestError("p", "bad").FallbackAllowed);
    Equal("all providers failed: [alpha] offline; [beta] quota",
        new AllProvidersFailedError(new ProviderError[] { unavailable, new QuotaExceededError("beta", "quota") }).Message);
    Equal("all providers failed: no provider configured", new AllProvidersFailedError(Array.Empty<ProviderError>()).Message);
    Equal(1, new AllProvidersFailedError(new[] { unavailable }).Failures.Length);
    IsType<LLMError>(new EmbeddingModelMismatchError("mismatch"));
    IsType<LLMError>(new ConfigError("config"));
}

static void RagContractsBehavior()
{
    Equal("no_evidence", RefusalReason.NO_EVIDENCE);
    Equal("weak_evidence", RefusalReason.WEAK_EVIDENCE);
    Equal("out_of_scope", RefusalReason.OUT_OF_SCOPE);
    Equal("equipment_not_in_tenant", RefusalReason.EQUIPMENT_NOT_IN_TENANT);
    Equal("invalid_citation", RefusalReason.INVALID_CITATION);
    Equal("missing_citation", RefusalReason.MISSING_CITATION);
    Equal("unsafe_output", RefusalReason.UNSAFE_OUTPUT);

    var evidence = new Evidence(1, "doc", "Manual", "Safety", "A", "current", null, "Wear PPE");
    Equal(0.0, evidence.DenseScore);
    Equal(0.0, evidence.KeywordScore);
    Equal(0.0, evidence.FusedScore);
    Equal<string?>(null, evidence.Equipment);
    Equal(1, evidence.ChunkId);
    Equal("doc", evidence.DocId);
    Equal("Manual", evidence.Title);
    Equal("Safety", evidence.Section);
    Equal("A", evidence.Revision);
    Equal("current", evidence.DocStatus);
    Equal<int?>(null, evidence.Page);
    Equal("Wear PPE", evidence.Text);
    var sameEvidence = new Evidence(1, "doc", "Manual", "Safety", "A", "current", null, "Wear PPE");
    True(evidence == sameEvidence);
    var citation = new Citation(1, "doc", "Safety", "A", null);
    Equal(1, citation.ChunkId);
    Equal("doc", citation.DocId);
    Equal("Safety", citation.Section);
    Equal("A", citation.Revision);
    Equal<int?>(null, citation.Page);
    var answer = new Answer("Grounded", ImmutableArray.Create(citation), false, null,
        new TokenUsage(4, 2), ImmutableArray.Create(evidence));
    Equal("Grounded", answer.Text);
    False(answer.Refused);
    Equal<string?>(null, answer.Reason);
    Equal(citation, answer.Citations[0]);
    Equal(4, answer.Usage.PromptTokens);
    Equal(evidence, answer.Evidence[0]);
    True(answer == new Answer("Grounded", ImmutableArray.Create(new Citation(1, "doc", "Safety", "A", null)),
        false, null, new TokenUsage(4, 2), ImmutableArray.Create(sameEvidence)));
    Equal(answer.GetHashCode(), new Answer("Grounded", ImmutableArray.Create(citation), false, null,
        new TokenUsage(4, 2), ImmutableArray.Create(evidence)).GetHashCode());
}

static void WorkflowContractsBehavior()
{
    var report = new SymptomReport("Noise reported");
    Equal("Noise reported", report.Symptoms);
    Equal<string?>(null, report.InstalledRevision);
    var match = new EquipmentMatch("EQ", ImmutableArray.Create("DOC"), "A", "current", ImmutableArray.Create(4L));
    Equal("EQ", match.EquipmentId);
    Equal("DOC", match.DocIds[0]);
    Equal("A", match.Revision);
    Equal("current", match.RevisionStatus);
    Equal(4L, match.EvidenceIds[0]);
    True(match == new EquipmentMatch("EQ", ImmutableArray.Create("DOC"), "A", "current", ImmutableArray.Create(4L)));
    Equal(match.GetHashCode(), new EquipmentMatch("EQ", ImmutableArray.Create("DOC"), "A", "current", ImmutableArray.Create(4L)).GetHashCode());
    False(match == new EquipmentMatch("EQ", ImmutableArray.Create("DOC"), "A", "current", ImmutableArray.Create(5L)));
    var clarification = new ClarificationRequest("Which model?");
    Equal("Which model?", clarification.Question);
    var step = new DiagnosticStep("Inspect", ImmutableArray.Create(1L, 2L));
    Equal("Inspect", step.Text);
    Equal(2, step.ChunkIds.Length);
    True(step == new DiagnosticStep("Inspect", ImmutableArray.Create(1L, 2L)));
    Equal(step.GetHashCode(), new DiagnosticStep("Inspect", ImmutableArray.Create(1L, 2L)).GetHashCode());
    var safety = new SafetyStepRecord(1L, 1, "Isolate power");
    Equal(1L, safety.Id);
    Equal(1, safety.StepNo);
    Equal("Isolate power", safety.Text);
    Equal<string?>(null, safety.DocId);
    var plan = new DiagnosticPlan("A", ImmutableArray.Create(step), ImmutableArray<SafetyStepRecord>.Empty);
    Equal("A", plan.Revision);
    Equal(step, plan.Steps[0]);
    True(plan.RequiredSafety.IsEmpty);
    True(plan == new DiagnosticPlan("A", ImmutableArray.Create(new DiagnosticStep("Inspect", ImmutableArray.Create(1L, 2L))), ImmutableArray<SafetyStepRecord>.Empty));
    var draft = ValidDraft();
    Equal("Repair", draft.Title);
    Equal("EQ", draft.EquipmentId);
    Equal("A", draft.Revision);
    Equal("Noise", draft.Symptoms);
    Equal(1, draft.DiagnosticSteps.Length);
    Equal(1L, draft.SafetyStepIds[0]);
    Equal("gasket", draft.Parts[0]);
    Equal(1L, draft.Citations[0]);
    True(draft == ValidDraft());
    Equal(draft.GetHashCode(), ValidDraft().GetHashCode());
    var gate = new GateResult(false, ImmutableArray.Create(2L, 4L));
    Equal(false, gate.Ok);
    Equal(2L, gate.Missing[0]);
    True(gate == new GateResult(false, ImmutableArray.Create(2L, 4L)));
    Equal(gate.GetHashCode(), new GateResult(false, ImmutableArray.Create(2L, 4L)).GetHashCode());
    var resolution = new RevisionResolution("A", false, "current");
    True(resolution == new RevisionResolution("A", false, "current"));
    object workflowError = new WorkflowError();
    False(workflowError is DomainError); // Python WorkflowError derives directly from Exception.
}

static void WorkflowValidationBehavior()
{
    Throws<WorkflowError>(() => new SymptomReport(" \t\n"), "symptoms must not be empty");
    Throws<WorkflowError>(() => new SymptomReport(null!), "symptoms must not be empty");
    Throws<WorkflowError>(() => new ClarificationRequest(null!), "a clarification request needs a question");
    Throws<WorkflowError>(() => new EquipmentMatch("", ImmutableArray.Create("d"), "r", "current", ImmutableArray.Create(1L)), "equipment_id must not be empty");
    Throws<WorkflowError>(() => new EquipmentMatch("e", ImmutableArray<string>.Empty, "r", "current", ImmutableArray.Create(1L)), "equipment match needs at least one document");
    Throws<WorkflowError>(() => new EquipmentMatch("e", ImmutableArray.Create("d"), "", "current", ImmutableArray.Create(1L)), "equipment match needs a revision");
    Throws<WorkflowError>(() => new EquipmentMatch("e", ImmutableArray.Create("d"), "r", "old", ImmutableArray.Create(1L)), "revision_status must be 'current' or 'superseded', got 'old'");
    Throws<WorkflowError>(() => new EquipmentMatch("e", ImmutableArray.Create("d"), "r", "current", ImmutableArray<long>.Empty), "equipment match needs at least one evidence id");
    Throws<WorkflowError>(() => new ClarificationRequest("\u001c\u001d"), "a clarification request needs a question");
    Throws<WorkflowError>(() => new DiagnosticStep(" ", ImmutableArray.Create(1L)), "a diagnostic step needs text");
    Throws<WorkflowError>(() => new DiagnosticStep("text", ImmutableArray<long>.Empty), "every diagnostic step must cite at least one chunk");
    Throws<WorkflowError>(() => new SafetyStepRecord(1, 1, "\u001e"), "a safety step needs text");
    Throws<WorkflowError>(() => new DiagnosticPlan("", ImmutableArray.Create(new DiagnosticStep("x", ImmutableArray.Create(1L))), ImmutableArray<SafetyStepRecord>.Empty), "a diagnostic plan needs a revision");
    Throws<WorkflowError>(() => new DiagnosticPlan("r", ImmutableArray<DiagnosticStep>.Empty, ImmutableArray<SafetyStepRecord>.Empty), "a diagnostic plan needs at least one step");
    Throws<WorkflowError>(() => new WorkOrderDraft(" ", "e", "r", "s", ImmutableArray.Create(new DiagnosticStep("x", ImmutableArray.Create(1L))), ImmutableArray<long>.Empty, ImmutableArray<string>.Empty, ImmutableArray.Create(1L)), "a work order needs a title");
    Throws<WorkflowError>(() => new WorkOrderDraft("t", "", "r", "s", ImmutableArray.Create(new DiagnosticStep("x", ImmutableArray.Create(1L))), ImmutableArray<long>.Empty, ImmutableArray<string>.Empty, ImmutableArray.Create(1L)), "a work order needs an equipment_id");
    Throws<WorkflowError>(() => new WorkOrderDraft("t", "e", "", "s", ImmutableArray.Create(new DiagnosticStep("x", ImmutableArray.Create(1L))), ImmutableArray<long>.Empty, ImmutableArray<string>.Empty, ImmutableArray.Create(1L)), "a work order needs a revision");
    Throws<WorkflowError>(() => new WorkOrderDraft("t", "e", "r", "\u001f", ImmutableArray.Create(new DiagnosticStep("x", ImmutableArray.Create(1L))), ImmutableArray<long>.Empty, ImmutableArray<string>.Empty, ImmutableArray.Create(1L)), "a work order needs the reported symptoms");
    Throws<WorkflowError>(() => new WorkOrderDraft("t", "e", "r", "s", ImmutableArray<DiagnosticStep>.Empty, ImmutableArray<long>.Empty, ImmutableArray<string>.Empty, ImmutableArray.Create(1L)), "a work order needs at least one diagnostic step");
    Throws<WorkflowError>(() => new WorkOrderDraft("t", "e", "r", "s", ImmutableArray.Create(new DiagnosticStep("x", ImmutableArray.Create(1L))), ImmutableArray<long>.Empty, ImmutableArray<string>.Empty, ImmutableArray<long>.Empty), "a work order needs at least one citation");
    // Empty optional detail collections are allowed by the Python constructor.
    var allowed = new WorkOrderDraft("t", "e", "r", "s", ImmutableArray.Create(new DiagnosticStep("x", ImmutableArray.Create(1L))), ImmutableArray<long>.Empty, ImmutableArray<string>.Empty, ImmutableArray.Create(1L));
    True(allowed.Parts.IsEmpty && allowed.SafetyStepIds.IsEmpty);
}

static void WorkflowImmutabilityBehavior()
{
    var validatedWorkflowTypes = new[]
    {
        typeof(SymptomReport), typeof(EquipmentMatch), typeof(ClarificationRequest),
        typeof(DiagnosticStep), typeof(SafetyStepRecord), typeof(DiagnosticPlan), typeof(WorkOrderDraft)
    };

    foreach (var type in validatedWorkflowTypes)
    foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
        True(property.SetMethod is null);

    // A distinct instance still goes through its validating constructor.
    Throws<WorkflowError>(() => new SymptomReport(" ", new SymptomReport("valid").InstalledRevision),
        "symptoms must not be empty");
}

static void PythonReprBehavior()
{
    // Expected repr strings were checked with Python 3.10.6 repr() for the same values.
    Equal("revision_status must be 'current' or 'superseded', got '\\x07'", RevisionStatusError("\u0007"));
    Equal("revision_status must be 'current' or 'superseded', got '\\x1b'", RevisionStatusError("\u001b"));
    Equal("revision_status must be 'current' or 'superseded', got '\\xad'", RevisionStatusError("\u00ad"));
    Equal("revision_status must be 'current' or 'superseded', got '\\u2028'", RevisionStatusError("\u2028"));
    Equal("revision_status must be 'current' or 'superseded', got '\\ue000'", RevisionStatusError("\ue000"));
    Equal("revision_status must be 'current' or 'superseded', got \"bad'status\"", RevisionStatusError("bad'status"));
    Equal("revision_status must be 'current' or 'superseded', got 'bad\\'\"status'", RevisionStatusError("bad'\"status"));
    Equal("revision_status must be 'current' or 'superseded', got 'bad\\\\status'", RevisionStatusError("bad\\status"));
    Equal("revision_status must be 'current' or 'superseded', got 'line\\n\\r\\t'", RevisionStatusError("line\n\r\t"));
}

static string RevisionStatusError(string value)
{
    try
    {
        _ = new EquipmentMatch("equipment", ImmutableArray.Create("doc"), "revision", value, ImmutableArray.Create(1L));
    }
    catch (WorkflowError error)
    {
        return error.Message;
    }
    throw new Exception("Expected invalid revision status to fail.");
}

static void WorkflowStateBehavior()
{
    Equal("technician", Role.TECHNICIAN.ToString());
    Equal("supervisor", Role.Parse("supervisor").Value);
    True(Role.TECHNICIAN == "technician");
    Equal("\"technician\"", JsonSerializer.Serialize(Role.TECHNICIAN));
    Equal(Role.SUPERVISOR, JsonSerializer.Deserialize<Role>("\"supervisor\""));
    Throws<ArgumentException>(() => Role.Parse("Technician"), "'Technician' is not a valid Role");

    var statuses = new[] { "running", "awaiting_acknowledgement", "pending_approval", "approved", "rejected", "cancelled", "failed", "completed" };
    var values = new[] { RunStatus.RUNNING, RunStatus.AWAITING_ACKNOWLEDGEMENT, RunStatus.PENDING_APPROVAL, RunStatus.APPROVED, RunStatus.REJECTED, RunStatus.CANCELLED, RunStatus.FAILED, RunStatus.COMPLETED };
    for (var index = 0; index < statuses.Length; index++)
    {
        Equal(statuses[index], values[index].ToString());
        Equal(values[index], RunStatus.Parse(statuses[index]));
        Equal($"\"{statuses[index]}\"", JsonSerializer.Serialize(values[index]));
        Equal(values[index], JsonSerializer.Deserialize<RunStatus>($"\"{statuses[index]}\""));
    }
    True(RunStatus.RUNNING == "running");
    Throws<ArgumentException>(() => RunStatus.Parse("unknown"), "'unknown' is not a valid RunStatus");

    True(WorkflowState.TERMINAL_RUN_STATUSES.SetEquals(new[] { RunStatus.APPROVED, RunStatus.REJECTED, RunStatus.CANCELLED, RunStatus.FAILED, RunStatus.COMPLETED }));
    True(WorkflowState.SUPERVISOR_EVENTS.SetEquals(new[] { "approve", "reject", "edit" }));
    var expected = new Dictionary<(RunStatus? Current, string Event), RunStatus>
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
    Equal(expected.Count, WorkflowState.TRANSITIONS.Count);
    foreach (var transition in expected)
        Equal(transition.Value, WorkflowState.TRANSITIONS[transition.Key]);
    False(WorkflowState.TRANSITIONS.ContainsKey((RunStatus.COMPLETED, "cancel")));
}

static WorkOrderDraft ValidDraft() => new("Repair", "EQ", "A", "Noise",
    ImmutableArray.Create(new DiagnosticStep("Inspect", ImmutableArray.Create(1L))),
    ImmutableArray.Create(1L), ImmutableArray.Create("gasket"), ImmutableArray.Create(1L));

static T Throws<T>(Action action, string expectedMessage) where T : Exception
{
    try { action(); }
    catch (T exception)
    {
        Equal(expectedMessage, exception.Message);
        return exception;
    }
    throw new Exception($"Expected {typeof(T).Name}.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected '{expected}', got '{actual}'.");
}

static void True(bool condition)
{
    if (!condition)
    {
        var line = new System.Diagnostics.StackTrace(1, true).GetFrame(0)?.GetFileLineNumber();
        throw new Exception($"Expected condition to be true at test line {line}.");
    }
}

static void False(bool condition)
{
    if (condition) throw new Exception("Expected condition to be false.");
}

static void Same(object? expected, object? actual)
{
    if (!ReferenceEquals(expected, actual)) throw new Exception("Expected the same object reference.");
}

static void IsType<T>(object actual)
{
    if (actual is not T) throw new Exception($"Expected assignable type {typeof(T).Name}.");
}
