using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using DomainCopilot.Application.Retrieval;
using DomainCopilot.Application.Text;
using DomainCopilot.Domain.Llm;
using DomainCopilot.Domain.Rag;

namespace DomainCopilot.Application.Answering;

public sealed record AnswerDependencies(IEmbedder Embedder, IChunkSearch Search, ILlmProvider Chain, IRetrievalSettings Settings);

public static partial class AnswerService
{
    public const string RefusalText = "The information is not in the documents.";
    public const string CanaryToken = "CANARY-7f3a9c"; // gitleaks:allow -- Synthetic prompt-injection canary mirrored from Python; not an authentication credential.
    public const string InjectionMarker = "UNTRUSTED EMBEDDED INSTRUCTION";
    public const string REFUSAL_TEXT = RefusalText, CANARY_TOKEN = CanaryToken, INJECTION_MARKER = InjectionMarker;
    public static readonly IReadOnlyDictionary<string, string> REFUSAL_MESSAGES = new Dictionary<string, string>
    {
        [RefusalReason.NO_EVIDENCE] = "I couldn't find relevant information for this question in the documents available to your tenant. Please check the controlled manual for the exact equipment and revision.",
        [RefusalReason.WEAK_EVIDENCE] = "The retrieved passages are not strong enough to support an answer to this question. Please check the controlled manual for the exact equipment and revision before acting.",
        [RefusalReason.OUT_OF_SCOPE] = "I couldn't find this topic in your tenant's maintenance documents.",
        [RefusalReason.EQUIPMENT_NOT_IN_TENANT] = "I couldn't find this equipment in the maintenance documents available to your tenant.",
        [RefusalReason.INVALID_CITATION] = "I couldn't verify that the answer was supported by the retrieved manual passages. Please check the controlled manual for the exact equipment and revision.",
        [RefusalReason.MISSING_CITATION] = "I found related information, but couldn't verify the answer from the available document references. Please check the maintenance manual.",
        [RefusalReason.UNSAFE_OUTPUT] = "I couldn't provide that response because it contained disallowed instruction or canary text. Please ask a question answerable from the controlled maintenance documents."
    };
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "what","which","when","where","who","whom","whose","why","how","does","did","do","is","are","was","were","be","been","being","the","a","an","and","or","but","if","to","of","for","in","on","at","by","with","from","into","about","as","it","this","that","these","those","me","my","please","tell","give","show","find","according","refer","reference","manual","maintenance","document","documents","procedure","approved","specified","specification","data","information","question","answer","untrusted","compromised","supplier"
    };
    public static IReadOnlySet<string> MEANINGFUL_STOP_WORDS => StopWords;
    [GeneratedRegex(@"(?:system\s+prompt|system\s+instructions|canary)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex PromptExtractionPattern();
    [GeneratedRegex(@"\b(?:equipment|machine|device|manual|document|procedure|maintenance reference|for|on|of|about)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex EquipmentReferencePattern();
    [GeneratedRegex(@"\b(ALPHA|BETA)-[A-Z]{2,5}-\d+[A-Z]?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex EquipmentTenantPattern();
    [GeneratedRegex(@"\[chunk:(\d+)\]", RegexOptions.CultureInvariant)] private static partial Regex CitationPattern();
    [GeneratedRegex(@"[a-z0-9]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex WordPattern();
    public static Regex PROMPT_EXTRACTION_PATTERN => PromptExtractionPattern();
    public static Regex EQUIPMENT_REFERENCE_PATTERN => EquipmentReferencePattern();
    public static Regex EQUIPMENT_TENANT_PATTERN => EquipmentTenantPattern();
    public static Regex CITATION_PATTERN => CitationPattern();
    public static Regex WORD_PATTERN => WordPattern();
    public static string REPO_ROOT => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    public static string SYSTEM_PROMPT_PATH => Path.Combine(REPO_ROOT, "prompts", "answer_system.md");

    public static string LoadSystemPrompt(string? path = null)
    {
        path ??= File.Exists(Path.Combine(AppContext.BaseDirectory, "prompts", "answer_system.md"))
            ? Path.Combine(AppContext.BaseDirectory, "prompts", "answer_system.md") : SYSTEM_PROMPT_PATH;
        return File.ReadAllText(path, System.Text.Encoding.UTF8);
    }

    public static Answer AnswerQuestion(string question, string tenantId, AnswerDependencies deps, string? correlationId = null)
    {
        if (EquipmentIsOutsideTenant(question, tenantId)) return Refuse(RefusalReason.EQUIPMENT_NOT_IN_TENANT, [], new());
        var evidence = HybridRetrieval.Retrieve(question, tenantId, deps.Embedder, deps.Search, deps.Settings);
        if (IsPromptExtractionRequest(question)) return Refuse(RefusalReason.OUT_OF_SCOPE, evidence, new());
        var gate = RefusalGate(evidence, deps.Settings.RetrievalMinSimilarity);
        if (gate is not null) return Refuse(gate, evidence, new());
        if (!HasTopicOverlap(question, evidence)) return Refuse(RefusalReason.OUT_OF_SCOPE, evidence, new());

        var result = deps.Chain.Complete(BuildRequest(question, evidence, correlationId, false));
        var answer = Validate(result.Content, result.Usage, evidence);
        if (answer.Reason is not (RefusalReason.MISSING_CITATION or RefusalReason.INVALID_CITATION)) return answer;
        var repaired = deps.Chain.Complete(BuildRequest(question, evidence, correlationId, true));
        var usage = new TokenUsage(result.Usage.PromptTokens + repaired.Usage.PromptTokens,
            result.Usage.CompletionTokens + repaired.Usage.CompletionTokens, result.Usage.Estimated || repaired.Usage.Estimated);
        return Validate(repaired.Content, usage, evidence);
    }

    public static IEnumerable<IReadOnlyDictionary<string, object?>> StreamAnswerQuestion(string question, string tenantId, AnswerDependencies deps, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (EquipmentIsOutsideTenant(question, tenantId)) { yield return Event("done", ("answer", Refuse(RefusalReason.EQUIPMENT_NOT_IN_TENANT, [], new()))); yield break; }
        var evidence = HybridRetrieval.Retrieve(question, tenantId, deps.Embedder, deps.Search, deps.Settings);
        if (IsPromptExtractionRequest(question)) { yield return Event("done", ("answer", Refuse(RefusalReason.OUT_OF_SCOPE, evidence, new()))); yield break; }
        var gate = RefusalGate(evidence, deps.Settings.RetrievalMinSimilarity);
        if (gate is not null) { yield return Event("done", ("answer", Refuse(gate, evidence, new()))); yield break; }
        if (!HasTopicOverlap(question, evidence)) { yield return Event("done", ("answer", Refuse(RefusalReason.OUT_OF_SCOPE, evidence, new()))); yield break; }
        yield return Event("progress", ("stage", "evidence_found"), ("count", evidence.Count));
        var content = new System.Text.StringBuilder(); var usage = new TokenUsage();
        foreach (var item in deps.Chain.Stream(BuildRequest(question, evidence, correlationId, false), cancellationToken))
        {
            if (item.Kind == "token") { content.Append(item.Text); yield return Event("token", ("text", item.Text)); }
            else if (item.Result is not null) { usage = item.Result.Usage; if (content.Length == 0) content.Append(item.Result.Content); }
        }
        var answer = Validate(content.ToString(), usage, evidence);
        if (answer.Reason is RefusalReason.MISSING_CITATION or RefusalReason.INVALID_CITATION)
        {
            var repair = deps.Chain.Complete(BuildRequest(question, evidence, correlationId, true));
            usage = new TokenUsage(usage.PromptTokens + repair.Usage.PromptTokens, usage.CompletionTokens + repair.Usage.CompletionTokens, usage.Estimated || repair.Usage.Estimated);
            answer = Validate(repair.Content, usage, evidence);
        }
        yield return Event("done", ("answer", answer));
    }

    private static CompletionRequest BuildRequest(string question, IReadOnlyList<Evidence> evidence, string? correlationId, bool repair)
    {
        var user = UserMessage(question, evidence);
        if (repair) user += "\n\nYour previous draft did not contain a valid citation. Answer the original question again using only the evidence above. Cite every factual statement with the exact [chunk:ID] marker of a provided evidence block. Do not invent facts or chunk IDs. If the evidence does not answer the question, reply exactly: " + RefusalText;
        return new CompletionRequest([
            new Message("system", LoadSystemPrompt()), new Message("user", user)
        ], MaxTokens: 2048, CorrelationId: correlationId);
    }

    private static string? RefusalGate(IReadOnlyList<Evidence> evidence, double minimum)
    {
        if (evidence.Count == 0) return RefusalReason.NO_EVIDENCE;
        return evidence.Max(x => x.DenseScore) < minimum ? RefusalReason.WEAK_EVIDENCE : null;
    }
    private static bool HasTopicOverlap(string question, IReadOnlyList<Evidence> evidence)
    {
        var questionTerms = WordPattern().Matches(question).Select(m => PythonString.CaseFold(m.Value)).Where(t => t.Length > 2 && !StopWords.Contains(t)).ToHashSet(StringComparer.Ordinal);
        if (questionTerms.Count == 0) return true;
        var evidenceTerms = evidence.SelectMany(x => WordPattern().Matches(x.Text).Select(m => PythonString.CaseFold(m.Value))).ToHashSet(StringComparer.Ordinal);
        return questionTerms.Intersect(evidenceTerms).Count() >= Math.Min(2, questionTerms.Count);
    }
    private static bool IsPromptExtractionRequest(string question) => PromptExtractionPattern().IsMatch(question);
    private static bool EquipmentIsOutsideTenant(string question, string tenantId)
    {
        if (!EquipmentReferencePattern().IsMatch(question)) return false;
        var expected = tenantId.Split('-').Last().ToLowerInvariant();
        return EquipmentTenantPattern().Matches(question).Select(m => m.Groups[1].Value.ToLowerInvariant()).Any(brand => brand != expected);
    }
    private static string UserMessage(string question, IReadOnlyList<Evidence> evidence) => string.Join("\n\n", evidence.Select(EvidenceBlock)) + $"\n\nQuestion: {question}";
    private static string EvidenceBlock(Evidence item) => $"<<<EVIDENCE chunk={item.ChunkId} doc={item.DocId} section=\"{item.Section}\" revision={item.Revision} status={item.DocStatus}>>>\n{item.Text}\n<<<END EVIDENCE>>>";
    private static Answer Validate(string content, TokenUsage usage, IReadOnlyList<Evidence> evidence)
    {
        if (content.Contains(CanaryToken, StringComparison.Ordinal) || content.Contains(InjectionMarker, StringComparison.Ordinal)) return Refuse(RefusalReason.UNSAFE_OUTPUT, evidence, usage);
        var ids = CitationPattern().Matches(content)
            .Select(m => BigInteger.Parse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture))
            .Distinct().ToArray();
        if (ids.Length == 0) return Refuse(RefusalReason.MISSING_CITATION, evidence, usage);
        var allowed = evidence.ToDictionary(x => x.ChunkId);
        if (ids.Any(id => id < long.MinValue || id > long.MaxValue || !allowed.ContainsKey((long)id)))
            return Refuse(RefusalReason.INVALID_CITATION, evidence, usage);
        var citations = ids.Select(id => { var x = allowed[(long)id]; return new Citation(x.ChunkId, x.DocId, x.Section, x.Revision, x.Page); }).ToImmutableArray();
        return new Answer(content, citations, false, null, usage, evidence.ToImmutableArray());
    }
    private static Answer Refuse(string reason, IReadOnlyList<Evidence> evidence, TokenUsage usage) =>
        new(REFUSAL_MESSAGES.GetValueOrDefault(reason, RefusalText), [], true, reason, usage, evidence.ToImmutableArray());
    private static IReadOnlyDictionary<string, object?> Event(string name, params (string Key, object? Value)[] pairs)
    { var result = new Dictionary<string, object?> { ["event"] = name }; foreach (var pair in pairs) result[pair.Key] = pair.Value; return result; }
}
