# Business Requirements Document — Domain Copilot

> Implementation alignment (2026-11-15): This BRD preserves language-independent product requirements. The active implementation is the .NET 9 solution; current route and command contracts are in API.md, CLI.md, and ARCHITECTURE-DOTNET.md. The status matrix distinguishes current implementation from historical Python measurements.

## Document control

| Field | Value |
| --- | --- |
| Product | Domain Copilot — Industrial Field Maintenance |
| Variant | Domain **D5** (Industrial / field maintenance); twist **T0** (multi-tenancy) |
| Variant rule (as recorded in `README.md`) | Last two digits of National ID mod 7 = 5 → D5; sum of all digits mod 8 = 0 → T0 |
| Repository | `domain-copilot` |
| Status | MVP implemented in code; measured gaps documented in traceability matrix |
| Related docs | `docs/SYSTEM-DESIGN.md`, `docs/ARCHITECTURE.md`, `docs/EVALUATION.md`, `docs/SECURITY.md` |
| Authoritative brief | **ITI Instructor Task — Domain Copilot** (§2–§9); variant **D5 + T0** |

**Binding principles (brief §1):**

1. **Grounded, never guessing** — every claim traces to a chunk; “not enough information” is a valid answer.
2. **The human holds the pen** — supervisor approval before consequential maintenance records proceed.
3. **Everything is observable** — runs inspectable: agents, tools, chunks, token cost.

**Assigned variant (brief §2):**

| Pack | Workflow | Agents | Approval gate | Domain risk |
| --- | --- | --- | --- | --- |
| **D5 Industrial** | Symptom → equipment & manual revision → diagnostics → safety prerequisites → work order | Symptom Matcher · Diagnostic & Safety Planner · Work Order Generator (+ orchestrator) | Supervisor approves dispatch | **Skipping safety prerequisites** — must be structurally enforced, not model-discretionary |

| Twist **T0** | Must demonstrably do |
| --- | --- |
| Multi-tenancy | ≥2 tenants, isolated corpora/users/runs; **data-layer** isolation; test proving cross-tenant leakage impossible |

---

## 1. Context and problem statement

Industrial maintenance teams rely on equipment manuals, troubleshooting guides, and lockout/tagout procedures. Technicians need fast, **document-grounded** answers in the field. Supervisors must ensure that **safety prerequisites** are acknowledged before any work-order draft is produced, and that **human approval** gates any maintenance action record.

The platform serves **multiple manufacturing tenants** on one deployment. Each tenant’s manuals, users, runs, and audit data must remain **strictly isolated** at the data layer so that application bugs cannot leak cross-tenant data.

The MVP combines:

- **Retrieval-augmented Q&A** over ingested manuals (hybrid dense + keyword search, citations, refusal when evidence is weak).
- An **agentic maintenance workflow**: match symptoms → load safety steps → technician acknowledgement → draft work order → supervisor review.
- A **single-page web UI**, REST API, CLI helpers, and a **golden evaluation set** with a recorded retrieval baseline.

Corpus: 30 maintenance documents (15 per demo tenant `tenant-alpha`, `tenant-beta`), Markdown and PDF, industrial equipment domain (hydraulic presses, cranes, chillers, etc.).

---

## 2. Personas

| ID | Persona | Role in system | Goals | Pain without product |
| --- | --- | --- | --- | --- |
| P-1 | **Field technician** | `technician` | Get cited answers from manuals; start a fault workflow; acknowledge safety steps; view own runs | Searching PDFs on a tablet; missing LOTO steps; no audit trail |
| P-2 | **Maintenance supervisor** | `supervisor` | Review draft work orders; approve/reject/edit; see tenant-wide runs and usage; trigger corpus re-ingest | Unsigned drafts reaching the floor; no visibility into token spend |
| P-3 | **Platform operator / dev** | Deployer | Run Docker Compose, migrations, ingest, configure LLM providers | Manual DB setup; unclear isolation guarantees |
| P-4 | **Evaluator / instructor** | External reviewer | Run golden-set harness; inspect baseline and adversarial cases | Subjective “it works” claims without metrics |

Demo users (seeded): `alpha-tech`, `alpha-sup`, `beta-tech`, `beta-sup` (`IMPLEMENTATION-AND-HANDOFF.md`).

---

## 3. Business objectives and measurable success criteria

