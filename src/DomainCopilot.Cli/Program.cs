using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DomainCopilot.Application;
using DomainCopilot.Application.Ingestion;
using DomainCopilot.Application.Retrieval;
using DomainCopilot.Application.Text;
using DomainCopilot.Domain.Documents;
using DomainCopilot.Domain.Rag;
using DomainCopilot.Infrastructure;
using DomainCopilot.Infrastructure.Configuration;
using DomainCopilot.Infrastructure.Persistence;
using DomainCopilot.Infrastructure.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

DotEnvLoader.LoadWorkspaceFile();
var argsList = args.ToList();
if (argsList.Count == 0 || argsList[0] is "help" or "--help" or "-h") { PrintHelp(); return 0; }
try
{
    var root = FindRoot();
    if (argsList[0] == "llm-smoke") Environment.SetEnvironmentVariable("LLM_CHAIN", Option(argsList, "--provider") ?? "fake");
    var config = new DomainCopilot.Cli.CliConfiguration(root);
    var infra = InfrastructureOptions.FromConfiguration(config);
    var services = new ServiceCollection();
    services.AddDomainCopilotInfrastructure(config);
    using var provider = services.BuildServiceProvider();
    switch (argsList[0])
    {
        case "migrate":
        {
            RequireNoExtra(argsList, 1);
            var applied = provider.GetRequiredService<MigrationRunner>().ProvisionAndApplyMigrations();
            Console.WriteLine(applied.Count == 0 ? "Database role and migrations are already current." : $"Applied {applied.Count} migration(s); application role is configured.");
            break;
        }
        case "seed-users":
        {
            var tenant = Option(argsList, "--tenant");
            var username = Option(argsList, "--username");
            var role = Option(argsList, "--role") ?? "technician";
            if (tenant is null || username is null) throw new ArgumentException("seed-users requires --tenant and --username");
            if (role == "supervisor" && !argsList.Contains("--confirm-supervisor"))
                throw new ArgumentException("provisioning a supervisor requires --confirm-supervisor");
            var password = infra.DemoUserPassword;
            if (string.IsNullOrEmpty(password)) password = ReadPassword("Password: ");
            var created = provider.GetRequiredService<IUserProvisioner>().CreateIfMissing(tenant, username, role, password);
            Console.WriteLine(created ? $"Provisioned {role} user '{username.Trim()}' for tenant '{tenant}'." : "A user with that tenant and username already exists; it was left unchanged.");
            break;
        }
        case "login":
        {
            var tenant = Option(argsList, "--tenant") ?? throw new ArgumentException("login requires --tenant");
            var username = Option(argsList, "--username") ?? throw new ArgumentException("login requires --username");
            await LoginAsync(tenant, username, ReadPassword("Password: "));
            Console.WriteLine("Login succeeded. No bearer token was displayed or persisted.");
            break;
        }
        case "ask":
        {
            var tenant = Option(argsList, "--tenant") ?? throw new ArgumentException("ask requires --tenant");
            var username = Option(argsList, "--username") ?? throw new ArgumentException("ask requires --username");
            var marker = argsList.IndexOf("--question");
            if (marker < 0 || marker + 1 >= argsList.Count) throw new ArgumentException("ask requires --question <text>");
            var question = string.Join(' ', argsList.Skip(marker + 1));
            var token = await LoginAsync(tenant, username, ReadPassword("Password: "));
            using var client = new HttpClient { BaseAddress = ApiBase() };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.PostAsJsonAsync("ask", new { question });
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"API returned HTTP {(int)response.StatusCode}: {SafeApiError(body)}");
            Console.WriteLine(body);
            break;
        }
        case "ingest":
        {
            var corpus = argsList.Count > 1 && !argsList[1].StartsWith("--", StringComparison.Ordinal) ? Path.GetFullPath(argsList[1]) : Path.Combine(root, "corpus");
            if (!Directory.Exists(corpus) && Directory.Exists(Path.Combine(AppContext.BaseDirectory, "corpus"))) corpus = Path.Combine(AppContext.BaseDirectory, "corpus");
            if (!Directory.Exists(corpus)) throw new DirectoryNotFoundException("The shared Corpus directory was not found.");
            var tenantFolders = Directory.EnumerateDirectories(corpus)
                .Where(path => Path.GetFileName(path).StartsWith("tenant-", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).ToArray();
            if (tenantFolders.Length == 0) throw new InvalidOperationException("The Corpus contains no tenant-* document folders.");
            var files = tenantFolders.SelectMany(folder => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                .Where(path => Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal).ToArray();
            var maxChars = int.TryParse(Option(argsList, "--max-chars"), out var parsedMax) ? parsedMax : DocumentChunker.DefaultMaxChars;
            if (maxChars < 1) throw new ArgumentException("--max-chars must be a positive integer");
            if (argsList.Contains("--dry-run"))
            {
                var extractor = provider.GetRequiredService<IDocumentExtractor>();
                var failed = 0;
                foreach (var path in files)
                {
                    try
                    {
                        var document = extractor.Extract(path);
                        var chunks = DocumentChunker.Chunk(document, maxChars);
                        var steps = SafetyStepExtractor.Extract(document);
                        Console.WriteLine($"{document.Meta.TenantId}: {Path.GetFileName(path)} format={document.Meta.SourceFormat} revision={document.Meta.Revision} chunks={chunks.Count} safety_steps={steps.Count}");
                    }
                    catch (DomainError error) { failed++; Console.WriteLine($"failed: {Path.GetFileName(path)}: {error.Message}"); }
                }
                Console.WriteLine($"Dry run checked {files.Length} file(s); failures={failed}. No database or provider was used.");
                if (failed > 0) return 1;
                break;
            }
            var results = DocumentIngestion.IngestFiles(files, provider.GetRequiredService<IEmbedder>(), provider.GetRequiredService<IDocumentRepository>(), provider.GetRequiredService<IDocumentExtractor>(), maxChars);
            foreach (var item in results) Console.WriteLine($"{item.Status}: {Path.GetRelativePath(corpus, item.Path)}{(item.Error is null ? "" : " — " + item.Error)}");
            Console.WriteLine($"Processed {results.Count} source file(s): {results.Count(x => x.Status == "ingested")} ingested, {results.Count(x => x.Status == "skipped")} unchanged, {results.Count(x => x.Status == "failed")} failed.");
            if (results.Any(x => x.Status == "failed")) return 1;
            break;
        }
        case "llm-smoke":
        {
            var selected = Option(argsList, "--provider") ?? "fake";
            if (selected != "fake" && !argsList.Contains("--confirm-live"))
                throw new ArgumentException("non-fake llm-smoke makes a live provider request; add --confirm-live to proceed");
            var chain = provider.GetRequiredService<ILlmProvider>();
            var result = chain.Complete(new DomainCopilot.Domain.Llm.CompletionRequest([new("user", "Reply with the single word OK.")], MaxTokens: 16));
            Console.WriteLine($"Provider '{result.Provider}' returned a completion ({result.Usage.CompletionTokens} completion tokens).");
            break;
        }
        case "eval":
        {
            var fixture = Path.Combine(root, "eval", "golden.jsonl");
            if (!File.Exists(fixture)) fixture = Path.Combine(AppContext.BaseDirectory, "eval", "golden.jsonl");
            if (!File.Exists(fixture)) throw new FileNotFoundException("Golden evaluation fixture is missing.");
            var embedder = provider.GetRequiredService<IEmbedder>();
            var search = provider.GetRequiredService<IChunkSearch>();
            var settings = provider.GetRequiredService<IRetrievalSettings>();
            var count = 0; var withExpectedEvidence = 0; var hits = 0;
            foreach (var line in File.ReadLines(fixture).Where(line => !string.IsNullOrWhiteSpace(line)))
            {
                using var item = JsonDocument.Parse(line);
                var tenant = item.RootElement.GetProperty("tenant").GetString()!;
                var question = item.RootElement.GetProperty("question").GetString()!;
                var expected = item.RootElement.TryGetProperty("evidence", out var evidence) ? evidence.EnumerateArray().ToArray() : [];
                var retrieved = HybridRetrieval.Retrieve(question, tenant, embedder, search, settings);
                count++;
                if (expected.Length == 0) continue;
                withExpectedEvidence++;
                if (expected.All(reference => retrieved.Any(hit => hit.DocId == reference.GetProperty("doc_id").GetString() && hit.Section == reference.GetProperty("section").GetString()))) hits++;
            }
            Console.WriteLine($"Retrieval hit@6: {hits}/{withExpectedEvidence} expected-evidence cases; {count} total cases. No chat completions were requested.");
            break;
        }
        default: throw new ArgumentException($"Unknown command '{argsList[0]}'. Run 'domain-copilot help'.");
    }
    return 0;
}
catch (Exception error)
{
    var safeMessage = error is Npgsql.NpgsqlException or HttpRequestException or TaskCanceledException
        ? $"{error.GetType().Name} occurred; verify service availability and configuration without sharing credentials."
        : error.Message;
    Console.Error.WriteLine($"Error: {safeMessage}");
    return 1;
}

static async Task<string> LoginAsync(string tenant, string username, string password)
{
    using var client = new HttpClient { BaseAddress = ApiBase() };
    using var response = await client.PostAsJsonAsync("auth/login", new { tenant_id = tenant, username, password });
    if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Login failed (HTTP {(int)response.StatusCode}).");
    using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return payload.RootElement.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("Login response omitted its bearer token.");
}
static Uri ApiBase() => new(Environment.GetEnvironmentVariable("API_BASE_URL") ?? "http://localhost:8000/");
static string? Option(List<string> values, string name) { var index = values.IndexOf(name); return index >= 0 && index + 1 < values.Count ? values[index + 1] : null; }
static void RequireNoExtra(List<string> values, int expected) { if (values.Count != expected) throw new ArgumentException("migrate takes no arguments"); }
static string ReadPassword(string prompt)
{
    var supplied = Environment.GetEnvironmentVariable("DOMAINCOPILOT_PASSWORD");
    if (!string.IsNullOrEmpty(supplied)) return supplied;
    if (Console.IsInputRedirected) throw new InvalidOperationException("Set DOMAINCOPILOT_PASSWORD in the current process or run interactively; the password is never accepted as a command-line argument.");
    Console.Write(prompt); var chars = new List<char>();
    while (true) { var key = Console.ReadKey(intercept: true); if (key.Key == ConsoleKey.Enter) break; if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; } if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar); }
    Console.WriteLine(); return new string(chars.ToArray());
}
static string SafeApiError(string body)
{
    try { using var json = JsonDocument.Parse(body); if (json.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String) return detail.GetString() ?? "request rejected"; } catch (JsonException) { }
    return "request rejected";
}
static string FindRoot()
{
    var current = new DirectoryInfo(Environment.CurrentDirectory);
    while (current is not null) { if (File.Exists(Path.Combine(current.FullName, "DomainCopilot.sln"))) return current.FullName; current = current.Parent; }
    if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "corpus"))) return AppContext.BaseDirectory;
    throw new DirectoryNotFoundException("Run this command from the .NET workspace or a published CLI directory.");
}
static void PrintHelp() => Console.WriteLine("""
Domain Copilot .NET CLI
  help
  migrate
  seed-users --tenant ID --username NAME [--role technician|supervisor] [--confirm-supervisor]
  login --tenant ID --username NAME
  ask --tenant ID --username NAME --question TEXT
  ingest [shared-corpus-directory] [--dry-run] [--max-chars N]
  llm-smoke [--provider fake|groq|gemini|ollama] [--confirm-live]
  eval
Passwords are read without echo from the console, or from DOMAINCOPILOT_PASSWORD in this process.
'migrate' requires ADMIN_DATABASE_URL and APP_DB_PASSWORD; it never runs at API startup.
'seed-users' uses DEMO_USER_PASSWORD when set, otherwise prompts; supervisor creation requires explicit confirmation.
'ask' calls the .NET API. 'eval' runs retrieval-only against eval/golden.jsonl and the configured database/index.
""");










