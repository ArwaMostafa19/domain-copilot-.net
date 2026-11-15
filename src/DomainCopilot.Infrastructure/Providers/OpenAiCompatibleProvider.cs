using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text;
using System.Text.Json;
using DomainCopilot.Application;
using DomainCopilot.Domain.Llm;

namespace DomainCopilot.Infrastructure.Providers;

/// <summary>Gemini and Groq adapter for the OpenAI-compatible chat and embedding endpoints.</summary>
public sealed class OpenAiCompatibleProvider : ILlmProvider
{
    private const int BodySnippetLength = 200;
    private readonly HttpClient _client;
    private readonly string _apiKey;
    private readonly string? _model;
    private readonly string? _embeddingModel;
    private readonly double _timeoutSeconds;
    private readonly bool _streamUsage;

    public OpenAiCompatibleProvider(string name, string baseUrl, string apiKey, string? model,
        HttpClient client, double timeoutSeconds, string? embeddingModel = null, bool streamUsage = true)
    {
        Name = name;
        BaseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _model = model;
        _client = client;
        _timeoutSeconds = timeoutSeconds;
        _embeddingModel = embeddingModel;
        _streamUsage = streamUsage;
    }

    public string Name { get; }
    private string BaseUrl { get; }

    public CompletionResult Complete(CompletionRequest request)
    {
        var model = RequireModel();
        using var timeout = CreateTimeout();
        using var message = CreateRequest($"{BaseUrl}/chat/completions", ChatPayload(request, model, false), request.CorrelationId);
        try
        {
            using var response = _client.Send(message, HttpCompletionOption.ResponseContentRead, timeout.Token);
            if (!response.IsSuccessStatusCode) throw MapStatus(response.StatusCode, response.Content.ReadAsStringAsync(timeout.Token).GetAwaiter().GetResult());
            using var document = Parse(response.Content.ReadAsStringAsync(timeout.Token).GetAwaiter().GetResult());
            return ReadCompletion(document.RootElement, request, model);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        { throw new ProviderUnavailableError(Name, "request timed out"); }
        catch (HttpRequestException)
        { throw new ProviderUnavailableError(Name, "provider endpoint is unavailable"); }
    }

    public IEnumerable<StreamEvent> Stream(CompletionRequest request, CancellationToken cancellationToken = default)
    {
        var model = RequireModel();
        using var timeout = CreateTimeout(cancellationToken);
        using var message = CreateRequest($"{BaseUrl}/chat/completions", ChatPayload(request, model, true), request.CorrelationId);
        HttpResponseMessage response;
        try { response = _client.Send(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { throw new ProviderUnavailableError(Name, "request timed out"); }
        catch (HttpRequestException) { throw new ProviderUnavailableError(Name, "provider endpoint is unavailable"); }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string body;
                try { body = response.Content.ReadAsStringAsync(timeout.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                { throw new ProviderUnavailableError(Name, "request timed out"); }
                throw MapStatus(response.StatusCode, body);
            }
                Stream stream;
                try { stream = response.Content.ReadAsStream(timeout.Token); }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                { throw new ProviderUnavailableError(Name, "request timed out"); }
                using (stream)
                {
            using var reader = new StreamReader(stream);
            var content = new StringBuilder();
            var fragments = new SortedDictionary<int, ToolFragment>();
            TokenUsage? usage = null;
            var finishReason = "stop";
            var responseModel = model;

            while (true)
            {
                string? line;
                try { line = reader.ReadLineAsync(timeout.Token).GetAwaiter().GetResult(); }
                catch (IOException) { throw new ProviderUnavailableError(Name, "provider stream was interrupted"); }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { throw new ProviderUnavailableError(Name, "request timed out"); }
                if (line is null) break;
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith(':')) continue;
                if (!trimmed.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = trimmed[5..].Trim();
                if (data == "[DONE]") break;
                using var chunkDocument = Parse(data);
                var root = chunkDocument.RootElement;
                if (TryString(root, "model", out var foundModel)) responseModel = foundModel;
                if (root.TryGetProperty("choices", out var choices) && choices.ValueKind != JsonValueKind.Null)
                {
                    if (choices.ValueKind != JsonValueKind.Array)
                        throw new ProviderResponseError(Name, $"{Name} sent choices that are not a list");
                    if (choices.GetArrayLength() > 0)
                    {
                        var choice = choices[0];
                        if (choice.ValueKind != JsonValueKind.Object)
                            throw new ProviderResponseError(Name, $"{Name} sent a choice that is not an object");
                        if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind != JsonValueKind.Null)
                        {
                            if (delta.ValueKind != JsonValueKind.Object)
                                throw new ProviderResponseError(Name, $"{Name} sent a delta that is not an object");
                            if (TryString(delta, "content", out var token) && token.Length > 0)
                            {
                                content.Append(token);
                                yield return new StreamEvent("token", token);
                            }
                            if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind != JsonValueKind.Null)
                            {
                                if (calls.ValueKind != JsonValueKind.Array) throw new ProviderResponseError(Name, $"{Name} sent tool_calls that are not a list");
                                foreach (var call in calls.EnumerateArray()) Accumulate(call, fragments);
                            }
                        }
                        if (TryString(choice, "finish_reason", out var reason) && reason.Length > 0) finishReason = reason;
                    }
                }
                var foundUsage = ReadChunkUsage(root);
                if (foundUsage is not null) usage = foundUsage;
            }

            var resultContent = content.ToString();
            var tools = fragments.Select(pair => pair.Value.ToCall(Name, pair.Key)).ToImmutableArray();
            var finalUsage = usage ?? TokenEstimation.EstimateUsage(request.PromptText(), resultContent);
            yield return new StreamEvent("done", Result: new CompletionResult(
                resultContent, tools, finalUsage, Name, responseModel, finishReason));
                }
        }
    }

    public EmbeddingResult Embed(IReadOnlyList<string> texts)
    {
        if (_embeddingModel is null) throw new ProviderRequestError(Name, "embeddings are not configured for this provider");
        if (texts.Count == 0) return EmptyEmbedding();
        using var timeout = CreateTimeout();
        using var request = CreateRequest($"{BaseUrl}/embeddings",
            new { model = _embeddingModel, input = texts }, null);
        try
        {
            using var response = _client.Send(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            if (!response.IsSuccessStatusCode) throw MapStatus(response.StatusCode, response.Content.ReadAsStringAsync(timeout.Token).GetAwaiter().GetResult());
            using var document = Parse(response.Content.ReadAsStringAsync(timeout.Token).GetAwaiter().GetResult());
            var root = document.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                throw new ProviderResponseError(Name, $"{Name} returned a malformed embedding response");
            var vectors = data.EnumerateArray()
                .Select(item => (Index: EmbeddingIndex(item),
                    Vector: ReadVector(item)))
                .OrderBy(item => item.Index).Select(item => item.Vector).ToImmutableArray();
            var usage = root.TryGetProperty("usage", out var usageElement) && ReadEmbeddingUsage(usageElement) is { } reported
                ? reported : new TokenUsage(TokenEstimation.EstimateTokens(string.Join("\n", texts)), 0, true);
            return new EmbeddingResult(vectors, _embeddingModel, vectors.Length == 0 ? 0 : vectors[0].Length, usage, Name);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { throw new ProviderUnavailableError(Name, "request timed out"); }
        catch (HttpRequestException) { throw new ProviderUnavailableError(Name, "provider endpoint is unavailable"); }
    }

    public bool Healthcheck()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(5, _timeoutSeconds)));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/models");
        AddAuthorization(request);
        try { using var response = _client.Send(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); return response.IsSuccessStatusCode; }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { return false; }
    }

