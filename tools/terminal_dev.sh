#!/usr/bin/env bash
# Runs the whole merged stack: Python signal engine + OpenTerminal (API + web).
#
#   ./tools/terminal_dev.sh            # gold on the committed snapshot
#   ./tools/terminal_dev.sh --btc      # BTC from keyless Binance (needs internet)
#   ./tools/terminal_dev.sh --xau-spot # spot gold via Twelve Data (needs TWELVEDATA_API_KEY)
#
# Ctrl-C stops everything. Nothing here places an order: the engine has no broker connection.
set -euo pipefail
cd "$(dirname "$0")/.."

MODE="${1:-}"
ENGINE_ARGS=(serve --port 8080)
case "$MODE" in
  --btc)      ENGINE_ARGS+=(--preset btc) ;;
  --xau-spot) ENGINE_ARGS+=(--preset xauusd --provider twelvedata --no-order-flow) ;;
  --eurusd)   ENGINE_ARGS+=(--preset eurusd --no-order-flow) ;;
  "")         ;;
  *) echo "unknown option: $MODE (try --btc, --xau-spot, --eurusd)"; exit 2 ;;
esac

if [ ! -d terminal/node_modules ]; then
  echo "installing terminal dependencies (one time)…"
  (cd terminal && npm ci --no-audit --no-fund)
fi

echo "starting the signal engine on :8080 …"
python3 -u -m signals "${ENGINE_ARGS[@]}" &
ENGINE_PID=$!
trap 'kill $ENGINE_PID 2>/dev/null || true' EXIT INT TERM

for _ in $(seq 1 40); do
  curl -sf -o /dev/null http://127.0.0.1:8080/api/health && break
  sleep 0.25
done

echo "starting the terminal (API :4000, web :3000) …"
cd terminal
exec npm run dev
