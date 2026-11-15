using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Numerics;
using System.Text.Json;
using DomainCopilot.Application;
using DomainCopilot.Domain.Llm;

namespace DomainCopilot.Infrastructure.Providers;

/// <summary>Ollama /api/chat and /api/embed adapter matching the original request contract.</summary>
public sealed class OllamaProvider : ILlmProvider
{
    private const int BatchSize = 16;
    private const int SnippetLength = 200;
    private readonly HttpClient _client;
    private readonly string _baseUrl;
    private readonly string _embeddingModel;
    private readonly int _dimensions;
    private readonly string? _chatModel;
    private readonly double _timeoutSeconds;

    public OllamaProvider(HttpClient client, string baseUrl, string embeddingModel, int dimensions,
        string? chatModel, double timeoutSeconds, string name = "ollama")
    {
        _client = client;
        _baseUrl = baseUrl.TrimEnd('/');
        _embeddingModel = embeddingModel;
        _dimensions = dimensions;
        _chatModel = chatModel;
        _timeoutSeconds = timeoutSeconds;
        Name = name;
    }

    public string Name { get; }

    public EmbeddingResult Embed(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0) return new([], _embeddingModel, _dimensions, new TokenUsage(), Name);
        var vectors = ImmutableArray.CreateBuilder<ImmutableArray<double>>(texts.Count);
        var promptTokens = 0;
        var estimated = false;
        using var timeout = CreateTimeout();
        for (var offset = 0; offset < texts.Count; offset += BatchSize)
        {
            var batch = texts.Skip(offset).Take(BatchSize).ToArray();
            using var request = JsonRequest("/api/embed", new { model = _embeddingModel, input = batch });
            using var response = Send(request, timeout.Token, "Ollama is not reachable");
            CheckStatus(response, _embeddingModel);
            using var document = Parse(response.Content.ReadAsStringAsync(timeout.Token).GetAwaiter().GetResult());
            if (!document.RootElement.TryGetProperty("embeddings", out var embeddings) ||
                embeddings.ValueKind != JsonValueKind.Array || embeddings.GetArrayLength() != batch.Length)
                throw new ProviderResponseError(Name, $"Ollama returned an unexpected embedding count for {batch.Length} texts");
            foreach (var item in embeddings.EnumerateArray())
            {
                var vector = ReadVector(item);
                if (vector.Length != _dimensions)
                    throw new ProviderResponseError(Name, $"Ollama returned {vector.Length} values, expected {_dimensions}");
                vectors.Add(vector);
            }
            if (document.RootElement.TryGetProperty("prompt_eval_count", out var count) && count.TryGetInt32(out var tokens))
                promptTokens += tokens;
            else estimated = true;
        }
        return new EmbeddingResult(vectors.MoveToImmutable(), _embeddingModel, _dimensions,
            new TokenUsage(promptTokens, 0, estimated), Name);
    }

    public CompletionResult Complete(CompletionRequest request)
    {
        var model = RequireChatModel();
        using var timeout = CreateTimeout();
        using var httpRequest = JsonRequest("/api/chat", ChatPayload(request, false));
        if (!string.IsNullOrEmpty(request.CorrelationId))
            httpRequest.Headers.TryAddWithoutValidation("X-Correlation-ID", request.CorrelationId);
        using var response = Send(httpRequest, timeout.Token, $"could not reach Ollama at {_baseUrl}");
        CheckStatus(response, model);
        using var document = Parse(response.Content.ReadAsStringAsync(timeout.Token).GetAwaiter().GetResult());
        var root = document.RootElement;
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            throw new ProviderResponseError(Name, "Ollama returned a response without a message object");
        var content = !message.TryGetProperty("content", out var contentValue)
            ? ""
            : contentValue.ValueKind == JsonValueKind.String ? contentValue.GetString()!
                : throw new ProviderResponseError(Name, "Ollama returned message content that is not text");
        var calls = ReadFunctions(message, "tool_calls");
        return new CompletionResult(content, calls, ReadUsage(root, request, content), Name, model,
            String(root, "done_reason") is { Length: > 0 } reason ? reason : "stop");
    }

    public IEnumerable<StreamEvent> Stream(CompletionRequest request, CancellationToken cancellationToken = default)
    {
        var model = RequireChatModel();
        using var timeout = CreateTimeout(cancellationToken);
        using var httpRequest = JsonRequest("/api/chat", ChatPayload(request, true));
        if (!string.IsNullOrEmpty(request.CorrelationId))
            httpRequest.Headers.TryAddWithoutValidation("X-Correlation-ID", request.CorrelationId);
        HttpResponseMessage response;
        try { response = _client.Send(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { throw new ProviderUnavailableError(Name, "request timed out"); }
        catch (HttpRequestException) { throw new ProviderUnavailableError(Name, $"could not reach Ollama at {_baseUrl}"); }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string body;
                try { body = response.Content.ReadAsStringAsync(timeout.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                { throw new ProviderUnavailableError(Name, "request timed out"); }
                throw StatusError(response.StatusCode, body, model);
            }
            Stream stream;
            try { stream = response.Content.ReadAsStream(timeout.Token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            { throw new ProviderUnavailableError(Name, "request timed out"); }
            using (stream)
            {
            using var reader = new StreamReader(stream);
            var content = new StringBuilder();
            var functions = new List<(string Name, Dictionary<string, object?> Arguments)>();
            TokenUsage? usage = null;
            var finishReason = "stop";
            while (true)
            {
                string? line;
                try { line = reader.ReadLineAsync(timeout.Token).GetAwaiter().GetResult(); }
                catch (IOException) { throw new ProviderUnavailableError(Name, "Ollama stream was interrupted"); }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { throw new ProviderUnavailableError(Name, "request timed out"); }
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
                {
                    if (String(message, "content") is { Length: > 0 } token)
                    {
                        content.Append(token);
                        yield return new StreamEvent("token", token);
                    }
                    functions.AddRange(ReadFunctionPairs(message, "tool_calls"));
                }
                if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
                {
                    if (root.TryGetProperty("prompt_eval_count", out var prompt) && prompt.TryGetInt32(out var promptCount) &&
                        root.TryGetProperty("eval_count", out var completion) && completion.TryGetInt32(out var completionCount))
                        usage = new TokenUsage(promptCount, completionCount, false);
                    finishReason = String(root, "done_reason") is { Length: > 0 } reason ? reason : "stop";
                }
            }

            var fullText = content.ToString();
            var finalUsage = usage ?? TokenEstimation.EstimateUsage(request.PromptText(), fullText);
            var toolCalls = functions.Select((function, index) => new ToolCall($"call_{index}", function.Name, function.Arguments)).ToImmutableArray();
            yield return new StreamEvent("done", Result: new CompletionResult(fullText, toolCalls, finalUsage, Name, model, finishReason));
            }
        }
    }

    public bool Healthcheck()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(5, _timeoutSeconds)));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/tags");
        try { using var response = _client.Send(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); return response.IsSuccessStatusCode; }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { return false; }
    }

    private object ChatPayload(CompletionRequest request, bool stream)
    {
        var messages = request.Messages.Select(message =>
        {
            var item = new Dictionary<string, object?> { ["role"] = message.Role, ["content"] = message.Content };
            if (!message.ToolCalls.IsEmpty)
                item["tool_calls"] = message.ToolCalls.Select(call => new
                {
                    function = new { name = call.Name, arguments = call.Arguments }
                }).ToArray();
            return item;
        }).ToArray();
        var options = new Dictionary<string, object?> { ["temperature"] = request.Temperature };
        if (request.MaxTokens is not null) options["num_predict"] = request.MaxTokens.Value;
        var payload = new Dictionary<string, object?>
        {
            ["model"] = RequireChatModel(), ["messages"] = messages, ["stream"] = stream, ["options"] = options
        };
        if (!request.Tools.IsEmpty)
            payload["tools"] = request.Tools.Select(tool => new
            {
                type = "function",
                function = new { name = tool.Name, description = tool.Description, parameters = tool.Parameters }
            }).ToArray();
        return payload;
    }

    private ImmutableArray<ToolCall> ReadFunctions(JsonElement message, string property) =>
        ReadFunctionPairs(message, property).Select((function, index) =>
            new ToolCall($"call_{index}", function.Name, function.Arguments)).ToImmutableArray();

    private List<(string Name, Dictionary<string, object?> Arguments)> ReadFunctionPairs(JsonElement message, string property)
    {
        var result = new List<(string, Dictionary<string, object?>)>();
        if (!message.TryGetProperty(property, out var calls) || calls.ValueKind == JsonValueKind.Null) return result;
        if (calls.ValueKind != JsonValueKind.Array) throw new ProviderResponseError(Name, "Ollama returned malformed tool_calls");
        foreach (var item in calls.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object ||
                String(function, "name") is not { Length: > 0 } name ||
                !function.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object)
                throw new ProviderResponseError(Name, "Ollama returned a malformed tool call");
            result.Add((name, ToDictionary(arguments)));
        }
        return result;
    }

    private ImmutableArray<double> ReadVector(JsonElement vector)
    {
        if (vector.ValueKind != JsonValueKind.Array) throw new ProviderResponseError(Name, "Ollama returned a non-list embedding");
        try { return vector.EnumerateArray().Select(x => x.GetDouble()).ToImmutableArray(); }
        catch (InvalidOperationException) { throw new ProviderResponseError(Name, "Ollama returned nonnumeric embedding values"); }
        catch (FormatException) { throw new ProviderResponseError(Name, "Ollama returned nonnumeric embedding values"); }
    }

    private static TokenUsage ReadUsage(JsonElement root, CompletionRequest request, string content)
    {
        if (root.TryGetProperty("prompt_eval_count", out var prompt) && prompt.TryGetInt32(out var p) &&
            root.TryGetProperty("eval_count", out var completion) && completion.TryGetInt32(out var c))
            return new TokenUsage(p, c, false);
        return TokenEstimation.EstimateUsage(request.PromptText(), content);
    }

    private HttpResponseMessage Send(HttpRequestMessage request, CancellationToken token, string unreachable)
    {
        try { return _client.Send(request, HttpCompletionOption.ResponseContentRead, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw new ProviderUnavailableError(Name, "request timed out"); }
        catch (HttpRequestException) { throw new ProviderUnavailableError(Name, unreachable); }
    }

    private void CheckStatus(HttpResponseMessage response, string model)
    {
        if (!response.IsSuccessStatusCode)
            throw StatusError(response.StatusCode, response.Content.ReadAsStringAsync().GetAwaiter().GetResult(), model);
    }

    private ProviderError StatusError(System.Net.HttpStatusCode status, string body, string model)
    {
        var snippet = body.Length > SnippetLength ? body[..SnippetLength] : body;
        var detail = $"Ollama returned HTTP {(int)status}" + (snippet.Length == 0 ? "" : $": {snippet}");
        if (status == System.Net.HttpStatusCode.NotFound)
            return new ProviderUnavailableError(Name, $"{detail}; run: ollama pull {model}");
        return (int)status >= 500
            ? new ProviderUnavailableError(Name, detail)
            : new ProviderRequestError(Name, detail);
    }

    private HttpRequestMessage JsonRequest(string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, InfrastructureJson.Options), Encoding.UTF8, "application/json")
        };
        return request;
    }

    private string RequireChatModel() => string.IsNullOrWhiteSpace(_chatModel)
        ? throw new ConfigError("OLLAMA_CHAT_MODEL is not set") : _chatModel;

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken = default)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
        return timeout;
    }

    private JsonDocument Parse(string body)
    {
        try
        {
            var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            { document.Dispose(); throw new ProviderResponseError(Name, "Ollama response body is not a JSON object"); }
            return document;
        }
        catch (JsonException) { throw new ProviderResponseError(Name, "Ollama returned malformed JSON"); }
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static Dictionary<string, object?> ToDictionary(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) result[property.Name] = ConvertValue(property.Value);
        return result;
    }

    private static object? ConvertValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => ToDictionary(element),
        JsonValueKind.Array => element.EnumerateArray().Select(ConvertValue).ToArray(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var number) => number,
        JsonValueKind.Number when BigInteger.TryParse(element.GetRawText(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) => integer,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Null => null, _ => null
    };
}