| Objective | Measurable criterion | Current evidence (2026-10-08 unless noted) |
| --- | --- | --- |
| O-1 Tenant-safe operations | Integration tests prove RLS; API run ownership checks | tests/DomainCopilot.InfrastructureTests/PostgresOptInIntegrationTests.cs, tests/DomainCopilot.ApiTests/Program.cs, ADR 0002 |
| O-2 Grounded answers with citations | Answers cite retrieved chunk IDs; post-generation citation validation | src/DomainCopilot.Application/Answering/AnswerService.cs and Application tests |
| O-3 Refuse when evidence insufficient | Pre-generation gate; expected-refusal cases in golden set | Historical Python baseline: 23/25 accuracy and 0/2 refusal recall; .NET answer-level refusal measurement is unavailable |
| O-4 Retrieval quality | Hit-rate@6 on golden set (in-corpus cases) | Historical Python baseline: 20/23 (87.0%); no current .NET benchmark is recorded |
| O-5 Safety before draft WO | Code-enforced gate; prerequisites from DB, not model | WorkflowRules.cs and WorkflowOrchestrator.cs, Application tests |
| O-6 Supervisor gate on drafts | Only supervisors approve; no external CMMS execution | ApiEndpoints.cs and docs/AGENTIC-WORKFLOW.md |
| O-7 Repeatable evaluation | Golden set and retrieval-only .NET harness | eval/golden.jsonl; .NET eval command in docs/EVALUATION.md; answer metrics are not available |
| O-8 Engineering quality | CI build, .NET tests, package audit, secret scan | .github/workflows/ci.yml; no current Python test count is used as .NET evidence |
| O-9 Instructor FR coverage | FR-1–FR-9 mapped in §5.1 and §10 with honest partial/deferred rows | This BRD + SDD gap table |
| O-10 Prompt-injection resistance (brief §5) | ≥3 injection eval cases **demonstrably resisted** | 6 adversarial cases exist; **not yet demonstrated** at answer level; pre-gen refusal recall **0/2** |

---

## 3.1 Instructor core functional requirements (FR-1 … FR-9)

The table below is the brief’s §3 checklist. Detailed business IDs remain **BR-xx** in §5; traceability in §10 links both.

| FR | Brief summary | Primary BR IDs | MVP status (summary) |
| --- | --- | --- | --- |
| **FR-1** | Ingestion: ≥2 formats, staged pipeline, metadata, idempotent, per-doc status | BR-03, BR-04, BR-05 | **Implemented** (MD + PDF) |
| **FR-2** | Hybrid retrieval, documented chunking, one enhancement, citations, low-evidence refusal | BR-05–BR-08 | **Partial** — enhancement = revision/metadata-aware retrieval + duplicate collapse; refusal gate weak on 2 adversarial cases |
| **FR-3** | Golden >=25 Q/A, >=5 adversarial, harness, baseline with bad numbers | BR-21 | **Partial** — .NET retrieval-only command exists; answer/refusal metrics and a .NET baseline are not recorded |
| **FR-4** | ≥3 agents + orchestrator, restricted tools, typed I/O, ≥4 tools (≥1 write gated) | BR-10–BR-13, BR-25 | **Implemented** (5 registered tools; write path gated by supervisor approval) |
| **FR-5** | Named orchestration pattern + breakers, timeouts, retry, degrade to plain RAG, audited approval | BR-12–BR-15, BR-26 | **Partial** — state machine + budgets; degrade/fallback paths exist but not all failure modes measured |
| **FR-6** | Token streaming, live agent progress, cancellation stops server work | BR-09, BR-15, BR-14 | **Partial** — SSE + stream; cancel is cooperative (in-flight LLM HTTP may continue) |
| **FR-7** | OpenAPI API + minimal UI: ingest, ask/citations, workflow, approval, trace; session history | BR-03, BR-09, BR-15, BR-16, BR-27 | **Partial** — UI/routes are implemented and documented; Swagger/OpenAPI is not exposed |
| **FR-8** | Auth + ≥2 roles, server-side enforcement | BR-16, BR-02 | **Implemented** |
| **FR-9** | Correlation ID; **token and cost** accounting persisted/queryable; tracing; health/ready | BR-22, BR-20, BR-28 | **Partial** — tokens yes; **no monetary cost field**; trace = `run_steps` + audit, not full LLM span export |

---

