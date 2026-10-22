# The terminal — OpenTerminal UI + the QuantLOL engine

QuantLOL used to have two disconnected halves: a Python signal engine with a small dashboard, and
a cTrader bot. This adds the missing half — a real terminal workspace — by vendoring
[OpenTerminal](https://github.com/ErTasselli/OpenTerminal) (MIT, pinned at `aed097c`) and putting
the engine inside it as a first-class panel.

**No API key is needed anywhere in this stack.** The terminal's widgets read free public endpoints
(Yahoo, Stooq, Nasdaq, SEC EDGAR, FRED, ECB, Binance, CoinGecko); the engine reads free public
endpoints too (Binance, Kraken, Coinbase, OKX, Bitstamp, Deribit, Yahoo). The Twelve Data key is now
*optional*, not required.

```
browser ──► Next.js  :3000 ──► Express :4000 ──┬──► free providers (Yahoo/Stooq/Binance/…)
                │                              │
                └── /api/quantlol/* ───────────┴──► Python engine :8080  (signals/, stdlib only)
                                                       │
                                                       └──► free venues (Binance/Yahoo/…) + risk rules
```

## What the signal panel shows

`SIGNALS` in the left add-widget bar (or `w-signal` in a fresh workspace):

* state badge — **LONG / SHORT / NO SIGNAL**, straight from the engine;
* the engine's own `reason` string (`NoSetup`, `RiskCapExceeded`, `SweepReclaim`, …) verbatim;
* for an actionable signal: entry, stop, target, risk in USD, net R:R, score, stop size in ticks,
  and the session — all copied, never recomputed;
* feed line: source, quality label (`live` / `proxy` / `no-volume`), bar count, bar age, staleness;
* engine health issues (stale data, missing volume, VIX/GEX state) in plain language;
* the last five signals and the engine's uptime;
* a permanent `advisory only · no orders` footer.

Every value comes from `GET /api/quantlol/state`. The component has no arithmetic of its own and no
way to place anything — neither does the engine.

## API contract

The Express server forwards read-only GETs to the engine (`QUANTLOL_URL`, default
`http://127.0.0.1:8080`; `QUANTLOL_TIMEOUT_MS`, default 4000):

| route | forwards to | returns |
|---|---|---|
| `GET /api/quantlol/status` | `/api/state` | `{ok, url, latencyMs, engine, config, latest, health}` |
| `GET /api/quantlol/state` | `/api/state` | the engine's full snapshot, unchanged |
| `GET /api/quantlol/signals` | `/api/state` | `{config, recent_signals, stats}` |

When the engine is not running you get **HTTP 503** with a start-up hint, and the panel says
`ENGINE OFFLINE` plus the command to fix it — it does not sit blank pretending the market is quiet.

## Running it

```bash
# from the repo root, one command for the whole stack
./tools/terminal_dev.sh                 # gold, committed snapshot (works offline)
./tools/terminal_dev.sh --btc           # BTC, keyless Binance
./tools/terminal_dev.sh --xau-spot      # spot gold via Twelve Data (optional key)
./tools/terminal_dev.sh --eurusd        # EUR/USD via Yahoo, order-flow disabled
```

or by hand:

```bash
python3 -m signals serve --port 8080    # terminal 1
cd terminal && npm ci && npm run dev     # terminal 2 → http://localhost:3000
```

Point the panel at a different engine (e.g. a second one on :8081 running BTC) with
`QUANTLOL_URL=http://127.0.0.1:8081 npm run dev` inside `terminal/`.

First run installs the terminal's Node dependencies (~300 packages). `npm ci` is exact, from the
vendored lockfile.

## Keys: what is actually required

| key | needed for | required? |
|---|---|---|
| — | every market-data widget | **no** (all providers are keyless public endpoints) |
| — | the QuantLOL signal panel | **no** |
| `API_KEY` | portfolio + AI routes | auto-generated on first run into `terminal/data/.api-key`; it is a **local shared secret**, not a vendor key |
| `ANTHROPIC_API_KEY` | the optional AI assistant widget | no — the widget is simply unavailable without it |
| `TWELVEDATA_API_KEY` | spot gold / spot FX from Twelve Data | no — optional, see `docs/LIVE_DATA.md` |

## What is ours and what is upstream

Ours: the whole `signals/` engine, the bridge route, the signal panel, the render check, this doc.
Upstream: everything else in `terminal/` — widget framework, providers, caching, rate limiting,
auth, Dockerfiles. The exact diff is listed in [`terminal/NOTICE.md`](../terminal/NOTICE.md).

Upgrading upstream means re-copying the pinned tree and re-applying that diff; the vendored copy is
not tracked automatically.

## Verified

Run in this repository, not asserted:

| check | result |
|---|---|
| `npx tsc --noEmit -p server` / `-p web` | 0 errors |
| `npm run build -w server && npm run build -w web` | server compiled; Next production build ✓ "Compiled successfully", "Linting and checking validity of types" ✓ |
| `GET :4000/api/quantlol/state` | 200, engine payload, 28 ms |
| `GET :3000/api/quantlol/state` (through Next) | 200, `XAUUSD (proxy: COMEX GC=F) · neutral/NoSetup · 4152.1001` |
| `npm run check:signals` | renders the real panel against the live engine; 15 assertions incl. the LONG plan branch, `RENDER CHECK PASSED` |

## Limits, stated plainly

* **The panel is only as live as the engine.** With the default config the engine reads the
  committed `research/data/gc_5m.csv.gz` snapshot, so the panel shows an honest
  `offline snapshot: last bar is … min old` warning. Use `--btc` (or a CSV export) for live bars.
* **The upstream widgets need internet.** In a sandbox without outbound access the chart, quotes and
  news panels error out while the signal panel keeps working — it talks to localhost. That is a
  property of the environment, not of the integration.
* **No execution.** The engine score, the $5 risk cap, the $15/day and $30 drawdown latches are
  enforcement inside the engine and the cBot; the terminal only displays them.
* **Portfolio/AI widgets still require the upstream local `API_KEY`.** It is generated for you, not
  requested from a vendor.
