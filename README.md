# llama-mcp

MCP server per parlare con un'istanza locale di **llama.cpp** (`llama-server`) o **LM Studio** in esecuzione su una workstation, raggiungibile in remoto in modo sicuro **senza aprire porte pubbliche**.

> **Stato**: in fase di design, nessun codice ancora. Vedi [`CLAUDE.md`](./CLAUDE.md) per l'architettura concordata.

## Architettura

```
MCP client (remoto)  --HTTP/SSE via tunnel-->  Cloudflare Tunnel  -->  MCP server (sulla workstation)  --localhost-->  llama.cpp / LM Studio
```

- Il server MCP gira **sulla stessa workstation** di llama.cpp/LM Studio e li raggiunge via `localhost`.
- L'accesso da remoto passa da un **Cloudflare Tunnel**: nessuna porta pubblica, nessun DNS dinamico da configurare.
- Autenticazione via **API key / bearer token** gestita direttamente dal server MCP.
- Target: l'API **OpenAI-compatible** esposta sia da `llama-server` che da LM Studio.

## Stack

Priorità: **.NET 10** → Python → Node.js.

## Licenza

Vedi [LICENSE](./LICENSE).
