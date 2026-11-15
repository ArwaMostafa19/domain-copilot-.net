using System.Net;
using System.Text;
using System.Text.Json;
using DomainCopilot.Application;
using DomainCopilot.Application.Llm;
using DomainCopilot.Domain.Documents;
using DomainCopilot.Domain.Llm;
using DomainCopilot.Infrastructure;
using DomainCopilot.Infrastructure.Extraction;
using DomainCopilot.Infrastructure.Providers;
using DomainCopilot.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

var tests = new (string Name, Action Run)[]
{
    ("infrastructure config precedence and secret redaction", ConfigurationBehavior),
    ("explicit empty LLM chain is rejected", EmptyChainBehavior),
    ("OpenAI-compatible complete and tool response", OpenAiCompletionBehavior),
    ("OpenAI-compatible streaming and usage", OpenAiStreamBehavior),
    ("OpenAI-compatible embedding order and Gemini request", OpenAiEmbeddingBehavior),
    ("Gemini chat uses the compatible request and response contract", GeminiChatBehavior),
    ("embedding rows without indexes use Python default ordering", MissingEmbeddingIndexBehavior),
    ("embedding indexes preserve Python integer and stable-sort behavior", EmbeddingIndexEdgeBehavior),
    ("streamed tool calls aggregate by index and preserve Python fields", StreamedToolAggregationBehavior),
    ("OpenAI-compatible embedding usage matches Python", OpenAiEmbeddingFallbackUsageBehavior),
    ("provider errors do not reveal configured API keys", ProviderSecretBehavior),
    ("malformed provider response fields are rejected", ProviderMalformedBehavior),
    ("malformed provider JSON shapes become provider errors and allow chat fallback", ProviderMalformedShapeFallbackBehavior),
    ("large JSON integers remain exact", LargeJsonIntegerBehavior),
    ("provider token usage respects the bounded .NET persistence contract", ProviderTokenCountBoundaryBehavior),
    ("OpenAI-compatible 404 and 408 permit provider fallback", ProviderUnavailableBehavior),
    ("provider streams honor caller cancellation and configured timeout", StreamingCancellationBehavior),
    ("provider stream timeouts cover Ollama, error bodies, and stream acquisition", StreamingTimeoutCoverageBehavior),
    ("Ollama chat and streaming success responses parse correctly", OllamaChatBehavior),
    ("Ollama embedding batches preserve order and usage", OllamaEmbeddingBehavior),
    ("Markdown extraction metadata and section boundaries", MarkdownExtractionBehavior),
    ("PDF extraction keeps page boundaries", PdfExtractionBehavior),
    ("all available corpus Markdown and PDF files extract", CorpusExtractionBehavior),
    ("selected corpus extraction matches Python reference signatures", PythonExtractionParityBehavior),
    ("tenant consistency migration is packaged", TenantMigrationAssetBehavior),
    ("application and administrative database roles remain separate", DatabaseRoleBoundaryBehavior),
    ("PostgreSQL URI settings convert without logging credentials", PostgresUriBehavior),
    ("initial user provisioning requires explicit admin connection and validates input", UserProvisioningBoundaryBehavior),
    ("service registration is lazy and exposes ports", ServiceRegistrationBehavior)
};

var failures = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failures++; Console.WriteLine($"FAIL {name}: {error.GetType().Name}: {error.Message}"); }
}
Console.WriteLine($"PostgreSQL opt-in integration: {PostgresOptInIntegrationTests.RunIfEnabled()}");
Console.WriteLine($"{tests.Length - failures}/{tests.Length} Infrastructure tests passed.");
return failures == 0 ? 0 : 1;

static void ConfigurationBehavior()
{
    const string secret = "test-secret-should-not-print";
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Copilot:GroqBaseUrl"] = "https://nested.invalid",
        ["Copilot:Legacy:GROQ_BASE_URL"] = "https://legacy.invalid/",
        ["Copilot:Legacy:GROQ_API_KEY"] = secret,
        ["Copilot:Legacy:DATABASE_URL"] = "Host=localhost;Database=test"
    }).Build();
    var options = InfrastructureOptions.FromConfiguration(configuration);
    Equal("https://legacy.invalid/", options.GroqBaseUrl);
    False(options.ToString().Contains(secret, StringComparison.Ordinal));
    Equal("groq,gemini,ollama", options.LlmChain);
    Equal(768, options.EmbeddingDimensions);
}