## 4. Scope

### In scope (MVP)

- Multi-tenant PostgreSQL with Row-Level Security (RLS).
- Ingestion of tenant corpus (Markdown/PDF), chunking, embeddings, safety-step extraction.
- Hybrid retrieval (pgvector HNSW + full-text, RRF fusion).
- `/ask` grounded Q&A (sync + stream), ask history, token logging.
- Maintenance workflow API with SSE progress, safety acknowledgement, draft generation, supervisor approval.
- Vanilla HTML/CSS/JS UI (no npm build).
- Auth: scrypt passwords, HMAC bearer tokens, technician vs supervisor RBAC.
- Provider abstraction: Groq, Gemini, Ollama, fake; configurable `LLM_CHAIN` fallback.
- Evaluation harness and retrieval-only baseline.

### Out of scope (explicit)

| Item | Rationale |
| --- | --- |
| External CMMS / ERP integration | Work orders remain drafts in PostgreSQL; no outbound maintenance execution |
| Tenant self-service admin (create tenant, billing) | Tenants seeded in migrations only |
| Mobile-native apps | Web UI only |
| Real-time collaborative editing of manuals | Corpus is read-only mount + re-ingest |
| Guaranteed prompt-injection immunity | Brief requires demonstrable resistance on ≥3 cases; not yet proven end-to-end |
| Full dollar cost accounting (FR-9) | Token counts persisted; USD/EUR cost table not implemented — see BR-28 |
| Cloud deployment / free-tier hosting | Optional deliverable (brief §7.9); MVP is Docker Compose |
| Horizontal autoscaling, managed vector DB, API gateway product | Documented as target architecture gaps — `docs/SYSTEM-DESIGN.md` |
| Automatic corpus ingest on `docker compose up` | By design: startup must not depend on embedding provider (`IMPLEMENTATION-AND-HANDOFF.md`) |

---

## 5. Business requirements

Each requirement has a unique ID, acceptance criteria, and traceability in §10.

### Multi-tenancy and data isolation

**BR-01 — Tenant isolation at the database**

- **Statement:** All tenant-owned rows must be visible only when `app.tenant_id` is set for the transaction; default is deny.
- **Acceptance criteria:**
  - Every table with `tenant_id` has RLS enabled and a `tenant_isolation` policy.
  - API connects as non-superuser `copilot_app`; migrations use admin user separately.
  - Automated test fails if any `tenant_id` table lacks RLS.
- **Priority:** Must

**BR-02 — API-level run and ask scoping**

- **Statement:** Technicians see only their runs; supervisors see all runs in their tenant; cross-tenant IDs return 404.
- **Acceptance criteria:** Handlers call ownership helpers; integration tests cover workflow tenant isolation.
- **Priority:** Must

### Knowledge base and ingestion

**BR-03 — Idempotent document ingest**

- **Statement:** Operators can ingest corpus per tenant; unchanged files are skipped; ingest status is queryable.
- **Acceptance criteria:** CLI `.\scripts\run-cli.ps1 ingest`; API `POST /ingest/corpus` (supervisor); `GET /documents`.
- **Priority:** Must

**BR-04 — Single embedding model per index**

- **Statement:** The index records one embedding model/dimension; mismatch blocks ingest/search.
- **Acceptance criteria:** `embedding_index_meta` table; guard exits with code 2 on mismatch (README).
- **Priority:** Must

**BR-05 — Section-safe chunking**

- **Statement:** Chunks align to manual sections; safety prerequisite sections remain atomic.
- **Acceptance criteria:** ADR 0003 rules; measured ~477 chunks on corpus.
- **Priority:** Must

### Grounded Q&A (maps to handoff **FR-2**)

**BR-06 — Hybrid retrieval**

- **Statement:** Questions retrieve top evidence via dense similarity and keyword search, fused for ranking.
- **Acceptance criteria:** HybridRetrieval.cs and PostgresChunkSearch.cs implement retrieval. No .NET hit-rate measurement is currently recorded; the 87% figure is historical Python evidence.
- **Priority:** Must

**BR-07 — Citations tied to evidence**

- **Statement:** Answers include `[chunk:ID]` citations; IDs must exist in retrieved set.
- **Acceptance criteria:** Citation regex validation in `src/DomainCopilot.Application/Answering/AnswerService.cs`; refusal if invalid or canary/injection marker in output.
- **Priority:** Must