    private CompletionResult ReadCompletion(JsonElement root, CompletionRequest request, string model)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new ProviderResponseError(Name, $"{Name} returned a response with no choices");
        var choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object)
            throw new ProviderResponseError(Name, $"{Name} returned a choice that is not an object");
        if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            throw new ProviderResponseError(Name, $"{Name} returned a choice with no message object");
        var contentValue = message.TryGetProperty("content", out var rawContent) ? rawContent : default;
        var content = contentValue.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => "",
            JsonValueKind.String => contentValue.GetString()!,
            _ => throw new ProviderResponseError(Name, $"{Name} returned message content that is not text")
        };
        ImmutableArray<ToolCall> calls;
        if (!message.TryGetProperty("tool_calls", out var rawCalls) || rawCalls.ValueKind == JsonValueKind.Null)
            calls = ImmutableArray<ToolCall>.Empty;
        else if (rawCalls.ValueKind == JsonValueKind.Array)
            calls = rawCalls.EnumerateArray().Select((item, index) => ReadToolCall(item, index)).ToImmutableArray();
        else
            throw new ProviderResponseError(Name, $"{Name} returned tool_calls that are not a list");
        var usage = root.TryGetProperty("usage", out var usageElement)
            ? ReadUsage(usageElement) ?? TokenEstimation.EstimateUsage(request.PromptText(), content)
            : TokenEstimation.EstimateUsage(request.PromptText(), content);
        var reason = TryString(choice, "finish_reason", out var finish) && finish.Length > 0 ? finish : "stop";
        return new CompletionResult(content, calls, usage, Name,
            TryString(root, "model", out var responseModel) && responseModel.Length > 0 ? responseModel : model, reason);
    }

    private object ChatPayload(CompletionRequest request, string model, bool stream)
    {
        var messages = request.Messages.Select(message =>
        {
            var item = new Dictionary<string, object?> { ["role"] = message.Role, ["content"] = message.Content };
            if (message.Role == "tool") item["tool_call_id"] = message.ToolCallId;
            if (!message.ToolCalls.IsEmpty)
                item["tool_calls"] = message.ToolCalls.Select(call => new
                {
                    id = call.Id, type = "function",
                    function = new { name = call.Name, arguments = JsonSerializer.Serialize(call.Arguments, InfrastructureJson.Options) }
                }).ToArray();
            return item;
        }).ToArray();
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model, ["messages"] = messages, ["temperature"] = request.Temperature
        };
        if (request.MaxTokens is not null) payload["max_tokens"] = request.MaxTokens.Value;
        if (!request.Tools.IsEmpty) payload["tools"] = request.Tools.Select(ToTool).ToArray();
        if (stream)
        {
            payload["stream"] = true;
            if (_streamUsage) payload["stream_options"] = new { include_usage = true };
        }
        return payload;
    }

    private static object ToTool(ToolSpec tool) => new
    {
        type = "function",
        function = new { name = tool.Name, description = tool.Description, parameters = tool.Parameters }
    };

    private ToolCall ReadToolCall(JsonElement item, int index)
    {
        if (item.ValueKind != JsonValueKind.Object)
            throw new ProviderResponseError(Name, $"returned malformed tool call {index}");
        if (!item.TryGetProperty("function", out var function) ||
            function.ValueKind != JsonValueKind.Object ||
            !TryString(function, "name", out var name) || string.IsNullOrEmpty(name))
            throw new ProviderResponseError(Name, $"returned tool call {index} without a name");
        var callId = TryString(item, "id", out var id) && id.Length > 0 ? id : $"call_{index}";
        var argsText = "";
        if (function.TryGetProperty("arguments", out var argsValue) && argsValue.ValueKind != JsonValueKind.Null)
        {
            if (argsValue.ValueKind != JsonValueKind.String) throw new ProviderResponseError(Name, $"{Name} returned tool arguments that are not text");
            argsText = argsValue.GetString() ?? "";
        }
        return new ToolCall(callId, name, ParseArguments(argsText));
    }

    private Dictionary<string, object?> ParseArguments(string raw)
    {
        if (raw.Length == 0) return new(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(raw);
            return ToDictionary(document.RootElement) ??
                throw new ProviderResponseError(Name, "returned tool arguments that are not a JSON object");
        }
        catch (JsonException) { throw new ProviderResponseError(Name, "returned invalid tool arguments"); }
    }

    private static Dictionary<string, object?>? ToDictionary(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateObject()) result[item.Name] = JsonValue(item.Value);
        return result;
    }

    private static object? JsonValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => ToDictionary(element),
        JsonValueKind.Array => element.EnumerateArray().Select(JsonValue).ToArray(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var number) => number,
        JsonValueKind.Number when BigInteger.TryParse(element.GetRawText(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) => integer,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Null => null, _ => null
    };

    private ImmutableArray<double> ReadVector(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            throw new ProviderResponseError(Name, "returned an embedding entry that is not an object");
        if (!item.TryGetProperty("embedding", out var vector) || vector.ValueKind != JsonValueKind.Array)
            throw new ProviderResponseError(Name, "returned malformed embedding data");
        if (vector.GetArrayLength() == 0) throw new ProviderResponseError(Name, "returned an empty embedding");
        try { return vector.EnumerateArray().Select(value => value.GetDouble()).ToImmutableArray(); }
        catch (InvalidOperationException) { throw new ProviderResponseError(Name, "returned nonnumeric embedding values"); }
        catch (FormatException) { throw new ProviderResponseError(Name, "returned nonnumeric embedding values"); }
    }

    private static TokenUsage? ReadEmbeddingUsage(JsonElement usage)
    {
        if (usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("prompt_tokens", out var prompt) && prompt.TryGetInt32(out var promptCount))
            return new TokenUsage(promptCount, 0, false);
        return null;
    }

    private static TokenUsage? ReadChunkUsage(JsonElement chunk)
    {
        JsonElement usage;
        if (chunk.TryGetProperty("usage", out usage) && usage.ValueKind != JsonValueKind.Null)
            return ReadUsage(usage);
        if (chunk.TryGetProperty("x_groq", out var groq) && groq.ValueKind == JsonValueKind.Object && groq.TryGetProperty("usage", out usage))
            return ReadUsage(usage);
        return null;
    }

    private static TokenUsage? ReadUsage(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object ||
            !usage.TryGetProperty("prompt_tokens", out var prompt) || !prompt.TryGetInt32(out var promptCount) ||
            !usage.TryGetProperty("completion_tokens", out var completion) || !completion.TryGetInt32(out var completionCount))
            return null;
        return new TokenUsage(promptCount, completionCount, false);
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        { value = property.GetString()!; return true; }
        value = "";
        return false;
    }

    private HttpRequestMessage CreateRequest(string url, object payload, string? correlationId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, InfrastructureJson.Options), Encoding.UTF8, "application/json")
        };
        AddAuthorization(request);
        if (!string.IsNullOrEmpty(correlationId)) request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
        return request;
    }

    private void AddAuthorization(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(_apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken = default)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
        return timeout;
    }
    private string RequireModel() => string.IsNullOrWhiteSpace(_model)
        ? throw new ConfigError($"{Name} chat model is not set") : _model;
    private EmbeddingResult EmptyEmbedding() => new([], _embeddingModel!, 0, new TokenUsage(), Name);

    private ProviderError MapStatus(HttpStatusCode statusCode, string body)
    {
        var snippet = body.Length > BodySnippetLength ? body[..BodySnippetLength] : body;
        if (!string.IsNullOrEmpty(_apiKey)) snippet = snippet.Replace(_apiKey, "[REDACTED]", StringComparison.Ordinal);
        var detail = $"{Name} returned HTTP {(int)statusCode}" + (snippet.Length == 0 ? "" : $": {snippet}");
        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ProviderAuthError(Name, detail),
            HttpStatusCode.TooManyRequests => new QuotaExceededError(Name, detail),
            HttpStatusCode.NotFound or HttpStatusCode.RequestTimeout => new ProviderUnavailableError(Name, detail),
            _ when (int)statusCode >= 500 => new ProviderUnavailableError(Name, detail),
            _ => new ProviderRequestError(Name, detail)
        };
    }

    private JsonDocument Parse(string raw)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(raw); }
        catch (JsonException) { throw new ProviderResponseError(Name, "returned malformed JSON"); }
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new ProviderResponseError(Name, $"{Name} returned a body that is not a JSON object");
        }
        return document;
    }

    private sealed class ToolFragment
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public StringBuilder Arguments { get; } = new();
        public ToolCall ToCall(string provider, int index)
        {
            if (Name.Length == 0) throw new ProviderResponseError(provider, "returned a streamed tool call without a name");
            return new ToolCall(Id.Length == 0 ? $"call_{index}" : Id, Name, ParseArgumentsStatic(Arguments.ToString(), provider));
        }
    }

    private static Dictionary<string, object?> ParseArgumentsStatic(string raw, string provider)
    {
        if (raw.Length == 0) return new(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(raw);
            return ToDictionary(document.RootElement) ?? throw new ProviderResponseError(provider, "returned tool arguments that are not a JSON object");
        }
        catch (JsonException) { throw new ProviderResponseError(provider, "returned invalid tool arguments"); }
    }

    private void Accumulate(JsonElement call, SortedDictionary<int, ToolFragment> fragments)
    {
        if (call.ValueKind != JsonValueKind.Object)
            throw new ProviderResponseError(Name, "sent a tool call that is not an object");
        var index = call.TryGetProperty("index", out var indexValue) && indexValue.TryGetInt32(out var parsed) ? parsed : 0;
        if (!fragments.TryGetValue(index, out var fragment)) fragments[index] = fragment = new ToolFragment();
        if (TryString(call, "id", out var id) && id.Length > 0) fragment.Id = id;
        if (!call.TryGetProperty("function", out var function)) return;
        if (function.ValueKind != JsonValueKind.Object)
            throw new ProviderResponseError(Name, "sent a tool call function that is not an object");
        if (TryString(function, "name", out var name) && name.Length > 0) fragment.Name = name;
        if (TryString(function, "arguments", out var args) && args.Length > 0) fragment.Arguments.Append(args);
    }

    private static BigInteger EmbeddingIndex(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("index", out var index))
            return BigInteger.Zero;
        if (index.ValueKind == JsonValueKind.True) return BigInteger.One;
        if (index.ValueKind == JsonValueKind.False) return BigInteger.Zero;
        return BigInteger.TryParse(index.GetRawText(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : BigInteger.Zero;
    }
}


