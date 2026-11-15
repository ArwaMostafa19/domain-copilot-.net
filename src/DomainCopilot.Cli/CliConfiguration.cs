using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace DomainCopilot.Cli;

/// <summary>Small configuration root for the CLI using only framework assemblies.</summary>
internal sealed class CliConfiguration : IConfiguration
{
    private static readonly string[] LegacyNames =
    [
        "POSTGRES_USER", "POSTGRES_PASSWORD", "POSTGRES_DB", "DATABASE_URL", "ADMIN_DATABASE_URL", "APP_DB_PASSWORD", "AUTH_SECRET",
        "LLM_CHAIN", "GROQ_API_KEY", "GROQ_BASE_URL", "GROQ_MODEL", "GROQ_STREAM_USAGE", "GEMINI_API_KEY", "GEMINI_BASE_URL",
        "GEMINI_MODEL", "GEMINI_STREAM_USAGE", "OLLAMA_BASE_URL", "OLLAMA_CHAT_MODEL", "EMBEDDING_PROVIDER", "EMBEDDING_MODEL",
        "EMBEDDING_DIMENSIONS", "LLM_TIMEOUT_SECONDS", "LLM_COOLDOWN_SECONDS", "RETRIEVAL_MIN_SIMILARITY", "RUN_MAX_MODEL_CALLS",
        "RUN_TOKEN_BUDGET", "RUN_TIMEOUT_SECONDS", "RUN_STEP_TIMEOUT_SECONDS", "TOKEN_TTL_SECONDS", "CORS_ORIGINS", "MAX_INGEST_FILE_BYTES", "DEMO_USER_PASSWORD"
    ];
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public CliConfiguration(string root)
    {
        var settings = Path.Combine(root, "src", "DomainCopilot.Api", "appsettings.json");
        if (File.Exists(settings))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(settings));
            Flatten(json.RootElement, "");
        }

        foreach (System.Collections.DictionaryEntry item in Environment.GetEnvironmentVariables())
        {
            if (item.Key is not string name || item.Value is not string value) continue;
            if (name.Contains("__", StringComparison.Ordinal)) _values[name.Replace("__", ":", StringComparison.Ordinal)] = value;
            _values[name] = value;
        }
        foreach (var name in LegacyNames)
            if (Environment.GetEnvironmentVariable(name) is { } value)
                _values[$"Copilot:Legacy:{name}"] = value;
    }

    public string? this[string key] { get => _values.TryGetValue(key, out var value) ? value : null; set => _values[key] = value; }
    public IEnumerable<IConfigurationSection> GetChildren() => Array.Empty<IConfigurationSection>();
    public IChangeToken GetReloadToken() => StaticChangeToken.Instance;
    public IConfigurationSection GetSection(string key) => new Section(this, key);

    private void Flatten(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject()) Flatten(property.Value, path.Length == 0 ? property.Name : path + ":" + property.Name);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            if (path.Equals("Copilot:CorsOrigins", StringComparison.OrdinalIgnoreCase))
                _values["Copilot:CorsOrigins"] = string.Join(',', element.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()));
            else
                for (var i = 0; i < element.GetArrayLength(); i++) Flatten(element[i], path + ":" + i);
        }
        else if (element.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            _values[path] = element.ToString();
    }

    private sealed class StaticChangeToken : IChangeToken
    {
        public static readonly StaticChangeToken Instance = new();
        public bool HasChanged => false;
        public bool ActiveChangeCallbacks => false;
        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) => EmptyDisposable.Instance;
        private sealed class EmptyDisposable : IDisposable { public static readonly EmptyDisposable Instance = new(); public void Dispose() { } }
    }
    private sealed class Section(CliConfiguration root, string path) : IConfigurationSection
    {
        public string Key => path[(path.LastIndexOf(':') + 1)..];
        public string Path => path;
        public string? Value { get => root[path]; set => root[path] = value; }
        public string? this[string key] { get => root[path + ":" + key]; set => root[path + ":" + key] = value; }
        public IEnumerable<IConfigurationSection> GetChildren() => Array.Empty<IConfigurationSection>();
        public IChangeToken GetReloadToken() => StaticChangeToken.Instance;
        public IConfigurationSection GetSection(string key) => new Section(root, path + ":" + key);
    }
}

