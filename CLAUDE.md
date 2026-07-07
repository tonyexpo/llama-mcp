# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project status

v1 scaffold implemented and verified end-to-end against a real LM Studio instance (both `chat` and `list_models`). Not yet done: launcher scripts (`start.sh`/`start.bat`/`start.ps1`), self-contained publish, Cloudflare quick tunnel wiring.

## Running locally

```
dotnet build
Auth__BearerToken=<token> dotnet run
```

`Auth:BearerToken` has no default and fails startup validation if unset (`ValidateOnStart`) — set it via the `Auth__BearerToken` env var, not `appsettings.json` (that file is tracked in git, don't put a real token in it). `Backend:BaseUrl` defaults to `http://localhost:1234` (LM Studio's default). Point an MCP client at `http://localhost:5181/` (or whatever `--urls` you pass) with header `Authorization: Bearer <token>`.

## What this is

An MCP server that lets a remote MCP client (Claude Desktop, Claude Code, etc.) talk to a local LLM inference server — llama.cpp (`llama-server`) or LM Studio's OpenAI-compatible API — running on a workstation, **without exposing a public port** on that workstation.

## Why this exists (goal, use to judge scope)

For someone who already pays for a Claude subscription, wiring in local agents only makes sense if it actually saves Claude tokens: offload basic, repetitive, or delegable work (extraction, classification, simple drafts, document/image analysis, etc.) to smaller/cheaper/custom models running on your own hardware, instead of spending Sonnet/Opus tokens on it. If a task doesn't fit that bar — it needs real reasoning, or delegating it costs more setup than it saves — there's no reason not to just stay on Sonnet directly. Use this as the filter for any new tool or feature: does it let you delegate real, repeatable work away from paid tokens, or is it a nice-to-have that doesn't move that needle.

## Architecture

```
MCP client (remote)  --HTTP/SSE over tunnel-->  Cloudflare Tunnel  -->  MCP server (on workstation)  --localhost-->  llama.cpp / LM Studio (OpenAI-compatible API)
```

Key decisions (settled — do not re-litigate without asking):

- **Topology**: the MCP server runs *on the same workstation* as llama.cpp/LM Studio, talking to it over `localhost`. Remote MCP clients reach the MCP server through the tunnel, not the other way around.
- **Tunnel**: Cloudflare Tunnel (`cloudflared`) is the default ingress. No public port is opened on the workstation; no dynamic DNS setup is needed for users.
- **Transport**: MCP over HTTP/SSE (required for remote clients, as opposed to stdio which only works for local same-machine clients).
- **Auth**: the MCP server owns authentication directly via an API key / bearer token validated per request. Auth is not delegated to the tunnel layer.
- **Backend target**: the OpenAI-compatible chat/completions API, since both `llama-server` and LM Studio expose it — the MCP server should not need backend-specific code paths for the two.

## v1 scope

- **MCP SDK**: use the official `ModelContextProtocol` / `ModelContextProtocol.AspNetCore` NuGet packages for the Streamable HTTP transport — don't hand-roll JSON-RPC framing. Currently pinned to `2.0.0-preview.1` (installed with `--prerelease`, no stable release exists yet) — expect breaking API changes on upgrade, re-verify the `AddMcpServer()/WithHttpTransport()/WithTools<T>()/MapMcp()` pattern in `Program.cs` against the changelog before bumping.
- **Tools exposed**: `chat` (proxies `/v1/chat/completions`) and `list_models` (proxies `/v1/models`). Nothing else until a real need shows up.
  - `chat` input: an OpenAI-style `messages` array (`role`/`content`), not a single flattened prompt string — needed for system prompts and multi-turn.
  - `chat` generation params (`temperature`, `max_tokens`, `top_p`, ...) are optional passthrough fields; if omitted, the backend's own defaults apply. No fixed values imposed server-side.
  - `chat`'s `model` field is optional; if omitted, falls back to a default model name from server config (consistent with the single fixed backend for v1).
- **Streaming**: none. `chat` returns the full completion in one response. The MCP host consumes tool results as a whole anyway, so token-by-token streaming buys nothing here — revisit only if generation length starts hitting client timeouts.
- **HTTP client to the backend**: plain `HttpClient` + `System.Text.Json` over minimal DTOs. Not the OpenAI SDK — too much for two endpoints.
- **Process model**: runs as a plain foreground process for now (`dotnet run` / manual start). No systemd/Windows service packaging until this is more than a dev setup.
- **Backends**: exactly one fixed backend base URL from config. No multi-workstation/multi-model routing until a real need shows up.
- **Cloudflare Tunnel**: quick tunnel (`cloudflared tunnel --url http://localhost:PORT`) for v1 — no Cloudflare account or domain required, one command, ephemeral `trycloudflare.com` URL. Named tunnel (stable hostname) is a later upgrade, and it has a hard prerequisite a script can't remove: the user must already have a domain on Cloudflare-managed DNS.
- **Distribution**: self-contained single-file publish per OS (`dotnet publish -r linux-x64|win-x64 --self-contained -p:PublishSingleFile=true`) — the user runs one binary, no separate .NET runtime install required.
- **Launcher scripts**: `start.sh` (Linux/macOS) and `start.bat`/`start.ps1` (Windows), each starting `cloudflared` quick tunnel + the MCP server together and printing the resulting URL and bearer token to paste into the MCP client config. Goal: one command/double-click to go from nothing running to a pastable connection.

## Deferred to v1.1+ (not decided, not started)

Explicitly out of scope for v1, kept here so they aren't forgotten or re-debated from scratch:

- **Streaming** chat responses (token-by-token), if generation length ever hits client timeouts.
- **systemd / Windows service** packaging, once this is more than a dev setup.
- **Multi-backend / multi-model routing** (more than one workstation or model to pick from).
- **Named Cloudflare Tunnel** (stable hostname), once a user has a domain on Cloudflare DNS.
- **`embeddings` tool** (proxy `/v1/embeddings`), if a RAG-style use case shows up.
- **`load_model` / `unload_model` tools**, if backend model switching becomes a real need — note this would likely reintroduce backend-specific code paths (LM Studio vs `llama-server` differ here).
- **Dedicated `health`/`status` tool** — currently skipped since a failed `chat` call already surfaces backend-unreachable errors, but flagged as a likely v1.1 addition (cheap, useful for diagnosing "backend down" vs "bad request" before spending a call).
- **Multimodal input** (image/document analysis via vision-capable local models, e.g. Qwen-VL) — `chat` would need to accept image content parts in `messages`, not just text. Directly serves the "why this exists" goal above: document/image analysis is exactly the kind of repeatable task worth offloading from paid tokens.

## Stack priority

1. .NET 10 (preferred)
2. Python
3. Node.js

Pick the first one that fits once implementation starts; don't split the implementation across stacks.