static void EmptyChainBehavior()
{
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    { ["Copilot:Legacy:LLM_CHAIN"] = "" }).Build();
    Throws<InvalidOperationException>(() => InfrastructureOptions.FromConfiguration(configuration));
}

static void OpenAiCompletionBehavior()
{
    var handler = new DelegateHandler(_ => Json(HttpStatusCode.OK, """
        {"model":"response-model","choices":[{"message":{"role":"assistant","content":"Done","tool_calls":[{"id":"call-1","function":{"name":"lookup","arguments":"{\"n\":2}"}}]},"finish_reason":"stop"}],"usage":{"prompt_tokens":4,"completion_tokens":2}}
        """));
    var provider = OpenAi(handler);
    var result = provider.Complete(new CompletionRequest([new Message("user", "hello")]));
    Equal("Done", result.Content);
    Equal("response-model", result.Model);
    Equal(4, result.Usage.PromptTokens);
    Equal(1, result.ToolCalls.Length);
    Equal("lookup", result.ToolCalls[0].Name);
    Equal(2L, result.ToolCalls[0].Arguments["n"]);
}

static void OpenAiStreamBehavior()
{
    var handler = new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""
            data: {"model":"stream-model","choices":[{"delta":{"content":"Hi "},"finish_reason":null}]}

            data: {"choices":[{"delta":{"content":"there"},"finish_reason":null}]}

            data: {"choices":[{"delta":{},"finish_reason":"stop"}],"x_groq":{"usage":{"prompt_tokens":3,"completion_tokens":2}}}

            data: [DONE]

            """)
    });
    var events = OpenAi(handler).Stream(new CompletionRequest([new Message("user", "hello")])).ToArray();
    Equal(3, events.Length);
    Equal("token", events[0].Kind);
    Equal("Hi ", events[0].Text);
    Equal("there", events[1].Text);
    Equal("done", events[2].Kind);
    Equal("Hi there", events[2].Result!.Content);
    Equal(3, events[2].Result!.Usage.PromptTokens);
    Equal(false, events[2].Result!.Usage.Estimated);
}

static void OpenAiEmbeddingBehavior()
{
    string? uri = null;
    string? scheme = null;
    string? token = null;
    string? sentBody = null;
    var handler = new DelegateHandler(request =>
    {
        uri = request.RequestUri!.ToString();
        scheme = request.Headers.Authorization?.Scheme;
        token = request.Headers.Authorization?.Parameter;
        sentBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return Json(HttpStatusCode.OK, """
            {"model":"embed-v1","data":[{"index":1,"embedding":[4,5]},{"index":0,"embedding":[1,2]}],"usage":{"prompt_tokens":7}}
            """);
    });
    var provider = new OpenAiCompatibleProvider("gemini", "https://example.test/v1/", "secret", null,
        new HttpClient(handler), 5, "embed-v1");
    var result = provider.Embed(["first", "second"]);
    Equal("https://example.test/v1/embeddings", uri);
    Equal("Bearer", scheme);
    Equal("secret", token);
    using var requestBody = JsonDocument.Parse(sentBody!);
    Equal("embed-v1", requestBody.RootElement.GetProperty("model").GetString());
    Equal(2, requestBody.RootElement.GetProperty("input").GetArrayLength());
    Equal(2, result.Vectors.Length);
    Equal(1d, result.Vectors[0][0]);
    Equal(4d, result.Vectors[1][0]);
    Equal(7, result.Usage.PromptTokens);
}

static void GeminiChatBehavior()
{
    string? uri = null;
    string? authScheme = null;
    string? authParameter = null;
    string? payload = null;
    var provider = new OpenAiCompatibleProvider("gemini",
        "https://generativelanguage.googleapis.com/v1beta/openai/", "gemini-test-key", "gemini-chat",
        new HttpClient(new DelegateHandler(request =>
        {
            uri = request.RequestUri!.ToString();
            authScheme = request.Headers.Authorization?.Scheme;
            authParameter = request.Headers.Authorization?.Parameter;
            payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(HttpStatusCode.OK, """{"model":"gemini-chat","choices":[{"message":{"content":"Gemini answer"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2}}""");
        })), 5);
    var result = provider.Complete(new CompletionRequest([new Message("user", "question")]));
    Equal("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", uri);
    Equal("Bearer", authScheme);
    Equal("gemini-test-key", authParameter);
    Equal("Gemini answer", result.Content);
    Equal(3, result.Usage.PromptTokens);
    using var body = JsonDocument.Parse(payload!);
    Equal("gemini-chat", body.RootElement.GetProperty("model").GetString());
}

static void MissingEmbeddingIndexBehavior()
{
    var handler = new DelegateHandler(_ => Json(HttpStatusCode.OK, """
        {"data":[{"embedding":[4,5]},{"index":0,"embedding":[1,2]}]}
        """));
    var provider = new OpenAiCompatibleProvider("gemini", "https://example.test/v1", "secret", null,
        new HttpClient(handler), 5, "embed-v1");
    var result = provider.Embed(["first", "second"]);
    Equal(4d, result.Vectors[0][0]);
    Equal(1d, result.Vectors[1][0]);
}

static void EmbeddingIndexEdgeBehavior()
{
    const string huge = "999999999999999999999999999999999999999999999999999999999";
    var provider = new OpenAiCompatibleProvider("gemini", "https://example.test/v1", "secret", null,
        new HttpClient(new DelegateHandler(_ => Json(HttpStatusCode.OK, $$"""
            {"data":[
              {"embedding":[4,4]},
              {"index":0,"embedding":[1,1]},
              {"index":true,"embedding":[3,3]},
              {"index":{{huge}},"embedding":[9,9]},
              {"embedding":[5,5]}
            ]}
            """))), 5, "embed");
    var result = provider.Embed(["one", "two", "three", "four", "five", "six"]);
    // Python treats bool as int, defaults missing indexes to zero, and uses a stable sort.
    Equal(5, result.Vectors.Length); // Python accepts a nonempty response even if row count differs from input.
    Equal(4d, result.Vectors[0][0]);
    Equal(1d, result.Vectors[1][0]);
    Equal(5d, result.Vectors[2][0]);
    Equal(3d, result.Vectors[3][0]);
    Equal(9d, result.Vectors[4][0]);
}

static void StreamedToolAggregationBehavior()
{
    var handler = new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""
            data: {"choices":[{"delta":{"tool_calls":[{"index":2,"function":{"name":"partial","arguments":"{\"a\":"}}]},"finish_reason":null}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":2,"function":{"name":"lookup","arguments":"1}"}}]},"finish_reason":null}]}

            data: [DONE]

            """)
    });
    var events = OpenAi(handler).Stream(new CompletionRequest([new Message("user", "hi")])).ToArray();
    var calls = events[^1].Result!.ToolCalls;
    Equal(1, calls.Length);
    Equal("call_2", calls[0].Id);
    Equal("lookup", calls[0].Name);
    Equal(1L, calls[0].Arguments["a"]);
}
static void OpenAiEmbeddingFallbackUsageBehavior()
{
    var handler = new DelegateHandler(_ => Json(HttpStatusCode.OK, """
        {"data":[{"index":0,"embedding":[1,2]},{"index":1,"embedding":[3,4]}]}
        """));
    var provider = new OpenAiCompatibleProvider("gemini", "https://example.test/v1", "secret", null,
        new HttpClient(handler), 5, "embed-v1");
    var result = provider.Embed(["abc", "def"]);
    Equal(1, result.Usage.PromptTokens); // Python estimates the joined input, including its separator.
    True(result.Usage.Estimated);
}
static void ProviderSecretBehavior()
{
    const string secret = "sensitive-provider-token";
    var handler = new DelegateHandler(_ => Json(HttpStatusCode.Unauthorized, JsonSerializer.Serialize(new { error = secret })));
    var error = Throws<ProviderAuthError>(() => OpenAi(handler, secret).Complete(new CompletionRequest([new Message("user", "hi")])));
    False(error.Message.Contains(secret, StringComparison.Ordinal));
}

