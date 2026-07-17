# llama-mcp

![License](badges/license.svg) ![.NET](badges/dotnet.svg) ![MCP](badges/mcp.svg)

MCP server for talking to a local **llama.cpp** (`llama-server`) or **LM Studio** instance running on a workstation, reachable remotely and securely **without opening any public port**.

> **Status**: v1 complete and verified end-to-end (server, launcher scripts, self-contained publish, quick tunnel, OAuth) against a real LM Studio instance. v1.1 (`health` tool, multimodal image input) and v1.2 (configurable timeout, `enableThinking` control, async batch jobs, progress heartbeat) verified the same way, including a live multi-model session over claude.ai web. v1.3 (empty-content detection, job timing), v1.4 (token usage, richer backend error messages, `cancel_job` aborts an in-flight item, `get_model_stats`, security hygiene, consumer skill), and v1.5 (`chat` returns backend failures as data instead of throwing) are all implemented, unit-tested, and verified live against a real LM Studio backend over the full remote tunnel path. v1.6 fixed two bugs that QA round surfaced: `get_model_stats` now attributes items to the model that actually answered instead of the (often blank) requested one, and `start.ps1`'s dev-mode fallback no longer kills the tunnel right after startup. See [`CLAUDE.md`](./CLAUDE.md) for all the architectural decisions, and [`skills/llama-mcp-consumer/SKILL.md`](./skills/llama-mcp-consumer/SKILL.md) for how to use these tools effectively.

## What this is for

If you already pay for a Claude subscription, wiring in one or more local agents only makes sense if it actually saves tokens: offload basic, repetitive, or otherwise delegable work (data extraction, classification, simple drafts, document/image analysis...) to smaller/cheaper models running on your own hardware, instead of spending Sonnet/Opus tokens on it. If a task needs real reasoning, you're better off just staying on Sonnet — this is the filter used to judge every new feature here.

## Architecture

```
MCP client (remote)  --HTTP/SSE via tunnel-->  Cloudflare Tunnel  -->  MCP server (on the workstation)  --localhost-->  llama.cpp / LM Studio
```

