# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project status

Pre-implementation. The repo currently contains only a LICENSE file — no code, build system, or tests exist yet. This file describes the agreed architecture/direction so implementation can start from a shared design. Update this file as soon as real structure (build tooling, source layout, tests) lands — do not let it drift from what actually exists.

## What this is

An MCP server that lets a remote MCP client (Claude Desktop, Claude Code, etc.) talk to a local LLM inference server — llama.cpp (`llama-server`) or LM Studio's OpenAI-compatible API — running on a workstation, **without exposing a public port** on that workstation.

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

## Stack priority

1. .NET 10 (preferred)
2. Python
3. Node.js

Pick the first one that fits once implementation starts; don't split the implementation across stacks.