static void ProviderMalformedBehavior()
{
    var badContent = new DelegateHandler(_ => Json(HttpStatusCode.OK,
        """{"choices":[{"message":{"role":"assistant","content":42}}]}"""));
    Throws<ProviderResponseError>(() => OpenAi(badContent).Complete(new CompletionRequest([new Message("user", "hi")])));
    var nonObject = new DelegateHandler(_ => Json(HttpStatusCode.OK, "[]"));
    Throws<ProviderResponseError>(() => OpenAi(nonObject).Complete(new CompletionRequest([new Message("user", "hi")])));

    var badCalls = new DelegateHandler(_ => Json(HttpStatusCode.OK,
        """{"choices":[{"message":{"role":"assistant","content":"ok","tool_calls":"invalid"}}]}"""));
    Throws<ProviderResponseError>(() => OpenAi(badCalls).Complete(new CompletionRequest([new Message("user", "hi")])));

    var badOllama = new OllamaProvider(new HttpClient(new DelegateHandler(_ => Json(HttpStatusCode.OK,
        """{"message":{"content":42}}"""))), "http://localhost:11434", "embed", 2, "chat", 5);
    Throws<ProviderResponseError>(() => badOllama.Complete(new CompletionRequest([new Message("user", "hi")])));
    var nullOllama = new OllamaProvider(new HttpClient(new DelegateHandler(_ => Json(HttpStatusCode.OK,
        """{"message":{"content":null}}"""))), "http://localhost:11434", "embed", 2, "chat", 5);
    Throws<ProviderResponseError>(() => nullOllama.Complete(new CompletionRequest([new Message("user", "hi")])));
}