**BR-08 — Low-evidence refusal before LLM**

- **Statement:** If no chunks or best dense similarity below `RETRIEVAL_MIN_SIMILARITY`, refuse without calling the chat model.
- **Acceptance criteria:** `_refusal_gate` in `src/DomainCopilot.Application/Answering/AnswerService.cs`; eval metrics for refusal gate.
- **Priority:** Must

**BR-09 — Streaming answers**

- **Statement:** Clients can receive streamed tokens and a final grounded answer event.
- **Acceptance criteria:** `POST /ask/stream`; UI consumes stream (`src/DomainCopilot.Api/static/app.js`).
- **Priority:** Should

### Agentic maintenance workflow

**BR-10 — Symptom-to-equipment match from manuals**

- **Statement:** Workflow starts from symptoms; matcher retrieves evidence; fails closed if no evidence.
- **Acceptance criteria:** `SymptomMatcher` in `src/DomainCopilot.Application/Agents/WorkflowAgents.cs`; run cannot proceed without match.
- **Priority:** Must

**BR-11 — Safety prerequisites from documents**

- **Statement:** Required safety steps come from `safety_prerequisites` table for matched documents, not from free-form model invention.
- **Acceptance criteria:** `DiagnosticSafetyPlanner`; steps returned to UI for acknowledgement.
- **Priority:** Must

**BR-12 — Acknowledgement gate**

- **Statement:** Draft generation blocked until every required safety step ID is acknowledged.
- **Acceptance criteria:** `check_safety_gate`; orchestrator refuses generation on failure.
- **Priority:** Must

**BR-13 — Draft work order with supervisor approval**

- **Statement:** Generated JSON draft enters `pending_approval`; supervisor may approve, reject, or edit (versioned).
- **Acceptance criteria:** `POST /runs/{id}/approval`; audit log entries; state machine in `src/DomainCopilot.Domain/Workflow/WorkflowContracts.cs`.
- **Priority:** Must

**BR-14 — Workflow budgets and cancellation**

- **Statement:** Runs respect token budget, model-call count, and timeouts; client can cancel cooperatively.
- **Acceptance criteria:** Env vars `RUN_*`; orchestrator checks at phase boundaries.
- **Priority:** Should

**BR-15 — Progress streaming for runs**

- **Statement:** Long-running phases emit SSE events including tokens during generation.
- **Acceptance criteria:** `POST /runs`, `POST /runs/{id}/acknowledge` return `text/event-stream`.
- **Priority:** Must

### Security and compliance

**BR-16 — Authentication and RBAC**

- **Statement:** Login returns expiring bearer token; `/me` reflects role; protected routes require auth.
- **Acceptance criteria:** `src/DomainCopilot.Application/Security/Authentication.cs`, scrypt hashes, `TOKEN_TTL_SECONDS`.
- **Priority:** Must

**BR-17 — Abuse limits on authentication**

- **Statement:** Brute-force login attempts are throttled per client IP.
- **Acceptance criteria:** 5 attempts / 60s on `POST /auth/login` in `src/DomainCopilot.Api/Http/ApiSecurityMiddleware.cs`.
- **Priority:** Should

**BR-18 — Provider-boundary redaction**

- **Statement:** Narrow PII patterns redacted before text is sent to external LLM/embed APIs.
- **Acceptance criteria:** `src/DomainCopilot.Application/Redaction/PiiRedactor.cs`; tests in `tests/DomainCopilot.ApplicationTests/Program.cs`.
- **Priority:** Should

**BR-19 — Secure web defaults**

- **Statement:** CSP, frame denial, body size limit, CORS from configuration.
- **Acceptance criteria:** Middleware in `src/DomainCopilot.Api/Http/ApiSecurityMiddleware.cs`; API tests inspect headers.
- **Priority:** Must

### Observability and evaluation (maps to handoff **FR-3**)

**BR-20 — Ask and run usage visibility**

- **Statement:** Token usage per ask and aggregated usage endpoint for users/supervisors.
- **Acceptance criteria:** `ask_log` table; `GET /usage`; workflow `total_tokens` on runs.
- **Priority:** Should

**BR-21 — Golden evaluation set and harness**

