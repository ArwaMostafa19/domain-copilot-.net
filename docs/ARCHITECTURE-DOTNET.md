# Current .NET architecture

This file describes the executable .NET 9 implementation. The older `ARCHITECTURE.md` and `SYSTEM-DESIGN.md` record the original Python reference/design and are historical migration context, not current deployment instructions.

| Project | Responsibility | References |
| --- | --- | --- |
| `DomainCopilot.Domain` | Domain contracts, records, value types, and workflow rules. | None |
| `DomainCopilot.Application` | Use cases, orchestration, agents, retrieval, security contracts, and ports. | Domain only |
| `DomainCopilot.Infrastructure` | PostgreSQL/pgvector repositories, migrations, providers, extractors, and user provisioning adapter. | Application and Domain |
| `DomainCopilot.Api` | HTTP endpoints, authentication middleware, dependency composition, static frontend hosting, and safe HTTP errors. | Application and Infrastructure |
| `DomainCopilot.Cli` | Explicit administrative and operational commands. It reuses Application/Infrastructure and does not launch Python. | Application and Infrastructure |

The API does not apply migrations or seed users at startup. `migrate` and `seed-users` are explicit CLI operations that use `ADMIN_DATABASE_URL`; normal repository traffic uses `DATABASE_URL` and the restricted `copilot_app` role. Tenant filters and PostgreSQL RLS are applied in Infrastructure transactions.

The API project packages the independent read-only `./corpus`, answer prompt, static assets, and SQL migration files for publish/Docker. The CLI publishes the same internal Corpus and golden evaluation fixture. The separate Python project and its Corpus path are not runtime dependencies.

Provider calls are behind Application ports and Infrastructure implementations for Gemini, Groq, Ollama, and fake testing. `LLM_CHAIN` controls chat fallback. The static frontend calls only the .NET API and parses the API's SSE stream.

See [API route contracts](API.md), [CLI operations](CLI.md), and [security notes](SECURITY.md). No Python runtime, Python API, or Python service is part of the .NET runtime path.