static void ProviderMalformedShapeFallbackBehavior()
{
    var request = new CompletionRequest([new Message("user", "hi")]);
    var badChoice = OpenAi(new DelegateHandler(_ => Json(HttpStatusCode.OK,
        """{"choices":[42]}""")));
    Throws<ProviderResponseError>(() => badChoice.Complete(request));
    Equal("fake", CompleteFallback(badChoice, request).Provider);

    var badTool = OpenAi(new DelegateHandler(_ => Json(HttpStatusCode.OK,
        """{"choices":[{"message":{"content":"ok","tool_calls":[42]}}]}""")));
    Throws<ProviderResponseError>(() => badTool.Complete(request));
    Equal("fake", CompleteFallback(badTool, request).Provider);

    var badStreamTool = OpenAi(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("data: {\"choices\":[{\"delta\":{\"tool_calls\":[42]}}]}\n\ndata: [DONE]\n")
    }));
    var streamed = new FallbackChain([badStreamTool, new FakeLlmProvider("model", 2)], new FakeLlmProvider("model", 2), 0)
        .Stream(request).ToArray();
    Equal("fake", streamed[^1].Result!.Provider);

    var badEmbedding = new OpenAiCompatibleProvider("gemini", "https://example.test/v1", "secret", null,
        new HttpClient(new DelegateHandler(_ => Json(HttpStatusCode.OK,
            """{"data":[42]}"""))), 5, "embed");
    Throws<ProviderResponseError>(() => badEmbedding.Embed(["one"]));

    var badOllama = new OllamaProvider(new HttpClient(new DelegateHandler(_ => Json(HttpStatusCode.OK,
        """{"message":{"content":"ok","tool_calls":[42]}}"""))), "http://localhost:11434", "embed", 2, "chat", 5);
    Throws<ProviderResponseError>(() => badOllama.Complete(request));
    Equal("fake", CompleteFallback(badOllama, request).Provider);
}

static CompletionResult CompleteFallback(ILlmProvider first, CompletionRequest request)
{
    var fallback = new FakeLlmProvider("model", 2);
    return new FallbackChain([first, fallback], fallback, cooldownSeconds: 0).Complete(request);
}

static void ProviderTokenCountBoundaryBehavior()
{
    // The provider protocol exposes integer counts without an int32 guarantee, while
    // the accepted .NET TokenUsage and PostgreSQL persistence contracts use int/integer.
    // Values outside that storage contract are treated as unavailable and estimated.
    var max = OpenAi(new DelegateHandler(_ => Json(HttpStatusCode.OK,
        """{"choices":[{"message":{"content":"x"}}],"usage":{"prompt_tokens":2147483647,"completion_tokens":1}}""")));
    var accepted = max.Complete(new CompletionRequest([new Message("user", "q")]));
    Equal(int.MaxValue, accepted.Usage.PromptTokens);
    False(accepted.Usage.Estimated);

    var beyond = OpenAi(new DelegateHandler(_ => Json(HttpStatusCode.OK,
        """{"choices":[{"message":{"content":"x"}}],"usage":{"prompt_tokens":2147483648,"completion_tokens":1}}""")));
    var estimated = beyond.Complete(new CompletionRequest([new Message("user", "q")]));
    True(estimated.Usage.Estimated);
}

