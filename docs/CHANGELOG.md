# Changelog

Notable changes to this project, one section per `vX.Y` version — matching the version sections in [CLAUDE.md](./CLAUDE.md), which has the "why" behind each entry. This isn't a semver release process (no published packages/tags yet), just a running history.

## v1.6 — 2026-07-17

### Fixed
- `get_model_stats` now groups by the model the backend actually answered with, instead of the request's `model` field (often empty, silently merging unrelated models into one bucket).
- `start.ps1`'s dev-mode fallback (no published binary) no longer kills the Cloudflare tunnel right after startup.

## v1.5 — 2026-07-13

### Fixed
- `chat` now returns a backend failure as an `Error` field instead of throwing — previously the MCP SDK masked the real error behind a generic "An error occurred invoking 'chat'" message.

## v1.4 — 2026-07-13

### Added
- `PromptTokens`/`CompletionTokens` surfaced on `chat` and job results, from the backend's own `usage` object.
- `cancel_job` can now abort an in-flight `Running` item, not just `Pending` ones.
- `get_model_stats` — empirical per-model profile (outcomes, average duration, average completion tokens) built from job history.
- Consumer usage guide repackaged as an installable Claude skill (`skills/llama-mcp-consumer/`).

### Changed
- Backend error responses now surface the actual error body (truncated), not just an HTTP status code.

### Security
- Constant-time comparison for the bearer token check.
- `signing.key` written with owner-only file permissions.
- Cap on OAuth dynamic client registrations (50).

## v1.3 — 2026-07-13

### Added
- `IsEmpty` flag on `chat` results and a new `CompletedEmpty` job-item status — a backend can report `finishReason: "stop"` (success) with genuinely empty content, which was previously indistinguishable from a real answer.
- Job timing exposed: `RunningForSeconds` on `get_job_status`, `StartedAt`/`CompletedAt`/`DurationSeconds` on `get_job_result`.
- Consumer usage guide (`docs/consumer-guide.md`).
- Unit test project (`tests/LlamaMcp.Tests`).

## v1.2 — 2026-07-10

### Added
- Configurable backend timeout (`Backend:TimeoutSeconds`, default 600s) — previously silently capped at `HttpClient`'s 100s default.
- `enableThinking` parameter on `chat`/`submit_job` to control reasoning-model "thinking" (maps to `chat_template_kwargs.enable_thinking`).
- Async batch job system — `submit_job` / `get_job_status` / `get_job_result` / `cancel_job` — backed by a persisted SQLite queue and a sequential background worker, for delegating large batches (many documents/images) without blocking the caller or losing progress on a dropped connection.
- Progress heartbeat on `chat` (every 15s while a call is in flight).

### Security
- Pinned `SQLitePCLRaw.lib.e_sqlite3` to 3.53.3 (CVE-2025-6965).

## v1.1 — 2026-07-09

### Added
- `health` tool — checks backend reachability without spending a generation call.
- Multimodal input on `chat`: messages can attach `imageUrls` for vision-capable models.

## v1 — 2026-07-07

### Added
- `chat` and `list_models` MCP tools, proxying an OpenAI-compatible backend (llama.cpp / LM Studio).
- Cloudflare quick tunnel + launcher scripts (`start.sh`/`start.ps1`/`start.bat`) for remote access without opening a public port.
- OAuth 2.1 authorization server (OpenIddict, dynamic client registration, PKCE) alongside the original static bearer token, for web MCP clients (claude.ai, ChatGPT) that require it.
- Self-contained single-file publish per OS.
- Badges, downloadable release instructions, and a CONTRIBUTING section (2026-07-08).
