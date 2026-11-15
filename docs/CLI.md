# .NET CLI

Run from the workspace root with `./scripts/run-cli.ps1 <command> ...` in PowerShell or `dotnet run --project src/DomainCopilot.Cli -- <command> ...`. The executable loads the .NET workspace `.env`; OS process variables take precedence. No command launches Python.

- `help`: list commands.
- `migrate`: provision the restricted `copilot_app` login role and apply ordered SQL migrations using the admin connection. Explicit only; never called by API startup.
- `seed-users --tenant ID --username NAME [--role technician|supervisor] [--confirm-supervisor]`: add one user idempotently, using `DEMO_USER_PASSWORD` or an interactive no-echo prompt. Existing users are left unchanged. Supervisor role requires an explicit confirmation flag and administrative database access. Passwords are never accepted as command-line values.
- `login --tenant ID --username NAME`: test login against `API_BASE_URL` (default `http://localhost:8000`); the token is neither printed nor saved.
- `ask --tenant ID --username NAME --question TEXT`: logs in, sends an authenticated request to the .NET `/ask` endpoint, and prints the response. It prompts without echo for a password.
- `ingest [corpus-directory] [--dry-run] [--max-chars N]`: extracts `.md` and `.pdf` documents under shared `tenant-*` folders. `--dry-run` reports metadata, chunk counts, and safety-step counts without a database or provider; normal mode uses the existing embedder and repository to write derived records only, never Corpus files.
- `llm-smoke [--provider fake|groq|gemini|ollama] [--confirm-live]`: fake is the default. Non-fake providers require explicit `--confirm-live` and make a real network request when invoked. This command is not used by CI verification.
- `eval`: retrieval-only hit@6 evaluation using `eval/golden.jsonl`, the configured embedding provider, and the existing .NET hybrid retrieval/index. It can contact the configured embedding service and requires the migrated index; it does not call a chat completion provider or Python.

The former Python `copilot run`, `acknowledge`, `approve`, and `status` subcommands were empty stubs. Their actual functionality is available through the API and frontend workflow routes; they are intentionally not represented by misleading CLI no-ops. Python's separate calibration and full answer-scoring report format are not ported. The native `eval` command supplies retrieval hit@6 only; use .NET tests for broader behavior and do not claim the Python report is reproduced.

For a host-run process, the PowerShell wrappers adjust only `.env`-derived Compose hostnames (`db` and `host.docker.internal`) to loopback/local host. They do not change OS-supplied variables or print connection values. Docker Compose retains service-host routing.