static void LargeJsonIntegerBehavior()
{
    const string large = "1234567890123456789012345678901234567890";
    var handler = new DelegateHandler(_ => Json(HttpStatusCode.OK,
        """{"choices":[{"message":{"role":"assistant","content":"ok","tool_calls":[{"id":"c","function":{"name":"large","arguments":"{\"n\":1234567890123456789012345678901234567890}"}}]}}]}"""));
    var result = OpenAi(handler).Complete(new CompletionRequest([new Message("user", "hi")]));
    var value = (System.Numerics.BigInteger)result.ToolCalls[0].Arguments["n"]!;
    Equal(large, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    string? requestBody = null;
    var capture = new DelegateHandler(request =>
    {
        requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"ok"}}]}""");
    });
    var priorCall = new ToolCall("c", "large", new Dictionary<string, object?> { ["n"] = value });
    OpenAi(capture).Complete(new CompletionRequest([new Message("assistant", "", ToolCalls: [priorCall])]));
    using var body = JsonDocument.Parse(requestBody!);
    var args = body.RootElement.GetProperty("messages")[0].GetProperty("tool_calls")[0]
        .GetProperty("function").GetProperty("arguments").GetString();
    Equal("{\"n\":" + large + "}", args);
}
static void ProviderUnavailableBehavior()
{
    foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.RequestTimeout })
    {
        var handler = new DelegateHandler(_ => Json(status, "{}"));
        Throws<ProviderUnavailableError>(() => OpenAi(handler).Complete(new CompletionRequest([new Message("user", "hi")])));
    }
}

static void StreamingCancellationBehavior()
{
    var request = new CompletionRequest([new Message("user", "hello")]);
    var openAi = OpenAi(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream()) }));
    using (var cancellation = new CancellationTokenSource())
    {
        var pending = Task.Run(() => openAi.Stream(request, cancellation.Token).ToArray());
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        try { pending.GetAwaiter().GetResult(); throw new Exception("expected stream cancellation"); }
        catch (OperationCanceledException) { }
    }

    var ollama = new OllamaProvider(new HttpClient(new DelegateHandler(_ =>
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream()) })),
        "http://localhost:11434", "embed", 2, "chat", 5);
    using (var cancellation = new CancellationTokenSource())
    {
        var pending = Task.Run(() => ollama.Stream(request, cancellation.Token).ToArray());
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        try { pending.GetAwaiter().GetResult(); throw new Exception("expected Ollama stream cancellation"); }
        catch (OperationCanceledException) { }
    }

    var timeout = new OpenAiCompatibleProvider("groq", "https://example.test/v1", "key", "chat",
        new HttpClient(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream()) })), .1);
    Throws<ProviderUnavailableError>(() => timeout.Stream(request).ToArray());

    var ollamaTimeout = new OllamaProvider(new HttpClient(new DelegateHandler(_ =>
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream()) })),
        "http://localhost:11434", "embed", 2, "chat", .1);
    Throws<ProviderUnavailableError>(() => ollamaTimeout.Stream(request).ToArray());
}

static void StreamingTimeoutCoverageBehavior()
{
    var request = new CompletionRequest([new Message("user", "timeout")]);
    foreach (var createProvider in new Func<StreamContent, ILlmProvider>[]
    {
        content => OpenAi(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = content }), timeout: .1),
        content => new OllamaProvider(new HttpClient(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
            { Content = content })), "http://localhost:11434", "embed", 2, "chat", .1)
    })
    {
        var blockedBody = new BlockingStream();
        Throws<ProviderUnavailableError>(() => createProvider(new StreamContent(blockedBody)).Stream(request).ToArray());
        True(blockedBody.WasDisposed);
    }

    foreach (var makeProvider in new Func<BlockingAcquisitionContent, ILlmProvider>[]
    {
        content => OpenAi(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content }), timeout: .1),
        content => new OllamaProvider(new HttpClient(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = content })), "http://localhost:11434", "embed", 2, "chat", .1)
    })
    {
        var content = new BlockingAcquisitionContent();
        Throws<ProviderUnavailableError>(() => makeProvider(content).Stream(request).ToArray());
        True(content.WasDisposed);
    }

    var cancelledContent = new BlockingAcquisitionContent();
    var cancelledProvider = OpenAi(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = cancelledContent }), timeout: 5);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
    Throws<OperationCanceledException>(() => cancelledProvider.Stream(request, cancellation.Token).ToArray());
    True(cancelledContent.WasDisposed);
}

static void OllamaChatBehavior()
{
    string? uri = null;
    string? sent = null;
    var handler = new DelegateHandler(request =>
    {
        uri = request.RequestUri!.ToString();
        sent = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return Json(HttpStatusCode.OK, """{"message":{"role":"assistant","content":"Ollama answer"},"done_reason":"stop","prompt_eval_count":4,"eval_count":2}""");
    });
    var provider = new OllamaProvider(new HttpClient(handler), "http://localhost:11434/", "embed", 2, "chat", 5);
    var request = new CompletionRequest([new Message("user", "hello")], MaxTokens: 30);
    var result = provider.Complete(request);
    Equal("http://localhost:11434/api/chat", uri);
    Equal("Ollama answer", result.Content);
    Equal(4, result.Usage.PromptTokens);
    using (var payload = JsonDocument.Parse(sent!))
        Equal(30, payload.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());

    var events = new OllamaProvider(new HttpClient(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""
            {"message":{"role":"assistant","content":"hello "},"done":false}
            {"message":{"role":"assistant","content":"there"},"done":true,"done_reason":"stop","prompt_eval_count":5,"eval_count":2}
            """)
    })), "http://localhost:11434", "embed", 2, "chat", 5).Stream(new CompletionRequest([new Message("user", "hello")] )).ToArray();
    Equal(3, events.Length);
    Equal("hello ", events[0].Text);
    Equal("there", events[1].Text);
    Equal("hello there", events[2].Result!.Content);
    Equal(5, events[2].Result!.Usage.PromptTokens);
}
static void OllamaEmbeddingBehavior()
{
    var calls = 0;
    var handler = new DelegateHandler(request =>
    {
        calls++;
        using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        var input = body.RootElement.GetProperty("input").GetArrayLength();
        var embeddings = Enumerable.Range(0, input).Select(_ => new[] { 1.0, 2.0 }).ToArray();
        return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { embeddings, prompt_eval_count = input }));
    });
    var provider = new OllamaProvider(new HttpClient(handler), "http://localhost:11434", "embed", 2, null, 5);
    var result = provider.Embed(Enumerable.Range(0, 35).Select(i => $"item-{i}").ToArray());
    Equal(3, calls);
    Equal(35, result.Vectors.Length);
    Equal(2, result.Dimensions);
    Equal(35, result.Usage.PromptTokens);
    False(result.Usage.Estimated);
}