- **Statement:** ≥25 fixed Q&A cases, ≥5 adversarial, runnable metrics and stored baseline.
- **Acceptance criteria:** eval/golden.jsonl contains the reference cases; .NET CLI eval reports retrieval hit@6 only. eval/baseline.json is historical Python output and is not a .NET baseline.
- **Priority:** Must

**BR-22 — Correlation IDs**

- **Statement:** Requests carry `X-Correlation-ID` for support and workflow tracing.
- **Acceptance criteria:** Middleware sets/forwards header; stored on ask_log and run steps where applicable.
- **Priority:** Should

### Provider and deployment

**BR-23 — LLM provider fallback chain**

- **Statement:** Chat providers tried in `LLM_CHAIN` order on transient/quota failures; no fallback after first stream token.
- **Acceptance criteria:** `src/DomainCopilot.Application/Llm/FallbackChain.cs`, ADR 0004; README fallback table.
- **Priority:** Must

**BR-24 — Local and offline demo path**

- **Statement:** System runs with Ollama and/or `fake` provider for tests and demos without hosted keys.
- **Acceptance criteria:** Docker Compose defaults; `LLM_CHAIN=fake` documented; ≥2 provider implementations (brief §4).
- **Priority:** Must (brief: provider abstraction mandatory)

**BR-25 — Tool registry and side-effect gate (FR-4)**

- **Statement:** ≥4 tools with schemas; ≥1 write tool never executes without passing approval gate.
- **Acceptance criteria:** `src/DomainCopilot.Application/Tools/ToolRegistry.cs` (`search_manuals`, `get_document_revisions`, `get_safety_prerequisites`, `create_work_order_draft`, `acknowledge_safety_steps`); workflow safety-gate validation; contract tests in `tests/DomainCopilot.ApplicationTests/Program.cs`.
- **Priority:** Must

**BR-26 — Orchestration controls and degradation (FR-5)**

- **Statement:** Resumable state machine with max model calls, step/run timeouts, retries, graceful degradation to plain RAG when workflow generation fails.
- **Acceptance criteria:** WorkflowOrchestrator.cs implements the resumable workflow and enforces configured RUN_* budgets; see docs/AGENTIC-WORKFLOW.md. End-to-end degradation scenarios remain incompletely measured.
- **Priority:** Must

**BR-27 — Minimal UI surface (FR-7)**

- **Statement:** UI covers login, ask with citations, workflow run, safety acknowledgement, supervisor approval, trace, documents, usage.
- **Acceptance criteria:** The .NET static UI is under src/DomainCopilot.Api/static; route contracts are documented in docs/API.md. Swagger/OpenAPI at /docs is not exposed, so that part of FR-7 remains unmet.
- **Priority:** Must

**BR-28 — Token and cost observability (FR-9)**

- **Statement:** Per-request token usage persisted and queryable; correlation ID end-to-end; health and readiness endpoints.
- **Acceptance criteria:** `ask_log`, `run_steps`, `GET /usage`, `GET /health`, `GET /ready`, `X-Correlation-ID`.
- **Gap vs brief:** **Cost** (currency) not computed — tokens only (`GET /usage endpoint` aggregates prompt/completion counts).
- **Priority:** Must (tokens); cost line item **partial**

**BR-29 — Prompt-injection evaluation (brief §5)**

- **Statement:** ≥3 injection cases in golden set; system **demonstrably resists** them (especially indirect injection in corpus).
- **Acceptance criteria:** Three injection cases remain in eval/golden.jsonl. The .NET evaluator is retrieval-only; answer-level resistance remains unverified.
- **Priority:** Must — **partial** until remeasured

**BR-30 — Staged ingest pipeline (FR-1)**

- **Statement:** Separable extract → clean → chunk → embed → index with document/chunk metadata (source, section, page, revision).
- **Acceptance criteria:** `src/DomainCopilot.Infrastructure/Extraction/FileSystemDocumentExtractor.cs`, `src/DomainCopilot.Application/Text/CleanText.cs`, `src/DomainCopilot.Application/Text/DocumentChunker.cs`, `src/DomainCopilot.Application/Ingestion/DocumentIngestion.cs`, `.NET CLI ingest command`; chunk columns in `002_chunks_and_safety.sql`.
- **Priority:** Must

---

## 6. Business rules

