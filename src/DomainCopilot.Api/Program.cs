using DomainCopilot.Api;
using DomainCopilot.Infrastructure.Configuration;

// Load workspace .env before the ASP.NET configuration providers are built.
DotEnvLoader.LoadWorkspaceFile();
var app = ApiHost.Build(args);
app.Run();

public partial class Program;