static void MarkdownExtractionBehavior()
{
    var folder = Path.Combine(Path.GetTempPath(), "domain-copilot-extract-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    try
    {
        var path = Path.Combine(folder, "manual.md");
        File.WriteAllText(path, """
            ---
            tenant: tenant-alpha
            doc_id: manual-1
            title: Main manual
            revision: Rev A
            status: current
            equipment: Pump X
            ---
            # Main manual

            | Field | Value |
            | --- | --- |
            Intro text.

            ## Service
            Inspect the seal.
            ### Subsection remains in parent.
            """, new UTF8Encoding(false));
        var document = new FileSystemDocumentExtractor(folder).Extract(path);
        Equal("tenant-alpha", document.Meta.TenantId);
        Equal("manual-1", document.Meta.DocId);
        Equal("markdown", document.Meta.SourceFormat);
        Equal("manual.md", document.Meta.SourcePath);
        Equal(64, document.Meta.ContentSha256.Length); // SHA-256 hex digest has 32 bytes.
        Equal(2, document.Sections.Length);
        Equal("Main manual", document.Sections[0].Title);
        Equal("Service", document.Sections[1].Title);
        True(document.Sections[1].Text.Contains("### Subsection remains in parent.", StringComparison.Ordinal));
    }
    finally { Directory.Delete(folder, recursive: true); }
}

static void PdfExtractionBehavior()
{
    var folder = Path.Combine(Path.GetTempPath(), "domain-copilot-pdf-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    try
    {
        File.WriteAllText(Path.Combine(folder, "manual.md"), """
            ---
            tenant: tenant-alpha
            doc_id: manual-pdf
            title: PDF manual
            revision: Rev B
            status: current
            ---
            PDF metadata sibling.
            """, new UTF8Encoding(false));
        var path = Path.Combine(folder, "manual.pdf");
        File.WriteAllBytes(path, MakeMinimalPdf("Hello PDF"));
        var document = new FileSystemDocumentExtractor(folder).Extract(path);
        Equal("pdf", document.Meta.SourceFormat);
        Equal("manual.pdf", document.Meta.SourcePath);
        True(document.Sections.Length > 0);
        Equal(1, document.Sections[0].Page);
        True(document.Sections[0].Text.Contains("Hello PDF", StringComparison.Ordinal));
    }
    finally { Directory.Delete(folder, recursive: true); }
}

static void CorpusExtractionBehavior()
{
    var root = Directory.GetCurrentDirectory();
    while (!Directory.Exists(Path.Combine(root, "corpus")) && Directory.GetParent(root) is { } parent) root = parent.FullName;
    var files = Directory.GetFiles(Path.Combine(root, "corpus"), "*.*", SearchOption.AllDirectories)
        .Where(path => !string.Equals(Path.GetDirectoryName(path), Path.Combine(root, "corpus"), StringComparison.OrdinalIgnoreCase))
        .Where(path => Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase) ||
                       Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        .Order(StringComparer.Ordinal).ToArray();
    True(files.Length > 0);
    var extractor = new FileSystemDocumentExtractor(root);
    foreach (var path in files)
    {
        var document = extractor.Extract(path);
        True(document.Sections.Length > 0);
        True(document.Meta.ContentSha256.Length == 64);
    }
}

static void PythonExtractionParityBehavior()
{
    var root = Directory.GetCurrentDirectory();
    while (!Directory.Exists(Path.Combine(root, "corpus")) && Directory.GetParent(root) is { } parent) root = parent.FullName;
    var extractor = new FileSystemDocumentExtractor(root);
    Check("air-compressor-preventive-maintenance.md", 11,
        "cdfbaf5c8b8ba5c954193988d1b2b5faab8446ee4c2ce2e24d45707493559257",
        "5445e75950499add3111d82d9413399ef8ed3c895f779349650b7d4d630252d5",
        "7aa35f4603e0647903ab2787996f1ecaf1ce3dedf3607ff3cd75cdf96ed06893");
    // PdfPig and pypdf produce different text for this PDF, so compare metadata,
    // section ordering and page boundaries without asserting false text parity.
    Check("hydraulic-press-manual-rev-a.pdf", 15,
        "f5adb223a0a0354fcdda13f0bad4b1e7fd0ebab19c31b8c4494ba8def92eeac1",
        null,
        "13a4f479676268b44c40087bb0cf0d462594d05841b4f1a54ba67781b6b42ab8");

    void Check(string fileName, int sectionCount, string expectedStructureSignature,
        string? expectedTextSignature, string expectedFileHash)
    {
        var document = extractor.Extract(Path.Combine(root, "corpus", "tenant-alpha", fileName));
        Equal(expectedFileHash, document.Meta.ContentSha256);
        Equal(sectionCount, document.Sections.Length);
        var structure = string.Join('\n', document.Sections.Select(section =>
            $"{section.Title}|{section.Page?.ToString() ?? "None"}"));
        var structureSignature = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(structure)));
        Equal(expectedStructureSignature, structureSignature);
        if (expectedTextSignature is not null)
        {
            var components = document.Sections.Select(section =>
                $"{section.Title}|{section.Page?.ToString() ?? "None"}|{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(section.Text)))}");
            var textSignature = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', components))));
            Equal(expectedTextSignature, textSignature);
        }
    }
}
static void TenantMigrationAssetBehavior()
{
    var path = Path.Combine(AppContext.BaseDirectory, "migrations", "006_tenant_consistent_foreign_keys.sql");
    True(File.Exists(path));
    var sql = File.ReadAllText(path);
    True(sql.Contains("chunks_same_tenant_document_fk", StringComparison.Ordinal));
    True(sql.Contains("runs_same_tenant_user_fk", StringComparison.Ordinal));
    True(sql.Contains("work_orders_same_tenant_run_fk", StringComparison.Ordinal));
    True(sql.Contains("ask_log_same_tenant_user_fk", StringComparison.Ordinal));
}
static void PostgresUriBehavior()
{
    var connection = new Npgsql.NpgsqlConnectionStringBuilder(PostgresConnectionString.ToNpgsql("postgresql://app:p%40ss%3Dword@db:5544/CopilotDB?sslmode=require&application_name=domain-copilot"));
    Equal("db", connection.Host);
    Equal(5544, connection.Port);
    Equal("CopilotDB", connection.Database);
    Equal("app", connection.Username);
    Equal("p@ss=word", connection.Password);
    Equal(Npgsql.SslMode.Require, connection.SslMode);
    var verified = new Npgsql.NpgsqlConnectionStringBuilder(PostgresConnectionString.ToNpgsql("postgres://app:p@db/CopilotDB?sslmode=verify-full"));
    Equal(5432, verified.Port);
    Equal(Npgsql.SslMode.VerifyFull, verified.SslMode);
    Throws<FormatException>(() => PostgresConnectionString.ToNpgsql("postgresql://invalid:99999/db"));
}
static void UserProvisioningBoundaryBehavior()
{
    var service = new PostgresUserProvisioner(new NoConnections(), new InfrastructureOptions());
    Throws<ArgumentException>(() => service.CreateIfMissing("", "u", "technician", "p"));
    Throws<ArgumentException>(() => service.CreateIfMissing("tenant-alpha", "u", "owner", "p"));
    Throws<ArgumentException>(() => service.CreateIfMissing("tenant-alpha", "u", "technician", ""));
    Throws<InvalidOperationException>(() => service.CreateIfMissing("tenant-alpha", "u", "technician", "nonempty"));
}
static void DatabaseRoleBoundaryBehavior()
{
    static ServiceProvider Build(string databaseUrl, string? adminUrl = null)
    {
        var values = new Dictionary<string, string?> { ["Copilot:Legacy:DATABASE_URL"] = databaseUrl };
        if (adminUrl is not null) values["Copilot:Legacy:ADMIN_DATABASE_URL"] = adminUrl;
        var services = new ServiceCollection();
        services.AddDomainCopilotInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return services.BuildServiceProvider();
    }

    using (var valid = Build("Host=localhost;Database=test;Username=copilot_app;Password=ignored"))
        True(valid.GetRequiredService<IPostgresConnections>().Application is not null);
    using (var elevatedApp = Build("Host=localhost;Database=test;Username=postgres;Password=ignored"))
        Throws<InvalidOperationException>(() => elevatedApp.GetRequiredService<IPostgresConnections>());
    using (var sharedRoles = Build("Host=localhost;Database=test;Username=copilot_app;Password=ignored",
               "Host=localhost;Database=test;Username=copilot_app;Password=ignored"))
        Throws<InvalidOperationException>(() => sharedRoles.GetRequiredService<IPostgresConnections>());
}
static void ServiceRegistrationBehavior()
{
    var services = new ServiceCollection();
    var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
    services.AddDomainCopilotInfrastructure(configuration);
    True(services.Any(x => x.ServiceType == typeof(IDocumentRepository)));
    True(services.Any(x => x.ServiceType == typeof(IChunkSearch)));
    True(services.Any(x => x.ServiceType == typeof(IUserRepository)));
    True(services.Any(x => x.ServiceType == typeof(ILlmProvider)));
    // Registration itself does not open a database connection; operational callers resolve repositories later.
}

