# .NET API contract

The API uses ASP.NET minimal endpoints. There is no Swagger package/UI; this file is the maintained route reference.

| Method | Path | Auth | Purpose / contract |
| --- | --- | --- | --- |
| GET | `/` | No | Serves the frontend `index.html`; JSON status fallback if assets are absent. |
| GET | `/health` | No | Liveness `{ "status": "ok" }`; does not access the database. |
| GET | `/ready` | No | Checks app-role database connectivity and configured model-chain readiness; never migrates or seeds. |
| POST | `/auth/login` | No | JSON `{ "tenant_id", "username", "password" }`; returns `access_token`, `token_type`, tenant/user/role/username; invalid credentials return 401. |
| GET | `/me` | Bearer | Current active user profile; user and role are reloaded from the tenant-scoped database. |
| POST | `/ask` | Bearer | JSON `{ "question" }`; grounded answer/refusal, citations, evidence and token usage. |
| POST | `/ask/stream` | Bearer | JSON `{ "question" }`; SSE `progress`, `token`, and `done` events in generation order. |
| GET | `/asks` | Bearer | Current user's tenant-scoped Ask history. |
| POST | `/runs` | Bearer | Creates and orchestrates a maintenance workflow run. |
| GET | `/runs` | Bearer | Lists current user's runs; supervisors receive tenant-scoped run history according to application rules. |
| GET | `/runs/{run_id}` | Bearer | Tenant/owner or supervisor-authorized run details and steps. |
| POST | `/runs/{run_id}/acknowledge` | Bearer | Technician acknowledges safety prerequisites. |
| POST | `/runs/{run_id}/approval` | Bearer | Supervisor approves or rejects the pending workflow. |
| POST | `/runs/{run_id}/cancel` | Bearer | Cancels an owned run (or supervisor-authorized run). |
| GET | `/documents` | Bearer | Lists documents visible to authenticated tenant. |
| GET | `/documents/{doc_id}` | Bearer | Gets a tenant-scoped document by numeric ID or document key. |
| POST | `/ingest/corpus` | Bearer | Explicitly ingests the shared Corpus for the authenticated tenant; no startup seeding. |
| GET | `/usage` | Bearer | Tenant/user-scoped Ask and workflow usage. |

Requests use JSON with camelCase response serialization and snake_case request fields where the API contracts specify them. Error responses are JSON; provider and database failures use safe generic details with correlation IDs. Authentication tokens preserve the Python format: two base64url segments separated by a dot, HMAC-SHA256 over the encoded JSON payload. They are not standards-based JWTs.

For exact per-route request DTOs, status handling, and SSE fields, inspect `src/DomainCopilot.Api/Http/ApiContracts.cs` and `ApiEndpoints.cs`; those source contracts are authoritative. CORS origins are configured using `CORS_ORIGINS`.
