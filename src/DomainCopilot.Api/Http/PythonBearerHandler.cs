using System.Security.Claims;
using System.Text.Encodings.Web;
using DomainCopilot.Application;
using DomainCopilot.Application.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DomainCopilot.Api.Http;

public sealed class PythonBearerHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IServiceProvider services,
    IOptions<Configuration.CopilotOptions> configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "PythonCompatibleBearer";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());
        var token = header[7..].Trim();
        try
        {
            var user = Authentication.VerifyAndLoad(token, configuration.Value.AuthSecret,
                () => services.GetRequiredService<IUserRepository>());
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, Convert.ToString(user["user_id"], System.Globalization.CultureInfo.InvariantCulture)!),
                new Claim("tenant_id", Convert.ToString(user["tenant_id"], System.Globalization.CultureInfo.InvariantCulture)!),
                new Claim(ClaimTypes.Role, Convert.ToString(user["role"], System.Globalization.CultureInfo.InvariantCulture)!),
                new Claim(ClaimTypes.Name, Convert.ToString(user["username"], System.Globalization.CultureInfo.InvariantCulture)!)
            };
            var identity = new ClaimsIdentity(claims, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
        catch (AuthError)
        {
            return Task.FromResult(AuthenticateResult.Fail("unauthorized"));
        }
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.ContentType = "application/json";
        await System.Text.Json.JsonSerializer.SerializeAsync(Response.Body,
            new { error = "unauthorized", correlation_id = CorrelationId() }, cancellationToken: Context.RequestAborted);
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        Response.ContentType = "application/json";
        await System.Text.Json.JsonSerializer.SerializeAsync(Response.Body,
            new { error = "forbidden", correlation_id = CorrelationId() }, cancellationToken: Context.RequestAborted);
    }

    private string CorrelationId() =>
        Convert.ToString(Context.Items[ApiSecurityMiddleware.CorrelationItem], System.Globalization.CultureInfo.InvariantCulture) ?? "cid-missing";
}