static OpenAiCompatibleProvider OpenAi(DelegateHandler handler, string key = "test-key", double timeout = 5) =>
    new("groq", "https://example.test/v1", key, "chat-model", new HttpClient(handler), timeout);

static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status)
{ Content = new StringContent(content, Encoding.UTF8, "application/json") };

static byte[] MakeMinimalPdf(string text)
{
    var content = $"BT /F1 12 Tf 20 100 Td ({text}) Tj ET";
    var stream = Encoding.ASCII.GetBytes(content);
    var objects = new[]
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        $"<< /Length {stream.Length} >>\nstream\n{content}\nendstream"
    };
    using var output = new MemoryStream();
    void Write(string value) { var bytes = Encoding.ASCII.GetBytes(value); output.Write(bytes); }
    Write("%PDF-1.4\n");
    var offsets = new List<long>();
    for (var i = 0; i < objects.Length; i++)
    {
        offsets.Add(output.Position);
        Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
    }
    var xref = output.Position;
    Write($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
    foreach (var offset in offsets) Write($"{offset:0000000000} 00000 n \n");
    Write($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    return output.ToArray();
}

static void True(bool value) { if (!value) throw new Exception("expected true"); }
static void False(bool value) => True(!value);
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"expected {expected}, got {actual}");
}
static T Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T error) { return error; }
    throw new Exception($"expected {typeof(T).Name}");
}

sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
{
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(send(request));
}

sealed class BlockingStream : Stream
{
    public bool WasDisposed { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

sealed class BlockingAcquisitionContent : HttpContent
{
    public bool WasDisposed { get; private set; }
    protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
    {
        Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).GetAwaiter().GetResult();
        throw new InvalidOperationException("unreachable after cancellation");
    }
    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
        throw new NotSupportedException("This test content is consumed as a stream.");
    protected override bool TryComputeLength(out long length) { length = 0; return true; }
    protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
}

sealed class NoConnections : IPostgresConnections
{
    public Npgsql.NpgsqlDataSource Application => throw new InvalidOperationException("A connection must not be opened during validation.");
    public Npgsql.NpgsqlDataSource Administrative => throw new InvalidOperationException("A connection must not be opened during validation.");
}



