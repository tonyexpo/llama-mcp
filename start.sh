#!/usr/bin/env bash
# Starts the MCP server + a Cloudflare quick tunnel together and prints the
# public URL and bearer token to paste into an MCP client.
set -euo pipefail

PORT="${PORT:-5181}"
TOKEN="${LLAMA_MCP_TOKEN:-$(openssl rand -hex 16 2>/dev/null || head -c16 /dev/urandom | od -An -tx1 | tr -d ' \n')}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN="$SCRIPT_DIR/publish/linux-x64/LlamaMcp"

if [ -x "$BIN" ]; then
  APP_CMD=("$BIN" --urls "http://localhost:$PORT")
else
  echo "Published binary not found at $BIN, falling back to 'dotnet run' (dev mode)." >&2
  APP_CMD=(dotnet run --project "$SCRIPT_DIR" -c Release --urls "http://localhost:$PORT")
fi

if ! command -v cloudflared >/dev/null 2>&1; then
  echo "cloudflared not found. Install it: https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/" >&2
  exit 1
fi

CF_LOG="$(mktemp)"
cleanup() {
  kill "${CF_PID:-}" "${APP_PID:-}" 2>/dev/null || true
  rm -f "$CF_LOG"
}
trap cleanup EXIT INT TERM

Auth__BearerToken="$TOKEN" "${APP_CMD[@]}" &
APP_PID=$!

cloudflared tunnel --url "http://localhost:$PORT" >"$CF_LOG" 2>&1 &
CF_PID=$!

echo "Waiting for tunnel URL..."
URL=""
for _ in $(seq 1 30); do
  URL="$(grep -oE 'https://[a-zA-Z0-9.-]+\.trycloudflare\.com' "$CF_LOG" | head -n1 || true)"
  [ -n "$URL" ] && break
  sleep 1
done

if [ -z "$URL" ]; then
  echo "Could not determine tunnel URL, check $CF_LOG" >&2
  exit 1
fi

echo
echo "MCP server ready:"
echo "  URL:   $URL/"
echo "  Token: $TOKEN"
echo
echo "Press Ctrl+C to stop."
wait
