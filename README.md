# llama-mcp

MCP server per parlare con un'istanza locale di **llama.cpp** (`llama-server`) o **LM Studio** in esecuzione su una workstation, raggiungibile in remoto in modo sicuro **senza aprire porte pubbliche**.

> **Stato**: v1 completo e verificato end-to-end (server, script di avvio, publish self-contained, quick tunnel, OAuth) con LM Studio reale. Vedi [`CLAUDE.md`](./CLAUDE.md) per tutte le decisioni architetturali.

## A cosa serve

Per chi ha già un abbonamento Claude, collegare uno o più agenti locali ha senso solo se fa risparmiare token: delegare a modelli più piccoli/economici, in esecuzione sul proprio hardware, i compiti di base, ripetibili o comunque delegabili (estrazione dati, classificazione, bozze semplici, analisi di documenti/immagini...) invece di spendere token Sonnet/Opus. Se un compito richiede vero ragionamento, tanto vale restare su Sonnet — questo è il filtro con cui giudichiamo ogni nuova funzionalità.

## Architettura

```
MCP client (remoto)  --HTTP/SSE via tunnel-->  Cloudflare Tunnel  -->  MCP server (sulla workstation)  --localhost-->  llama.cpp / LM Studio
```

- Il server MCP gira **sulla stessa workstation** di llama.cpp/LM Studio e li raggiunge via `localhost`.
- L'accesso da remoto passa da un **Cloudflare quick tunnel**: nessuna porta pubblica, nessun DNS dinamico da configurare, nessun account Cloudflare richiesto.
- Autenticazione via **API key / bearer token** (Claude Code, script/CLI) oppure via **OAuth 2.1** con PKCE (claude.ai, ChatGPT — i client web richiedono OAuth, un header statico non basta). Entrambe gestite direttamente dal server MCP, nessuna delega al tunnel.
- Target: l'API **OpenAI-compatible** esposta sia da `llama-server` che da LM Studio.

## Tool MCP esposti (v1)

- **`chat`** — proxy verso `/v1/chat/completions`. Accetta un `messages` array stile OpenAI (system/user/assistant), parametri di generazione opzionali (`temperature`, `max_tokens`, `top_p`, ...) passati al backend, e un `model` opzionale (default configurato lato server). Nessuno streaming: risposta completa in un'unica chiamata.
- **`list_models`** — proxy verso `/v1/models`, elenca i modelli disponibili sul backend.

## Prerequisiti

- [.NET 10 SDK](https://dotnet.microsoft.com/) (solo per compilare/pubblicare, non per eseguire il binario già pubblicato).
- [`cloudflared`](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/) nel `PATH`.
- `llama-server` o LM Studio avviati sulla workstation, con un modello caricato, con il server API attivo (porta di default `1234` per LM Studio).

## Avvio rapido (server + tunnel)

```bash
# Linux/macOS
./start.sh

# Windows: doppio click su start.bat, oppure
powershell -File start.ps1
```

Lo script:
1. genera un bearer token casuale (o usa quello in `LLAMA_MCP_TOKEN` se impostato);
2. avvia il server MCP (usa il binario pubblicato in `publish/<rid>/` se esiste, altrimenti fa `dotnet run` in modalità dev);
3. avvia `cloudflared tunnel --url http://localhost:5181`;
4. stampa a schermo l'URL pubblico (`https://xxxx.trycloudflare.com`) e il token da usare nel client MCP.

L'URL è effimero: cambia ad ogni riavvio dello script.

## Pubblicare il binario self-contained

Per non richiedere il runtime .NET sulla workstation:

```bash
# Linux
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish/linux-x64

# Windows
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish\win-x64
```

`start.sh`/`start.ps1` cercano il binario in questi percorsi esatti.

## Collegare un client MCP

Con l'URL e il token stampati da `start.sh`/`start.ps1`:

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

**claude.ai / ChatGPT (web)** — questi richiedono OAuth, non accettano un header statico:

1. Impostazioni → Connettori → "Aggiungi connettore personalizzato" → incolla l'URL (`https://xxxx.trycloudflare.com/`).
2. Il client scopre da solo gli endpoint OAuth e si registra automaticamente (dynamic client registration) — se invece ti chiede un "Client ID" a mano, la registrazione automatica non è scattata; verifica che l'URL sia esattamente quello stampato dallo script.
3. Si apre una schermata di consenso del server: inserisci lì lo stesso bearer token stampato da `start.sh`/`start.ps1` (fa da password di approvazione, non viene mai condiviso col client web).
4. Fatto — il client web ora tiene un proprio access/refresh token, distinto dal bearer token master.

Lo stato OAuth (client registrati, token emessi, chiave di firma) è salvato in `~/.config/llama-mcp/` (`%APPDATA%\llama-mcp\` su Windows) e sopravvive ai riavvii del server — non serve ri-autorizzare ogni client ad ogni riavvio.

## Sviluppo locale (senza tunnel)

```bash
Auth__BearerToken=<un-token-a-scelta> dotnet run
```

Il server ascolta su `http://localhost:5181/`. Ogni richiesta MCP deve avere l'header `Authorization: Bearer <token>` (senza, risponde 401). `Backend:BaseUrl` di default è `http://localhost:1234` (porta di default di LM Studio) — modificabile in `appsettings.json` o via env var `Backend__BaseUrl`.

## Stack

Priorità: **.NET 10** → Python → Node.js.

## Licenza

Vedi [LICENSE](./LICENSE).
