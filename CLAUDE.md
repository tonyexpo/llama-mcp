# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project status

v1 complete and verified end-to-end against a real LM Studio instance: `chat` and `list_models` tools, self-contained publish, `start.sh`/`start.ps1`+`start.bat` launching the app + a Cloudflare quick tunnel together, and a full OAuth 2.1 authorization server (see below) — both the static-bearer-token path and the OAuth authorization-code+PKCE path were verified end-to-end via curl, including token persistence across a server restart. See README.md for user-facing usage instructions — don't duplicate them here, keep this file to the "why" and the decisions.

## OAuth (added mid-v1, not originally planned)

Both claude.ai's and ChatGPT's web "custom connector" flows turned out to require a real OAuth 2.1 dance (discovery, dynamic client registration, authorization code + PKCE) — a static bearer token in a header, which is all Claude Code CLI needs, isn't accepted by either web client. Since "usable from web clients" was the actual original goal, this pulled OAuth into v1 scope rather than deferring it.

- **Library**: `OpenIddict` (`OpenIddict.AspNetCore` + `OpenIddict.EntityFrameworkCore`), not hand-rolled — this is security-critical protocol code (PKCE validation, token issuance/expiry, redirect URI validation), exactly where you reach for a mature dependency instead of writing it yourself.
- **Dynamic client registration is hand-rolled**: OpenIddict 7.5.0 has no built-in RFC 7591 endpoint (verified by inspecting the shipped assembly — no `SetClientRegistrationEndpointUris` or similar exists). `OAuthEndpoints.cs`'s `POST /register` is a minimal subset (accepts `redirect_uris` + `client_name`, creates a public PKCE-required OpenIddict application, returns `client_id`) — not a full RFC 7591 implementation (no client update/delete, no auth on the registration endpoint itself since there's no secret to gate it with).
- **Storage**: SQLite (not JSON) at `~/.config/llama-mcp/oauth.db` (via `Environment.SpecialFolder.ApplicationData`, resolves cross-platform to `~/.config` on Linux/macOS and `%APPDATA%` on Windows) — chosen over hand-rolling JSON persistence for OpenIddict's application/authorization/token stores because that would mean reimplementing several elaborate store interfaces ourselves on a security-sensitive path; SQLite is a single local file, not a "lock-in" dependency, EF Core's provider is already battle-tested. Schema created via `Database.EnsureCreatedAsync()`, not EF migrations — no need for a `Migrations/` folder on a single-tenant local tool.
- **Signing/encryption keys**: a persisted RSA key at `~/.config/llama-mcp/signing.key` (`AppData.LoadOrCreateSigningKey()`), not OpenIddict's `AddDevelopmentEncryptionCertificate()` helpers — those lean on OS certificate stores, which behave inconsistently across Linux/Windows. A plain persisted RSA key file works identically on both and is why tokens survive a server restart instead of every restart invalidating every issued token.
- **"Consent screen" is not a real login system**: `GET/POST /connect/authorize` renders a bare HTML form asking for the *same* static bearer token from `AuthOptions` as the approval credential. There's exactly one owner of this server, so a full user/password/session system would be pure overhead — the real security boundary is still "do you know the token," same as before OAuth existed here.
- **Dual auth on the MCP endpoint**: the middleware in `Program.cs` accepts *either* the static bearer token (unchanged path, keeps Claude Code CLI/`claude mcp add --header` working exactly as before) *or* a valid OpenIddict-issued access token (`OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme`). `/connect/*`, `/register`, and both `/.well-known/*` paths are always public (listed explicitly in `publicPaths`) — they must be reachable without a token for the dance to bootstrap at all.
- **`DisableTransportSecurityRequirement()` is required, not optional**: OpenIddict defaults to rejecting non-HTTPS requests. In our real topology, TLS *always* terminates at the Cloudflare tunnel — this process's own Kestrel listener only ever sees plain HTTP, even for genuine remote/HTTPS traffic (the quick tunnel forwards to `http://localhost:PORT`). Standard for anything behind a reverse proxy; don't remove this thinking it's a leftover dev shortcut.
- **`app.UseForwardedHeaders(...)` is also required**: `cloudflared` sends `X-Forwarded-Proto: https` (confirmed by inspecting raw request headers), but nothing reads it without this middleware — every generated URL (issuer, endpoints, redirects) would otherwise be built with `http://` even for real HTTPS traffic. `KnownIPNetworks`/`KnownProxies` are cleared because Kestrel only ever hears from `cloudflared` on loopback; there's no untrusted hop where a spoofed header could matter. Must run before `UseAuthentication()` below.
- **Protected Resource Metadata** (`GET /.well-known/oauth-protected-resource`, RFC 9728) and the `WWW-Authenticate: Bearer resource_metadata="..."` header on 401s are what let an MCP client discover the auth server automatically instead of requiring the user to paste a client ID manually.
- **`registration_endpoint` must be injected into the discovery document manually**: OpenIddict's configuration endpoint has no idea `/register` exists (it's our own hand-rolled endpoint, not something OpenIddict manages) and won't advertise it by default. Without this, claude.ai can't find dynamic registration and falls back to asking the user for a manually-entered client ID. Fixed via an `AddEventHandler<HandleConfigurationRequestContext>` inline handler in `Program.cs` that adds `context.Metadata["registration_endpoint"]`.
- **`app.UseAuthentication()` is required, not optional, even though nothing here uses cookie/session auth**: OpenIddict Server registers itself as an ASP.NET Core authentication scheme, and its non-passthrough endpoints (the discovery document is the one that matters here) only run through the full middleware pipeline — including `UseForwardedHeaders` above — if this is present. Without it, the discovery endpoint still responded (200 OK, valid JSON), but every URL in it came out `http://` instead of `https://`, silently regardless of the forwarded-headers fix. This one cost real debugging time — if OAuth URLs start coming back as `http://` again, check this middleware is still registered before assuming the forwarded-headers config is wrong.

## Running locally (dev, no tunnel)

```
dotnet build
Auth__BearerToken=<token> dotnet run
```

`Auth:BearerToken` has no default and fails startup validation if unset (`ValidateOnStart`) — set it via the `Auth__BearerToken` env var, not `appsettings.json` (that file is tracked in git, don't put a real token in it). `Backend:BaseUrl` defaults to `http://localhost:1234` (LM Studio's default). Point an MCP client at `http://localhost:5181/` (or whatever `--urls` you pass) with header `Authorization: Bearer <token>` — or drive the OAuth dance (`POST /register` → `GET/POST /connect/authorize` with the same token → `POST /connect/token`) if you're testing the web-client path. OAuth state lives in `~/.config/llama-mcp/` (`oauth.db` + `signing.key`) — delete both to reset to a clean slate.

For the full run (publish + tunnel), use `start.sh`/`start.ps1` — see README.md.

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
- **Auth**: the MCP server owns authentication directly, not delegated to the tunnel layer. Two accepted credentials on the MCP endpoint: the static API key / bearer token (original design, still what Claude Code CLI uses), or an OpenIddict-issued OAuth access token (added mid-v1 once web clients turned out to require it — see "OAuth" section below).
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
