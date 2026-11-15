using DomainCopilot.Api.Configuration;
using DomainCopilot.Infrastructure;
using DomainCopilot.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;

var tests = new (string Name, Action Run)[]
{
    ("dotenv parsing", DotEnvParsing),
    ("dotenv process precedence", DotEnvPrecedence),
    ("shared configuration parser parity", SharedParserConsistency),
    ("Python defaults", Defaults),
    ("stream booleans", StreamBooleans),
    ("LLM chain parsing", ChainParsing),
    ("provider validation", ProviderValidation),
    ("numeric validation boundaries", NumericValidation),
    ("cooldown float semantics", CooldownSemantics),
    ("CORS normalization", CorsNormalization),
    ("environment aliases and precedence", AliasesAndPrecedence),
    ("blank legacy chain is rejected", BlankLegacyChain),
    ("secret diagnostics are redacted", SecretRedaction)
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

Console.WriteLine($"{tests.Length - failed}/{tests.Length} configuration tests passed.");
return failed == 0 ? 0 : 1;

static void DotEnvParsing()
{
    var values = DotEnvLoader.Parse("# comment\nexport FIRST = plain=value # trailing comment\nSINGLE='quoted # value = kept'\nDOUBLE=\"line\\nnext = kept\"\nEMPTY=");
    Equal("plain=value", values["FIRST"]);
    Equal("quoted # value = kept", values["SINGLE"]);
    Equal("line\nnext = kept", values["DOUBLE"]);
    Equal("", values["EMPTY"]);
}

static void DotEnvPrecedence()
{
    const string key = "DOMAINCOPILOT_DOTENV_PRECEDENCE_TEST";
    var old = Environment.GetEnvironmentVariable(key);
    try
    {
        Environment.SetEnvironmentVariable(key, "explicit");
        DotEnvLoader.LoadContent($"{key}=from-file");
        Equal("explicit", Environment.GetEnvironmentVariable(key));
    }
    finally { Environment.SetEnvironmentVariable(key, old); }
}

static void SharedParserConsistency()
{
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Copilot:Legacy:EMBEDDING_DIMENSIONS"] = "7_68",
        ["Copilot:Legacy:LLM_TIMEOUT_SECONDS"] = "1_8.5",
        ["Copilot:Legacy:GEMINI_MODEL"] = "gemini-test",
        ["Copilot:Legacy:RUN_TOKEN_BUDGET"] = "2_0_000"
    }).Build();
    var infra = InfrastructureOptions.FromConfiguration(config);
    var api = new CopilotOptions();
    CopilotConfiguration.Bind(config, api);
    Equal(infra.EmbeddingDimensions, api.EmbeddingDimensions);
    Equal(infra.LlmTimeoutSeconds, api.LlmTimeoutSeconds);
    Equal(infra.GeminiModel, api.GeminiModel);
    Equal(infra.RunTokenBudget, api.RunTokenBudget);
}
static CopilotOptions Load(params (string Key, string? Value)[] pairs)
{
    var values = pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    var options = new CopilotOptions();
    CopilotConfiguration.Bind(configuration, options);
    return options;
}

static void Defaults()
{
    var options = Load();
    Equal(new[] { "groq", "gemini", "ollama" }, options.LlmChain);
    Equal(true, options.GroqStreamUsage);
    Equal(true, options.GeminiStreamUsage);
    Equal("ollama", options.EmbeddingProvider);
    Equal("nomic-embed-text", options.EmbeddingModel);
    Equal(768, options.EmbeddingDimensions);
    Equal(180d, options.LlmTimeoutSeconds);
    Equal(60d, options.LlmCooldownSeconds);
    Equal(0.55d, options.RetrievalMinSimilarity);
}

static void StreamBooleans()
{
    foreach (var value in new[] { "true", "TRUE", "1" })
        Equal(true, Load(("Copilot:Legacy:GROQ_STREAM_USAGE", value)).GroqStreamUsage);
    foreach (var value in new[] { "false", "FALSE", "0" })
        Equal(false, Load(("Copilot:Legacy:GEMINI_STREAM_USAGE", $" {value} ")).GeminiStreamUsage);
    Equal(true, Load(("Copilot:Legacy:GROQ_STREAM_USAGE", " ")).GroqStreamUsage);
    Throws("GROQ_STREAM_USAGE must be true, false, 1 or 0",
        () => Load(("Copilot:Legacy:GROQ_STREAM_USAGE", "yes")));
}

static void ChainParsing()
{
    Equal(new[] { "fake", "ollama", "groq" },
        Load(("Copilot:Legacy:LLM_CHAIN", " fake , ollama,groq ")).LlmChain);
    Throws("LLM_CHAIN is empty; allowed provider names are: groq, gemini, ollama, fake",
        () => Load(("Copilot:Legacy:LLM_CHAIN", "  ")));
    Throws("unknown provider 'openai' in LLM_CHAIN; allowed provider names are: groq, gemini, ollama, fake",
        () => Load(("Copilot:Legacy:LLM_CHAIN", "openai,ollama")));
}

static void ProviderValidation()
{
    Throws("EMBEDDING_PROVIDER must be one of: ollama, gemini, fake",
        () => Load(("Copilot:Legacy:EMBEDDING_PROVIDER", "openai")));
    Equal("fake", Load(("Copilot:Legacy:EMBEDDING_PROVIDER", " fake ")).EmbeddingProvider);
}

