# QuantLOL Signals — standalone signal application

A separate product from the cBot: same rules, **no broker, no cTrader, no orders**. It reads free
market data, evaluates the newest **closed** bar, and publishes an advisory signal (or an explicit
rejection) to a local dashboard, JSON API and CSV logs.

```
data (local snapshot | Yahoo GC=F | your CSV export)
        │
        ▼
  validate: timestamps, duplicates, OHLC sanity, freshness, quality label
        │
        ▼
  quantlol_ref  ← the SAME rule module the cBot core is parity-tested against
        │
        ├── signal   : long/short + entry/stop/target + $ risk + R:R + evidence
        └── rejection: reason + detail  (NoSetup, RiskCapExceeded, VixFilter, …)
        │
        ▼
  files (JSONL/CSV) + HTTP dashboard + JSON API
```

## 1. Why it is separate

| | cBot (`src/QuantLOL`) | Signal app (`signals/`) |
|---|---|---|
| Runs inside | cTrader Automate (C#, net6.0) | plain Python 3.9+, stdlib only |
| Can trade | yes | **no — no order API, no broker class** |
| Data | broker bars/ticks | committed snapshots, Yahoo GC=F, or your CSV |
| Risk cap | enforced on execution | enforced on the *signal* ($5, wider stop = rejected) |
| Account limits | $15/day, $30 DD, kill switch | not applicable (no account) |
| Output | trades + rejections CSV, cTrader log | JSONL/CSV + dashboard |
| Needs | .NET SDK, cTrader | nothing but Python |

The rules are **not** duplicated: `signals/core_bridge.py` imports `research/quantlol_ref.py`, the
module the C# core is pinned to by `tests/QuantLOL.Tests/ParityTests.cs` and by the CI
"regenerate the fixture and diff it" step. One change to a rule shows up in all three places at once.

## 2. Install

```bash
git clone https://github.com/GilangNAdha/QUANTLOL.git
cd QUANTLOL
python3 -m unittest tests.test_signals -v     # 54 tests, offline
```

No pip install, no virtualenv, no API key. Python 3.9+ (uses `dataclasses`, `http.server`).

## 3. First run

```bash
python3 -m signals doctor          # config + data + external inputs, like the cBot banner
python3 -m signals once            # evaluate the newest closed bar, print the record
python3 -m signals replay --limit 10   # what would it have said historically?
```

`doctor` prints the arithmetic that matters:

```
config : 0.01 lots, 100.00 oz/lot -> $1.00 per 1.00 price move
risk   : cap $5.00 -> max structural stop 4.810 price units (481 ticks)
costs  : commission $0.14 round turn, slippage 5 ticks, assumed spread 30 ticks (ASSUMPTION)
data   : DATA:proxy rows=13612 last=2026-10-21 19:54 age=565.4m src=file:gc_5m.csv.gz
vix    : VIX:STALE(delayed,age=1985m)
```

## 4. Dashboard

```bash
python3 -m signals serve --port 8080
# → http://localhost:8080
```

The page polls `/api/state` every 5 s and shows: the current read, the plan (entry/stop/target/risk/
R:R/evidence), data quality and freshness, VIX/GEX status, recent signals, and the **rejection
breakdown** for the session. "Refresh now" fires `POST /api/evaluate`.

Read-only endpoints:

| Endpoint | Method | Purpose |
|---|---|---|
| `/` | GET | dashboard |
| `/api/state` | GET | everything the page renders |
| `/api/health` | GET | `{ok, issues[]}` for monitoring/uptime checks |
| `/api/config` | GET | effective configuration summary |
| `/api/signals?limit=N` | GET | last N emitted signals |
| `/api/evaluate` | POST | run one evaluation now |

There is no order endpoint. The test suite asserts that no function or import resembling order
submission exists anywhere in the package.

## 5. Configuration

`python3 -m signals --config config/signals.example.json doctor`, or write a file with
`python3 -m signals config --init myconfig.json`. Everything is validated at start-up; a bad value
stops the app with a clear message instead of running a different configuration silently.

| Key | Default | Meaning |
|---|---|---|
| `data_mode` | `local` | `local` (snapshot), `live` (Yahoo GC=F), `csv` (your export) |
| `data_path` / `csv_path` | `research/data/gc_5m.csv.gz` | where the bars come from |
| `live_symbol` / `live_range` | `GC=F` / `60d` | live polling source and window |
| `timeframe_minutes` | `5` | 1, 3, 5, 15, 30, 60, 240 |
| `max_bar_age_minutes` | `30` | older than this and the data is reported stale |
| `lots`, `ounces_per_lot`, `tick` | `0.01`, `100`, `0.01` | contract arithmetic (`money_per_price`) |
| `commission_round_trip`, `slippage_ticks`, `assumed_spread_ticks` | `0.14`, `5`, `30` | costs used in the risk check (assumptions) |
| `max_risk_usd`, `min_rr` | `5.0`, `1.5` | the mandate |
| `min_score`, `swing_lookback`, `sweep_window`, … | as the bot | rule parameters |
| `enable_vix`, `vix_path`, `vix_max_age_minutes` | `true`, samples, `1440` | VIX veto |
| `enable_gex`, `gex_path`, `gex_max_age_minutes` | `false`, samples, `240` | GEX levels |
| `poll_seconds`, `output_dir`, `host`, `port` | `60`, `signals/out`, `0.0.0.0`, `8080` | service |

## 6. Output files (`signals/out/`)

| File | Content |
|---|---|
| `signals.jsonl` | every emitted signal, full record (append-only) |
| `signals.csv` | the same in flat form for spreadsheets |
| `evaluations.jsonl` | **every** evaluated bar, including rejections — the audit trail |
| `state.json` | last emitted bar/direction and last evaluation key, used to suppress duplicate rows after a restart |

One row is written per **(bar, outcome)**, not per poll: a static feed polled every minute does not
grow the logs without bound, and a restart does not re-append what it already wrote. The counters
`stats.evaluations` (polls) and `stats.evaluations_skipped` (deduplicated) show the difference.

## 7. Data sources and their labels

| Source | Label in the app | Note |
|---|---|---|
| `research/data/gc_*.csv.gz` | `proxy` | COMEX **GC=F** front month, not spot XAUUSD |
| Yahoo `GC=F` live | `proxy` | delayed, free; timeframe-dependent history limits |
| Binance / Kraken / Coinbase / OKX / Bitstamp / Bybit / Deribit (keyless) | `live` | that venue's own book, real volume, in-progress candle dropped — see `docs/LIVE_DATA.md` |
| Yahoo `EURUSD=X` live | `no-volume` | intraday FX with **no volume**; the EURUSD preset disables order-flow confirmation |
| Twelve Data (free key) | `vendor-agg` | 800 req/day; aggregated FX, not one venue's book |
| Deribit BTC options chain | `oi-derived` | open-interest concentrations; **not** dealer gamma (Deribit publishes no gamma) |
| Your broker CSV | `broker` | anything with `time,open,high,low,close[,volume]` |
| Cboe VIX daily | `delayed` | one value per day; broad context only |
| GEX file | `manual` / `proxy` | `manual` for XAUUSD, `proxy` only for a converted GLD/IAU file |

The app never invents volume, never derives dealer exposure from gold candles, and never calls a
daily file "live". In `data_mode: live` with no provider named it walks a **tested fallback chain**
(`binance → bybit → okx → coinbase → kraken → bitstamp → deribit → yahoo`) and reports each failure in
the feed note; if every venue fails you get `DATA:INVALID`, not an empty-but-happy feed.

## 7b. Other markets (BTC, EUR/USD)

```bash
python3 -m signals --preset btc doctor        # keyless Binance BTCUSDT
python3 -m signals --preset btc serve         # dashboard on 8080
python3 -m signals --preset eurusd once       # Yahoo EUR/USD, order-flow disabled
```

The preset changes the instrument arithmetic (`contract_units`, tick, volume) and the feed, never
the risk mandate. Full provider matrix, rate limits and the verified probe table:
**`docs/LIVE_DATA.md`**.

## 8. What it deliberately does not do

- **No execution, no position management, no account state.** Use the cBot for that.
- **No performance stats.** P&L, PF, expectancy, MAE/MFE require executed trades; the app has none,
  and inventing them would be fabrication (`docs/SIGNAL_APP_COVERAGE.md` row 13).
- **No L2/footprint/real delta.** Those need a paid feed, which your brief excludes.

## 9. Tests

```bash
python3 -m unittest tests.test_signals -v     # 54 tests
```

They cover configuration validation, row-level data rejection, freshness, VIX/GEX acceptance and
refusal, cap compliance, agreement with the shared rule function, duplicate suppression, store
round-trip, the HTTP endpoints, and the structural separation from the cBot (no broker imports, no
order functions). CI runs them on every push and fails the build if they fail.
