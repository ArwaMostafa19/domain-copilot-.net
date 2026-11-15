using DomainCopilot.Api.Configuration;
using DomainCopilot.Api.Http;
using DomainCopilot.Application;
using DomainCopilot.Application.Workflow;
using DomainCopilot.Infrastructure.Providers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DomainCopilot.Api;

public static class ApiHost
{
    public static WebApplication Build(string[] args, Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddLegacyEnvironmentAliases();

        var parsedOptions = new CopilotOptions();
        CopilotConfiguration.Bind(builder.Configuration, parsedOptions);
        builder.Services.AddSingleton(parsedOptions);
        builder.Services.AddOptions<CopilotOptions>()
            .Configure<IConfiguration>((options, configuration) => CopilotConfiguration.Bind(configuration, options))
            .Validate(options => options.LlmChain.Count > 0, "LLM_CHAIN must contain at least one provider")
            .ValidateOnStart();

        builder.Services.AddDomainCopilotInfrastructure(builder.Configuration);
        builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        builder.Services.AddAuthentication(PythonBearerHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, PythonBearerHandler>(PythonBearerHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        var allowedOrigins = parsedOptions.CorsOrigins.ToArray();
        builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
            .SetIsOriginAllowed(origin => allowedOrigins.Contains("*", StringComparer.Ordinal) || allowedOrigins.Contains(origin, StringComparer.Ordinal))
            .WithMethods("GET", "POST", "OPTIONS")
            .WithHeaders("Authorization", "Content-Type", "X-Correlation-ID")
            .AllowCredentials()));
        builder.Services.AddSingleton<LoginRateLimiter>();
        builder.Services.AddScoped<WorkflowOrchestrator>(services =>
        {
            var settings = services.GetRequiredService<IOptions<CopilotOptions>>().Value;
            return new WorkflowOrchestrator(
                services.GetRequiredService<IRunRepository>(), services.GetRequiredService<IWorkOrderRepository>(),
                services.GetRequiredService<IAuditLog>(), services.GetRequiredService<ISafetyPrerequisiteRepository>(),
                services.GetRequiredService<IEmbedder>(), services.GetRequiredService<IChunkSearch>(),
                services.GetRequiredService<ILlmProvider>(), services.GetRequiredService<IRetrievalSettings>(),
                settings.RunMaxModelCalls, settings.RunTokenBudget, settings.RunTimeoutSeconds, settings.RunStepTimeoutSeconds);
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseExceptionHandler(handler => handler.Run(async context =>
        {
            var error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ApiErrors");
            var providerFailure = error is DomainCopilot.Domain.Llm.LLMError;
            var databaseFailure = error is Npgsql.NpgsqlException;
            var validationFailure = error is BadHttpRequestException;
            var correlationId = Convert.ToString(context.Items[ApiSecurityMiddleware.CorrelationItem], System.Globalization.CultureInfo.InvariantCulture) ?? "cid-missing";
            context.Response.Headers["X-Correlation-ID"] = correlationId;
            logger.LogError("Request failed ({FailureType}); correlation {CorrelationId}", error?.GetType().Name ?? "unknown", correlationId);
            context.Response.StatusCode = providerFailure ? StatusCodes.Status502BadGateway :
                databaseFailure ? StatusCodes.Status503ServiceUnavailable :
                validationFailure ? StatusCodes.Status422UnprocessableEntity : StatusCodes.Status500InternalServerError;
            var detail = providerFailure ? "provider unavailable" : databaseFailure ? "database service unavailable" : "internal server error";
            if (validationFailure)
                await Results.Json(new { detail = new[] { new { type = "json_invalid", loc = new[] { "body" }, msg = "Invalid JSON body" } } }, statusCode: context.Response.StatusCode).ExecuteAsync(context);
            else if (providerFailure || databaseFailure)
                await Results.Json(new { detail }, statusCode: context.Response.StatusCode).ExecuteAsync(context);
            else
                await Results.Json(new { error = "internal error", correlation_id = correlationId }, statusCode: context.Response.StatusCode).ExecuteAsync(context);
        }));
        app.UseMiddleware<ApiSecurityMiddleware>();
        app.UseStatusCodePages(async statusContext =>
        {
            var context = statusContext.HttpContext;
            if (context.Response.StatusCode != StatusCodes.Status404NotFound) return;
            context.Response.ContentType = "application/json";
            var correlationId = Convert.ToString(context.Items[ApiSecurityMiddleware.CorrelationItem], System.Globalization.CultureInfo.InvariantCulture) ?? "cid-missing";
            await context.Response.WriteAsJsonAsync(new { error = "not found", correlation_id = correlationId }, context.RequestAborted);
        });
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();

        var staticPath = Path.Combine(app.Environment.ContentRootPath, "static");
        if (Directory.Exists(staticPath))
            app.UseStaticFiles(new StaticFileOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(staticPath), RequestPath = "/static" });

        app.MapCopilotEndpoints();
        return app;
    }
}
