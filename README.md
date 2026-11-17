# Domain Copilot (.NET 9)

Tenant-scoped industrial maintenance assistant. The current .NET API and CLI are independent of Python at runtime. The active document corpus is the real directory at corpus/.

> **Support status:** all projects target net9.0 under the approved migration boundary. .NET 9 support ended on November 10, 2026 and no longer receives Microsoft security servicing. Do not deploy it as a production service until an upgrade to a supported release is approved. See [Microsoft's .NET support policy](https://learn.microsoft.com/dotnet/core/releases-and-support).

## Prerequisites

- .NET 9 SDK for host builds ([Microsoft download page](https://dotnet.microsoft.com/download/dotnet/9.0)); note that .NET 9 is out of support.
- Docker Desktop or Docker Engine with Docker Compose v2.
- PowerShell for scripts.
- PostgreSQL 16 with pgvector; Compose uses pgvector/pgvector:pg16.
- An embedding provider/model for meaningful ingestion and retrieval. Fake providers are for offline smoke tests only.

## Fresh Docker installation

Run in PowerShell from the repository root. A new Compose project name creates a separate persistent database volume; reuse that name for this installation later.

1. Copy the template with Copy-Item .env.example .env. In .env, set separate local passwords for the PostgreSQL administrator and copilot_app. APP_DB_PASSWORD must match the password in DATABASE_URL. Set AUTH_SECRET to a random value at least 32 characters long. Never commit or share .env.
2. For offline smoke testing, set LLM_CHAIN=fake and EMBEDDING_PROVIDER=fake. This does not require provider keys, but fake embeddings are not useful for semantic search quality.
3. Start the database using a unique project name:

   $projectName = "dcnet-local-20261117-a1b2"
   docker compose -p $projectName up -d db
   docker compose -p $projectName ps

   Wait for the database to be healthy. Host port 5432 is loopback-only. Compose names its persistent volume from the project name.

4. Provision the restricted application role and apply ordered SQL migrations:

   .\scripts\run-cli.ps1 migrate

   This explicit CLI command uses ADMIN_DATABASE_URL and APP_DB_PASSWORD. API startup never migrates. It is safe to repeat: recorded migrations are skipped and the configured copilot_app password is reconciled.

5. Create a supervisor:

   .\scripts\run-cli.ps1 seed-users --tenant tenant-alpha --username supervisor --role supervisor --confirm-supervisor

   DEMO_USER_PASSWORD is used if set; otherwise the CLI prompts without echo. Supervisor creation requires admin access and the confirmation flag. An existing user's password and role are left unchanged. Use a different username for tenant-beta.

6. Start the API and open http://127.0.0.1:18080/:

   docker compose -p $projectName up --build -d api
   docker compose -p $projectName ps

   Host port 18080 maps to API container port 8080. Check:

   Invoke-RestMethod http://127.0.0.1:18080/health
   Invoke-RestMethod http://127.0.0.1:18080/ready

   /health is liveness only. /ready checks database and chat-chain readiness. Neither migrates or seeds data.

7. After migrations and embedding setup, ingest the shared corpus:

   .\scripts\run-cli.ps1 ingest

   It reads Markdown/PDF from corpus and writes derived records to PostgreSQL, not back to files. It rechecks on each run and skips unchanged content, but ingestion does write to the database.

8. Sign in in the browser or test login:

   .\scripts\run-cli.ps1 login --tenant tenant-alpha --username supervisor

   The CLI prompts for the password and neither displays nor saves the bearer token.

## Run API on the host

Start PostgreSQL in Compose, apply migrations, and provision a user first. Then run .\scripts\run-api.ps1 from the repository root. Its default launch profile listens on http://localhost:5100/. The HTTPS launch profile is https://localhost:7251/ and also enables HTTP 5100; start it with dotnet run --project src/DomainCopilot.Api --launch-profile https.

For CLI requests to a host-run API, set $env:API_BASE_URL = "http://localhost:5100/" in the current PowerShell process. The run-cli.ps1 wrapper defaults to the Docker API at port 18080. The API script changes Compose hostnames to localhost only for values read from .env. Container-to-container traffic uses service names. Port 8080 is container-internal, not a host URL.

## Fresh database versus existing deployment

- For a fresh install, use a new Compose project name once, then migrate, seed, and ingest.
- For an existing deployment, reuse its existing project name and volume. Do not choose a new name just to restart the API.
- migrate applies unrecorded migrations; seed-users leaves existing accounts unchanged; compose up does not clear database data.
- Never use docker compose down -v as normal setup: -v deletes the database volume.

## Configuration

API and CLI load the workspace .env before building configuration; a process environment variable already present is not overwritten by .env. In options parsing, a supplied legacy uppercase key such as DATABASE_URL takes precedence over Copilot__DatabaseUrl. Otherwise standard .NET configuration providers apply (including environment variables over appsettings.json), then code defaults. This repository has no deployment appsettings file. Compose passes required API settings explicitly.

| Variable | Purpose |
| --- | --- |
| POSTGRES_USER, POSTGRES_PASSWORD, POSTGRES_DB | Initialize PostgreSQL on first volume creation; changing these does not reset an existing database account. |
| ADMIN_DATABASE_URL | Administrative connection for explicit migrate and seed-users operations. |
| APP_DB_PASSWORD | Password provisioned for restricted role copilot_app. |
| DATABASE_URL | Application/API connection; must log in as copilot_app, not administrator. |
| AUTH_SECRET | Bearer-token signing secret, at least 32 characters. |
| LLM_CHAIN | Ordered chat providers: groq, gemini, ollama, fake. |
| GROQ_API_KEY, GROQ_BASE_URL, GROQ_MODEL, GROQ_STREAM_USAGE | Groq settings; key required when selected. |
| GEMINI_API_KEY, GEMINI_BASE_URL, GEMINI_MODEL, GEMINI_STREAM_USAGE | Gemini settings; key required when selected. |
| OLLAMA_BASE_URL, OLLAMA_CHAT_MODEL | Ollama endpoint/model; Compose defaults to host gateway. |
| EMBEDDING_PROVIDER, EMBEDDING_MODEL, EMBEDDING_DIMENSIONS | Embedding settings; providers ollama, gemini, fake. Schema requires 768 dimensions. |
| LLM_TIMEOUT_SECONDS, LLM_COOLDOWN_SECONDS | Provider timeout and cooldown. |
| RETRIEVAL_MIN_SIMILARITY | Similarity threshold from 0 through 1. |
| RUN_MAX_MODEL_CALLS, RUN_TOKEN_BUDGET, RUN_TIMEOUT_SECONDS, RUN_STEP_TIMEOUT_SECONDS | Workflow budgets. |
| TOKEN_TTL_SECONDS | Token lifetime. |
| CORS_ORIGINS | Comma-separated allowed origins; same-origin frontend needs none. |
| MAX_INGEST_FILE_BYTES | Maximum ingestion file size. |
| DEMO_USER_PASSWORD | Optional seed password; otherwise prompt. Never pass passwords as CLI arguments. |
| API_BASE_URL | CLI login/ask target. run-cli.ps1 defaults to http://localhost:18080/; use port 5100 for host-run API. |

DOMAINCOPILOT_PASSWORD is another process-only CLI login/ask input. Treat it as secret and clear it after use.

## Authentication, API, and CLI

Tenants are tenant-alpha and tenant-beta; roles are technician and supervisor. The app issues a Python-compatible two-segment base64url JSON/HMAC-SHA256 token, not a standard three-segment JWT. Protected requests validate it and reload the active user's current role. Do not log passwords or tokens.

The root serves the static frontend. Implemented route and JSON/SSE contracts are in [docs/API.md](docs/API.md). Swagger, /docs, and /openapi.json are not implemented. CLI commands are help, migrate, seed-users, login, ask, ingest, llm-smoke, and eval; details are in docs/CLI.md. llm-smoke needs --confirm-live for external providers. eval measures retrieval hit@6 only and does not generate or score answers. Former Python copilot run/acknowledge/approve commands were stubs; workflow is in the .NET API/UI.

## Tests and CI

Each of Domain, Application, Infrastructure, API, and Configuration has a package-free console runner:

    dotnet build DomainCopilot.sln --configuration Release
    dotnet run --project tests/DomainCopilot.DomainTests --configuration Release
    dotnet run --project tests/DomainCopilot.ApplicationTests --configuration Release
    dotnet run --project tests/DomainCopilot.InfrastructureTests --configuration Release
    dotnet run --project tests/DomainCopilot.ApiTests --configuration Release
    dotnet run --project tests/DomainCopilot.ConfigurationTests --configuration Release

Infrastructure includes provider and extractor tests. PostgreSQL opt-in requires a newly created disposable database and the checks in PostgresOptInIntegrationTests.cs; never point it to user or production data. Current CI disables that suite and skips the Python extraction-signature comparison with DOMAINCOPILOT_SKIP_PYTHON_EXTRACTION_PARITY=1. Skipped checks are not passes. The gitleaks job is disabled; the remaining build, tests, NuGet audit, and Docker build remain enabled. Confirm the GitHub run before claiming CI passed.

tests/unit/test_corpus.py is optional Python corpus-authoring validation, not .NET runtime. eval/baseline.json contains historical Python measurements, not .NET results. .NET eval currently measures retrieval only; no answer-quality/refusal-calibration result is documented.


## Demo videos

1. **Slide presentation** — [https://drive.google.com/drive/folders/1ztD-UBLIeWyhsi1Jy4G-9TIYv6M7YKfD?usp=sharing](#)
2. **Project demo** — [https://drive.google.com/drive/folders/1SAxbr6ZxXGkH3hb_B8sA4PW2r4YBUUxU?usp=sharing](#)