| ID | Rule |
| --- | --- |
| BRU-1 | A user belongs to exactly one tenant; tenant is embedded in the auth token and reinforced by DB session variable. |
| BRU-2 | Only supervisors may approve or reject work orders and trigger tenant corpus re-ingest via API. |
| BRU-3 | Technicians may only approve safety steps for their own run via acknowledgement API, not final work-order approval. |
| BRU-4 | Retrieved evidence is treated as untrusted data in the answer prompt; the model must not obey embedded “UNTRUSTED EMBEDDED INSTRUCTION” markers (corpus includes adversarial chunks). |
| BRU-5 | Work-order parts suggested by the model are kept only when supported by retrieved evidence (`WorkOrderGenerator` logic). |
| BRU-6 | Embedding provider and model cannot change without full re-index. |
| BRU-7 | Demo password is shared via `DEMO_USER_PASSWORD` env; production must rotate secrets and restrict CORS origins. |

---

## 7. Assumptions

| ID | Assumption |
| --- | --- |
| A-1 | Two demo tenants suffice to prove T0 multi-tenancy; no dynamic tenant provisioning is required for grading. |
| A-2 | Technicians have network access to the API (browser or CLI); offline-first mobile sync is not required. |
| A-3 | Hosted LLM providers (Groq, Gemini) are acceptable for non-production demos when keys are supplied; Ollama is acceptable for local dev with latency trade-offs. |
| A-4 | English manuals and `english` tsvector configuration match the evaluation questions. |
| A-5 | Supervisors are trusted insiders; the approval gate is organizational, not cryptographic dual-control. |
| A-6 | Corpus content is controlled teaching material, not live SCADA or customer PII—though redaction handles common PII patterns anyway. |
| A-7 | **English-only** documentation and UI satisfy the brief (T1 bilingual not assigned). |
| A-8 | **Docker Compose** is the required packaging path (brief §4); live cloud deploy is optional extra credit. |
| A-9 | **FR-9 “cost”** is interpreted as token accounting plus a path to currency (price table/env); MVP delivers tokens first. |

---

## 8. Risks

| ID | Risk | Likelihood | Impact | Mitigation (current / planned) |
| --- | --- | --- | --- | --- |
| R-1 | Wrong maintenance advice from hallucination | Medium | High | Retrieval-only answers; citation check; refusal gate (partial—see BR-08 traceability) |
| R-2 | Prompt injection via manual text | Medium | High | Canary/marker checks; golden adversarial cases; **refusal recall 0/2** — needs threshold/heuristic improvement |
| R-3 | Cross-tenant data leak | Low | Critical | RLS + tests; API ownership checks |
| R-4 | Token spend abuse (hosted APIs) | Medium | Medium | Login rate limit only; run token budgets; **no global API rate limit** — see SYSTEM-DESIGN gap |
| R-5 | Provider outage | Medium | Medium | LLM_CHAIN fallback; 502 mapping |
| R-6 | Secrets in Git history | Low | High | Gitleaks CI; operator must verify full history before publish (`docs/SECURITY.md`) |
| R-7 | Safety steps bypassed by model output | Low | High | Prerequisites from DB; gate in code; model cannot remove required list on WO |
| R-8 | Single-process rate limits ineffective under scale | Medium | Low | Documented; defer gateway limiter |

---

## 9. Dependencies

- PostgreSQL 16 + pgvector (Docker image `pgvector/pgvector:pg16`).
- .NET 9 (net9.0), ASP.NET Core minimal API, Npgsql, PostgreSQL 16, and pgvector.
- Optional: Ollama on host for default Compose LLM/embeddings.
- Optional: Groq/Gemini API keys for hosted models.

---

## 10. Requirements traceability matrix

Status key: **Implemented** = meets acceptance criteria in code/tests; **Partial** = core exists, measurable or operational gap; **Deferred** = explicitly out of MVP or not built.