- The MCP server runs **on the same workstation** as llama.cpp/LM Studio and reaches it over `localhost`.
- Remote access goes through a **Cloudflare quick tunnel**: no public port, no dynamic DNS to configure, no Cloudflare account required.
- Authentication via **API key / bearer token** (Claude Code, scripts/CLI) or via **OAuth 2.1** with PKCE (claude.ai, ChatGPT — web clients require OAuth, a static header isn't enough). Both are handled directly by the MCP server, no delegation to the tunnel.
- Target: the **OpenAI-compatible** API exposed by both `llama-server` and LM Studio.

## MCP tools exposed

- **`chat`** — proxies `/v1/chat/completions`. Accepts an OpenAI-style `messages` array (system/user/assistant), optional generation parameters (`temperature`, `max_tokens`, `top_p`, `enableThinking`, ...) passed through to the backend, and an optional `model` (server-configured default otherwise). No streaming: a full response in a single call, with a progress heartbeat every 15s while it's in flight so a long call doesn't look dead. Any message can attach `imageUrls` (http(s) URLs or `data:` base64 URIs) for vision-capable models (e.g. Qwen-VL). The result includes `IsEmpty`: `true` when the backend returned an empty completion even on a "successful" `finishReason` — treat this as a failed call, see the consumer skill. Also includes `PromptTokens`/`CompletionTokens` when the backend reports usage, so a caller can size `maxTokens` on the next call from real observed completion length instead of guessing. **v1.5**: on a backend failure, `chat` no longer throws — it returns a result with `Error` set to the backend's error message (non-null only on failure), so the detailed error body reaches the caller instead of being masked behind a generic MCP invocation error.
- **`list_models`** — proxies `/v1/models`, lists the models available on the backend.
- **`health`** — checks whether the backend is reachable (without spending a generation call) and reports the configured base URL and available models, or the error if it's down.
- **`submit_job` / `get_job_status` / `get_job_result` / `cancel_job`** — async batch processing for work too large or slow for a single `chat` call (many documents/images, long translations). `submit_job` takes a list of items (each shaped like `chat`'s `messages`, with an optional per-item `label`) plus generation parameters shared across the batch, and returns immediately with a `jobId` — the server processes items sequentially in the background. Poll `get_job_status` for progress and `get_job_result` for output (supports pagination, fetching specific items by index, and filtering by status) whenever convenient; `jobId` isn't tied to any session, so a dropped connection or a fresh reconnect doesn't lose the job.
  - Note: the local backend still processes one item at a time (no parallelism) — async means the caller isn't blocked waiting, not that work finishes faster.
  - Item results can land in `CompletedEmpty` instead of `Completed` — same empty-content case as `chat`'s `IsEmpty`, but as a distinct terminal status so you can filter for it via `statusFilter`. Completed items also carry `PromptTokens`/`CompletionTokens` when the backend reports them.
  - `get_job_status` reports `RunningForSeconds` (elapsed time on the currently-running item, null if nothing is running) and `get_job_result` reports each item's `StartedAt`/`CompletedAt`/`DurationSeconds` — the server never auto-cancels a slow item, so use these to set your own patience threshold and call `cancel_job` if you decide to give up on it.
  - `cancel_job` now aborts a `Running` item's in-flight backend call, not just `Pending` ones — no more restarting the server to get unstuck from one glacially slow item. The result's `RunningItemCancellationRequested` tells you whether a running item was signalled; it lands in `Cancelled` once the abort is observed.
  - A backend error (e.g. a non-vision model rejecting an image) now surfaces the actual response body (truncated) in the item's `Error`/the `chat` exception, instead of an opaque status code.
- **`get_model_stats`** — an empirical per-model profile built from this server's own job history: item counts by outcome (`completed`/`completedEmpty`/`failed`/`cancelled`), average duration, and average completion tokens. Use it to pick the best-fit local model for a task from what's actually been observed on this hardware, not the model's advertised specs.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/) (only to build/publish, not to run the already-published binary).
- [`cloudflared`](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/) on your `PATH`.
- `llama-server` or LM Studio running on the workstation, with a model loaded and the API server active (default port `1234` for LM Studio).

## Quick start (server + tunnel)

```bash
# Linux/macOS
./start.sh

# Windows: double-click start.bat, or
powershell -File start.ps1
```

The script:
1. generates a random bearer token (or uses the one in `LLAMA_MCP_TOKEN` if set);
2. starts the MCP server (uses the published binary in `publish/<rid>/` if it exists, otherwise falls back to `dotnet run` in dev mode);
3. starts `cloudflared tunnel --url http://localhost:5181`;
4. prints the public URL (`https://xxxx.trycloudflare.com`) and the token to use in your MCP client.

The URL is ephemeral: it changes every time the script restarts.

## Getting the self-contained binary

Download a prebuilt release instead of building from source — extracts straight into the `publish/` layout `start.sh`/`start.ps1` expect:

```bash
# Linux
curl -L "https://github.com/tonyexpo/llama-mcp/releases/latest/download/llama-mcp-linux-x64.tar.gz" | tar xz -C publish

# Windows (PowerShell)
Invoke-WebRequest -Uri "https://github.com/tonyexpo/llama-mcp/releases/latest/download/llama-mcp-win-x64.zip" -OutFile llama-mcp-win-x64.zip
Expand-Archive llama-mcp-win-x64.zip -DestinationPath publish
```

Or build it yourself, to avoid requiring the .NET runtime on the workstation:

```bash
# Linux
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish/linux-x64

# Windows
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish\win-x64
```

`start.sh`/`start.ps1` look for the binary at these exact paths.

## Connecting an MCP client

Using the URL and token printed by `start.sh`/`start.ps1`:

**Claude Code** (CLI):

```bash
claude mcp add --transport http llama-mcp https://xxxx.trycloudflare.com/ --header "Authorization: Bearer <token>"
```

**Claude Desktop** (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "llama-mcp": {
      "url": "https://xxxx.trycloudflare.com/",
      "headers": {
        "Authorization": "Bearer <token>"
      }
    }
  }
}
```

**claude.ai / ChatGPT (web)** — these require OAuth, a static header isn't accepted:

1. Settings → Connectors → "Add custom connector" → paste the URL (`https://xxxx.trycloudflare.com/`).
2. The client discovers the OAuth endpoints on its own and registers itself automatically (dynamic client registration) — if it instead asks you for a "Client ID" by hand, automatic registration didn't kick in; check that the URL is exactly the one printed by the script.
3. A consent screen from the server opens: enter the same bearer token printed by `start.sh`/`start.ps1` there (it acts as the approval password, and is never shared with the web client).
4. Done — the web client now holds its own access/refresh token, separate from the master bearer token.

OAuth state (registered clients, issued tokens, signing key) is saved in `~/.config/llama-mcp/` (`%APPDATA%\llama-mcp\` on Windows) and survives server restarts — no need to re-authorize every client on every restart.

## Installing the consumer skill

[`skills/llama-mcp-consumer/`](./skills/llama-mcp-consumer/) is a Claude skill packaging the usage lessons above (empty-content handling, `maxTokens` sizing, sample-verification, chunking, model selection via `get_model_stats`, ...) so any orchestrating Claude session picks them up automatically instead of relying on someone pasting a doc. To install it:

```bash
# copy
cp -r skills/llama-mcp-consumer ~/.claude/skills/

# or symlink, to pick up repo updates automatically
ln -s "$(pwd)/skills/llama-mcp-consumer" ~/.claude/skills/llama-mcp-consumer
```

## Local development (no tunnel)

```bash
Auth__BearerToken=<any-token-you-choose> dotnet run
```

The server listens on `http://localhost:5181/`. Every MCP request needs the `Authorization: Bearer <token>` header (401 without it). `Backend:BaseUrl` defaults to `http://localhost:1234` (LM Studio's default port) and `Backend:TimeoutSeconds` defaults to `600` (local/large-model generation is often slow) — both configurable in `appsettings.json` or via `Backend__BaseUrl` / `Backend__TimeoutSeconds` env vars.

## Stack

Priority: **.NET 10** → Python → Node.js.

## Contributing

Issues and PRs are welcome. Before opening a PR:

- Check [`CLAUDE.md`](./CLAUDE.md) for the settled architectural decisions and v1 scope — it explains the *why* behind non-obvious choices (OAuth, SQLite storage, forwarded headers, ...), don't re-litigate those without a good reason.
- Keep the "why this exists" filter in mind: a feature earns its place if it helps offload real, repeatable work from paid Claude tokens onto local models — not just because it's technically possible.
- Small, focused PRs over large ones. Run `dotnet build` before submitting.

## License

See [LICENSE](./LICENSE).
