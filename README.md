# llama-mcp

MCP server per parlare con un'istanza locale di **llama.cpp** (`llama-server`) o **LM Studio** in esecuzione su una workstation, raggiungibile in remoto in modo sicuro **senza aprire porte pubbliche**.

> **Stato**: scaffold v1 implementato e verificato end-to-end con LM Studio reale. Mancano ancora: script di avvio, publish self-contained, tunnel Cloudflare. Vedi [`CLAUDE.md`](./CLAUDE.md) per tutte le decisioni architetturali.

## A cosa serve

Per chi ha già un abbonamento Claude, collegare uno o più agenti locali ha senso solo se fa risparmiare token: delegare a modelli più piccoli/economici, in esecuzione sul proprio hardware, i compiti di base, ripetibili o comunque delegabili (estrazione dati, classificazione, bozze semplici, analisi di documenti/immagini...) invece di spendere token Sonnet/Opus. Se un compito richiede vero ragionamento, tanto vale restare su Sonnet — questo è il filtro con cui giudichiamo ogni nuova funzionalità.

## Architettura

```
MCP client (remoto)  --HTTP/SSE via tunnel-->  Cloudflare Tunnel  -->  MCP server (sulla workstation)  --localhost-->  llama.cpp / LM Studio
```

- Il server MCP gira **sulla stessa workstation** di llama.cpp/LM Studio e li raggiunge via `localhost`.
- L'accesso da remoto passa da un **Cloudflare Tunnel** (quick tunnel per v1): nessuna porta pubblica, nessun DNS dinamico da configurare, nessun account Cloudflare richiesto.
- Autenticazione via **API key / bearer token** gestita direttamente dal server MCP.
- Target: l'API **OpenAI-compatible** esposta sia da `llama-server` che da LM Studio.

## Tool MCP esposti (v1)

- **`chat`** — proxy verso `/v1/chat/completions`. Accetta un `messages` array stile OpenAI (system/user/assistant), parametri di generazione opzionali (`temperature`, `max_tokens`, `top_p`, ...) passati al backend, e un `model` opzionale (default configurato lato server). Nessuno streaming: risposta completa in un'unica chiamata.
- **`list_models`** — proxy verso `/v1/models`, elenca i modelli disponibili sul backend.

## Uso oggi (locale, senza tunnel)

```
Auth__BearerToken=<un-token-a-scelta> dotnet run
```

Il server ascolta su `http://localhost:5181/`. Ogni richiesta MCP deve avere l'header `Authorization: Bearer <token>` (senza, risponde 401). `Backend:BaseUrl` di default è `http://localhost:1234` (porta di default di LM Studio).

## Uso previsto (una volta completato)

1. Avvia `llama-server` o LM Studio sulla workstation.
2. Lancia lo script di avvio (`start.sh` su Linux/macOS, `start.bat`/`start.ps1` su Windows): avvia insieme il Cloudflare quick tunnel e il server MCP, e stampa a schermo l'URL pubblico e il bearer token da usare.
3. Incolla URL e token nella configurazione del client MCP (Claude Desktop/Code) da un'altra macchina.

Nessun'installazione del runtime .NET richiesta: il binario sarà pubblicato self-contained per Linux e Windows.

## Stack

Priorità: **.NET 10** → Python → Node.js.

## Licenza

Vedi [LICENSE](./LICENSE).