| Req ID | Summary | Status | Evidence |
| --- | --- | --- | --- |
| BR-01 | DB tenant RLS | Implemented | migrations/*.sql, ADR 0002, PostgresOptInIntegrationTests.cs |
| BR-02 | API run/ask scoping | Implemented | ApiEndpoints.cs, API tests, PostgreSQL isolation tests |
| BR-03 | Ingest + document status | Implemented | .NET CLI ingest, DocumentIngestion.cs, PostgresDocumentRepository.cs |
| BR-04 | Fixed embedding index | Implemented | 003_embedding_index_meta.sql and EmbeddingGuard.cs |
| BR-05 | Section chunking | Implemented | ADR 0003, ingest pipeline |
| BR-06 | Hybrid retrieval | Implemented | HybridRetrieval.cs, PostgresChunkSearch.cs; 87% is a historical Python measurement, not a .NET measurement |
| BR-07 | Citation validation | Implemented | AnswerService.cs and Application tests |
| BR-08 | Low-evidence refusal | Partial | Gate exists; historical Python refusal recall was 0/2; .NET answer-level refusal metrics are unavailable |
| BR-09 | Stream /ask | Implemented | .NET API SSE route and static UI |
| BR-10 | Symptom matcher | Implemented | WorkflowAgents.cs and WorkflowOrchestrator.cs |
| BR-11 | Safety from DB | Implemented | `safety_prerequisites`, planner agent |
| BR-12 | Acknowledgement gate | Implemented | WorkflowRules.cs and Application tests |
| BR-13 | Supervisor approval | Implemented | ApiEndpoints.cs approval endpoint and audit_log |
| BR-14 | Run budgets/cancel | Implemented | .NET configuration RUN_*; cooperative cancellation documented |
| BR-15 | Workflow SSE | Implemented | .NET ApiEndpoints.cs SSE responses |
| BR-16 | Auth + RBAC | Implemented | Authentication.cs, ApplicationSecurity.cs, explicit CLI seed-users |
| BR-17 | Login rate limit | Partial | In-memory, login-only; not distributed |
| BR-18 | PII redaction | Partial | PiiRedactor.cs uses narrow patterns; not full DLP |
| BR-19 | Web security headers | Implemented | ApiSecurityMiddleware.cs and API tests |
| BR-20 | Usage logging | Implemented | 005_asks_and_usage.sql and .NET usage endpoint |
| BR-21 | Golden eval + baseline | Partial | .NET CLI measures retrieval hit@6 only; baseline is historical Python output and answer-level metrics are unavailable |
| BR-22 | Correlation IDs | Implemented | Middleware, ask_log, workflow |
| BR-23 | LLM fallback chain | Implemented | ADR 0004 and FallbackChain.cs |
| BR-24 | Offline/fake path | Implemented | fake provider and Infrastructure test runner |
| BR-25 | Tools + approval gate | Implemented | ToolRegistry.cs and Application tests |
| BR-26 | Orchestration + degrade | Partial | WorkflowOrchestrator.cs state machine + budgets; not all degradation paths are measured end-to-end |
| BR-27 | UI + OpenAPI | Partial | .NET static UI and docs/API.md; Swagger/OpenAPI is unavailable |
| BR-28 | Observability | Partial | Tokens + correlation + health; no currency cost |
| BR-29 | Injection resistance proven | Partial | 6 adversarial cases; current .NET evaluator is retrieval-only and answer-level resistance is unverified |
| BR-30 | Staged ingest | Implemented | .NET Application + Infrastructure ingest pipeline |

### FR → BR quick reference

| FR | BR IDs |
| --- | --- |
| FR-1 | BR-03, BR-04, BR-05, BR-30 |
| FR-2 | BR-05, BR-06, BR-07, BR-08 |
| FR-3 | BR-21, BR-29 |
| FR-4 | BR-10, BR-11, BR-25 |
| FR-5 | BR-12, BR-13, BR-14, BR-15, BR-26 |
| FR-6 | BR-09, BR-14, BR-15 |
| FR-7 | BR-03, BR-09, BR-15, BR-27 |
| FR-8 | BR-01, BR-02, BR-16 |
| FR-9 | BR-20, BR-22, BR-28 |

### Gaps to close before submission (from brief non-negotiables)

| Brief expectation | Current gap | Where documented |
| --- | --- | --- |
| ≥3 injection cases **demonstrably resisted** | Pre-gen gate missed 2 expected refusals; answer-level run not completed | `docs/EVALUATION.md`, BR-29 |
| FR-9 cost accounting | Tokens only | BR-28, SDD gap table |
| Committed dependency lockfile (brief section 5 supply chain) | No packages.lock.json; NuGet versions are declared in project files | docs/SECURITY.md |
| Process deliverables (§6–§7): ≥30 commits, ≥8 PRs, teaching pack, videos, `AI-USAGE-LOG` | Out of scope of this BRD file — track in repo hygiene | Instructor checklist §6–§7 |

---

*End of BRD*

