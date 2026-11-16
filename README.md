# Domain Copilot (.NET 9)

Tenant-scoped industrial-maintenance assistant. The .NET API and CLI run without Python. An independent, read-only Corpus copy is packaged from `./corpus`.

> **Runtime support:** all projects remain on `net9.0` per the approved migration boundary. .NET 9 support ended in November 2026; it no longer receives security servicing. Plan an explicitly approved retarget to a supported .NET release before production deployment. See [Microsoft .NET support policy](https://learn.microsoft.com/dotnet/core/releases-and-support).

## Fresh installation

1. Copy `.env.example` to `.env`. Set unique PostgreSQL admin and app-role passwords, use the app password in both `APP_DB_PASSWORD` and the `DATABASE_URL` password, set an `AUTH_SECRET` of at least 32 characters, and configure a provider. `.env` is ignored by Git and excluded from Docker build context.
2. Choose a new unique Compose project name and start PostgreSQL 16 + pgvector, for example `docker compose -p dcnet-local-20261114-x7k2 up -d db`. The isolated project gets its own new `pgdata` volume; do not reuse an old project name when you want a fresh database.
3. Apply schema migrations and provision the restricted `copilot_app` role explicitly: `./scripts/run-cli.ps1 migrate` (PowerShell) or `dotnet run --project src/DomainCopilot.Cli -- migrate` from the workspace root. This command uses the administrative connection, configures the application role and applies ordered migrations. It is never run by API startup.
4. Create an initial user explicitly: `./scripts/run-cli.ps1 seed-users --tenant tenant-alpha --username supervisor --role supervisor --confirm-supervisor`. The command uses `DEMO_USER_PASSWORD` if set, otherwise prompts without echo; it never accepts passwords as command-line arguments. Supervisor creation requires both the separate admin database credential and the explicit confirmation flag. Repeating the command does not replace an existing user's password or role.
5. Start the API and database using the same unique project name, for example `docker compose -p dcnet-local-20261114-x7k2 up --build -d`. Browse to <http://127.0.0.1:18080/>. The API serves the static frontend and its API routes. For host execution use `./scripts/run-api.ps1`; the script loads `.env`, adjusts only `.env`-derived Compose service hostnames for host access, and does not print credentials.
6. Ingest the shared Corpus explicitly after migrations and provider setup: `./scripts/run-cli.ps1 ingest`. This invokes the .NET extractors, embedding provider, and tenant-scoped repository. It does not change Corpus files. Choose `EMBEDDING_PROVIDER=fake` for deterministic offline development; real provider requests occur only when an operator runs ingestion or an AI command.
7. Sign in through the frontend, or use `./scripts/run-cli.ps1 login --tenant tenant-alpha --username supervisor`. The CLI prompts for the password and does not display or persist the returned bearer token.

For an offline API configuration, set `LLM_CHAIN=fake` and `EMBEDDING_PROVIDER=fake`. Hosted providers require their respective keys. Ollama uses `OLLAMA_BASE_URL`; inside Compose the default targets the host gateway. Do not use real provider credentials in tests.

## Configuration

.NET configuration precedence is code defaults, then `appsettings.json`, then nested `Copilot__SettingName` environment values, then legacy flat environment names. The API and CLI load the workspace `.env` before configuration is built; explicitly supplied OS variables are not overwritten. `.env.example` contains placeholders and safe defaults only. Provider settings and timeouts are parsed through the shared Infrastructure configuration parser; malformed or out-of-range values fail clearly without printing secrets. Compose explicitly passes provider and application settings to the API container, including `OLLAMA_BASE_URL`.

The Python-compatible bearer token is a two-part `base64url(JSON).base64url(HMAC-SHA256)` value, not a three-part JWT. Its signing secret must be at least 32 characters. Sign-in reloads active user and role data from the database. Do not paste tokens into support logs.

## API contract

The application does not expose Swagger, `/docs`, `/redoc`, or `/openapi.json`; the accurate .NET route contract is in [docs/API.md](docs/API.md). The root route serves the frontend, and `GET /health` is liveness only. `GET /ready` checks database and configured provider readiness. Neither endpoint runs migrations or seeds users.

## CLI

See [docs/CLI.md](docs/CLI.md) for commands and examples, and [docs/EVALUATION.md](docs/EVALUATION.md) for the exact native evaluation scope. Available commands: `help`, `migrate`, `seed-users`, `login`, `ask`, `ingest`, `llm-smoke`, and retrieval-only `eval`. `llm-smoke` uses the fake provider by default; selecting a real provider requires `--confirm-live`. `ask` calls the .NET API and prompts for credentials. `eval` uses the shared golden JSONL fixture and existing .NET retrieval/database/provider architecture; it does not invoke Python or issue chat completions.

## Docker and operations

Compose builds the .NET 9 API and PostgreSQL 16 + pgvector. PostgreSQL data is stored in the named `pgdata` volume for normal persistent deployments. API startup does not apply migrations or seed data. `ADMIN_DATABASE_URL` is used only by explicit CLI migration and provisioning commands; `DATABASE_URL` must authenticate as the restricted `copilot_app` role. Keep these credentials separate. To inspect API health use `http://localhost:8000/health`; readiness is `http://localhost:8000/ready`.

## Scope and limitations

- The .NET application has no Python runtime dependency. Optional corpus authoring and corpus-invariant checks use scripts under scripts; they are not involved in build, startup, ingestion, or requests.
- The independent `./corpus` directory is packaged for API publish and mounted read-only by Compose; the separate Python project is not needed at runtime.
- No live provider request is part of build/test verification. Provider behavior that requires credentials or external services remains a separate operational check.
- OpenAPI UI is intentionally absent because no OpenAPI package is installed; `docs/API.md` is the maintained contract alternative.
- The Python evaluation output/report format is not reproduced byte-for-byte. The .NET `eval` command provides retrieval hit@6 results using the same golden cases; existing .NET tests remain the detailed automated checks.

## Tests

Run `dotnet build DomainCopilot.sln --configuration Release`, then the package-free runners under `tests/DomainCopilot.*Tests`. The opt-in PostgreSQL suite requires a newly created disposable PG16+pgvector database and explicit safety variables; never point it at a user database. Browser, database, and Docker checks must be reported only when actually run.

## Demo videos

1. **Slide presentation** — [https://drive.google.com/drive/folders/1ztD-UBLIeWyhsi1Jy4G-9TIYv6M7YKfD?usp=sharing](#)
2. **Project demo** — [https://drive.google.com/drive/folders/1SAxbr6ZxXGkH3hb_B8sA4PW2r4YBUUxU?usp=sharing](#)


