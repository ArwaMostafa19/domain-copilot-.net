using System.Collections.Concurrent;
using System.Text;

namespace DomainCopilot.Api.Http;

public sealed class LoginRateLimiter
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _attempts = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public bool TryAcquire(string address, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_attempts.TryGetValue(address, out var queue)) _attempts[address] = queue = new Queue<DateTimeOffset>();
            while (queue.Count > 0 && now - queue.Peek() >= TimeSpan.FromSeconds(60)) queue.Dequeue();
            if (queue.Count >= 5) return false;
            queue.Enqueue(now);
            return true;
        }
    }
}

public sealed class ApiSecurityMiddleware(RequestDelegate next, ILogger<ApiSecurityMiddleware> logger)
{
    public const string CorrelationItem = "domain-copilot-correlation-id";
    private const int MaxBodyBytes = 1_048_576;

    public async Task InvokeAsync(HttpContext context, LoginRateLimiter limiter)
    {
        var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault();
        if (string.IsNullOrEmpty(correlationId)) correlationId = Guid.NewGuid().ToString("N");
        context.Items[CorrelationItem] = correlationId;
        AddResponseHeaders(context, correlationId);
        try
        {
            if (context.Request.Path.Equals("/auth/login", StringComparison.OrdinalIgnoreCase) && context.Request.Method == "POST")
            {
                var address = context.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
                if (!limiter.TryAcquire(address, DateTimeOffset.UtcNow))
                {
                    context.Response.Headers.RetryAfter = "60";
                    await WriteProblem(context, StatusCodes.Status429TooManyRequests, "too many login attempts");
                    return;
                }
            }

            var lengthHeader = context.Request.Headers["Content-Length"].FirstOrDefault() ?? "";
            if (lengthHeader.Length > 0 && (!long.TryParse(lengthHeader, out var declaredLength) || declaredLength < 0))
            {
                await WriteProblem(context, StatusCodes.Status400BadRequest, "invalid Content-Length");
                return;
            }
            if (context.Request.ContentLength > MaxBodyBytes)
            {
                await WriteProblem(context, StatusCodes.Status413PayloadTooLarge, "payload too large");
                return;
            }

            if (context.Request.Body.CanRead)
            {
                await using var buffered = new MemoryStream();
                var bytes = new byte[32 * 1024];
                var total = 0;
                while (true)
                {
                    var count = await context.Request.Body.ReadAsync(bytes, context.RequestAborted);
                    if (count == 0) break;
                    total += count;
                    if (total > MaxBodyBytes)
                    {
                        await WriteProblem(context, StatusCodes.Status413PayloadTooLarge, "payload too large");
                        return;
                    }
                    await buffered.WriteAsync(bytes.AsMemory(0, count), context.RequestAborted);
                }
                buffered.Position = 0;
                context.Request.Body = buffered;
                await next(context);
                context.Request.Body = Stream.Null;
                return;
            }
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug("Request cancelled; correlation {CorrelationId}", correlationId);
        }
        finally
        {
            if (!context.Response.HasStarted) AddResponseHeaders(context, correlationId);
        }
    }

    private static void AddResponseHeaders(HttpContext context, string correlationId)
    {
        context.Response.Headers["X-Correlation-ID"] = correlationId;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline';";
        if (context.Request.Path.Equals("/", StringComparison.Ordinal) || context.Request.Path.StartsWithSegments("/static"))
            context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
    }

    private static async Task WriteProblem(HttpContext context, int status, string detail)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { detail }), Encoding.UTF8, context.RequestAborted);
    }
}