static void NumericValidation()
{
    Equal(1, Load(("Copilot:Legacy:EMBEDDING_DIMENSIONS", "1")).EmbeddingDimensions);
    Equal(0d, Load(("Copilot:Legacy:RETRIEVAL_MIN_SIMILARITY", "0")).RetrievalMinSimilarity);
    Equal(1d, Load(("Copilot:Legacy:RETRIEVAL_MIN_SIMILARITY", "1")).RetrievalMinSimilarity);
    Throws("EMBEDDING_DIMENSIONS must be an integer greater than 0", () => Load(("Copilot:Legacy:EMBEDDING_DIMENSIONS", "0")));
    Throws("EMBEDDING_DIMENSIONS must be an integer greater than 0", () => Load(("Copilot:Legacy:EMBEDDING_DIMENSIONS", "wide")));
    Throws("LLM_TIMEOUT_SECONDS must be a number greater than 0", () => Load(("Copilot:Legacy:LLM_TIMEOUT_SECONDS", "0")));
    Throws("RUN_MAX_MODEL_CALLS must be an integer greater than 0", () => Load(("Copilot:Legacy:RUN_MAX_MODEL_CALLS", "-1")));
    Throws("RUN_TOKEN_BUDGET must be an integer greater than 0", () => Load(("Copilot:Legacy:RUN_TOKEN_BUDGET", "bad")));
    Throws("RUN_TIMEOUT_SECONDS must be a number greater than 0", () => Load(("Copilot:Legacy:RUN_TIMEOUT_SECONDS", "0")));
    Throws("RUN_STEP_TIMEOUT_SECONDS must be a number greater than 0", () => Load(("Copilot:Legacy:RUN_STEP_TIMEOUT_SECONDS", "-2")));
    Throws("TOKEN_TTL_SECONDS must be an integer greater than 0", () => Load(("Copilot:Legacy:TOKEN_TTL_SECONDS", "0")));
    Throws("MAX_INGEST_FILE_BYTES must be an integer greater than 0", () => Load(("Copilot:Legacy:MAX_INGEST_FILE_BYTES", "0")));
    Throws("RETRIEVAL_MIN_SIMILARITY must be a number between 0 and 1", () => Load(("Copilot:Legacy:RETRIEVAL_MIN_SIMILARITY", "NaN")));
    Throws("RETRIEVAL_MIN_SIMILARITY must be a number between 0 and 1", () => Load(("Copilot:Legacy:RETRIEVAL_MIN_SIMILARITY", "1.01")));
}

static void CooldownSemantics()
{
    Equal(-2.5d, Load(("Copilot:Legacy:LLM_COOLDOWN_SECONDS", "-2.5")).LlmCooldownSeconds);
    Equal(10, Load(("Copilot:Legacy:EMBEDDING_DIMENSIONS", "1_0")).EmbeddingDimensions);
}

static void CorsNormalization()
{
    Equal(Array.Empty<string>(), Load(("Copilot:Legacy:CORS_ORIGINS", " ,  ")).CorsOrigins);
    Equal(new[] { "https://one.example", "http://two.example/path" },
        Load(("Copilot:Legacy:CORS_ORIGINS", " https://one.example/// , http://two.example/path/ , ")).CorsOrigins);
}

static void AliasesAndPrecedence()
{
    var previous = Environment.GetEnvironmentVariable("EMBEDDING_DIMENSIONS");
    try
    {
        Environment.SetEnvironmentVariable("EMBEDDING_DIMENSIONS", "777");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Copilot:EmbeddingDimensions"] = "111"
            })
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Copilot:EmbeddingDimensions"] = "222"
            })
            .AddLegacyEnvironmentAliases()
            .Build();
        var options = new CopilotOptions();
        CopilotConfiguration.Bind(config, options);
        Equal(777, options.EmbeddingDimensions);
    }
    finally
    {
        Environment.SetEnvironmentVariable("EMBEDDING_DIMENSIONS", previous);
    }
}

static void BlankLegacyChain()
{
    var previous = Environment.GetEnvironmentVariable("LLM_CHAIN");
    try
    {
        Environment.SetEnvironmentVariable("LLM_CHAIN", "");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Copilot:LlmChain"] = "fake" })
            .AddLegacyEnvironmentAliases()
            .Build();
        Throws("LLM_CHAIN is empty; allowed provider names are: groq, gemini, ollama, fake",
            () => CopilotConfiguration.Bind(config, new CopilotOptions()));
    }
    finally
    {
        Environment.SetEnvironmentVariable("LLM_CHAIN", previous);
    }
}

static void SecretRedaction()
{
    const string secret = "do-not-print-this-secret";
    var options = Load(("Copilot:Legacy:GROQ_API_KEY", secret), ("Copilot:Legacy:AUTH_SECRET", secret));
    if (options.ToString().Contains(secret, StringComparison.Ordinal))
        throw new Exception("Options diagnostics contained a configured secret.");
}

static void Throws(string expectedMessage, Action action)
{
    try { action(); }
    catch (CopilotConfigurationException exception) when (exception.Message == expectedMessage) { return; }
    throw new Exception($"Expected configuration error: {expectedMessage}");
}

static void Equal<T>(T expected, T actual)
{
    if (expected is IEnumerable<string> expectedItems && actual is IEnumerable<string> actualItems)
    {
        if (!expectedItems.SequenceEqual(actualItems)) throw new Exception("Sequence values differed.");
        return;
    }
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Values differed.");
}
