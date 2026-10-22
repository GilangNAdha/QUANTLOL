# Free live data: forex and BTC (verified, not from vendor marketing)

**Short answer:** for **BTC** there is a genuinely good free, keyless, intraday feed — use
**Binance** (`data-api.binance.vision`, spot klines, real volume) with Kraken / Coinbase / OKX /
Bitstamp / Deribit as fallbacks. For **forex** there is **no keyless intraday feed**; the honest
options are Yahoo `EURUSD=X` (5-minute bars, ~60 days, **no volume**, delayed) or a **free API key**
from Twelve Data (800 requests/day, verified working below). **XAUUSD**: Twelve Data's free key
returns **spot XAU/USD intraday**, which is better than the `GC=F` futures proxy — but with **no
volume**, so order-flow confirmation must be switched off (`--no-order-flow`).

## How this was verified

This table is not copied from provider docs. Every row was requested for real by
`tools/probe/live_data_probe.py`, executed on GitHub Actions (the development sandbox has no
outbound network), run `39754396647` on 2026-10-22 09:18 UTC:

| class | provider | HTTP | latency | bars/items | key | volume | note |
|---|---|---|---|---|---|---|---|
| crypto | **Binance** (`data-api.binance.vision`) | **200** | 597 ms | 50 | none | yes | venue spot klines |
| crypto | Binance (`api.binance.com`) | **451** | 130 ms | 0 | none | – | **geo-blocked from CI** — use the `data-api` mirror |
| crypto | **Kraken** | **200** | 157 ms | 721 | none | yes | last row is the in-progress candle |
| crypto | **Coinbase Exchange** | **200** | 45 ms | 350 | none | yes | newest first, max 300 candles |
| crypto | **OKX** | **200** | 239 ms | 50 | none | yes | newest first, unconfirmed tail row |
| crypto | **Bitstamp** | **200** | 433 ms | 50 | none | yes | |
| crypto | Bybit | **403** | 87 ms | 0 | none | – | **blocked from CI** — do not make it the primary |
| crypto | **Deribit** (perp) | **200** | 220 ms | 289 | none | yes | venue chart feed |
| crypto-options | **Deribit** chain | **200** | 184 ms | 930 contracts | none | n/a | 747 with open interest |
| forex | **Twelve Data (free key)** | 200 | 100–200 ms | up to 5 000/call | **free key** | **no** | 5m spot FX **and spot gold**; 8 req/min, 800/day |
| forex | **Yahoo `EURUSD=X`** | **200** | 281 ms | 1277 | none | **no** | 5m, ~60-day lookback |
| forex | Frankfurter (ECB) | 200 | 101 ms | 1 | none | no | **daily fixing only** |
| forex | open.er-api | 200 | 100 ms | 1 | none | no | daily rates, no OHLC |
| forex | Twelve Data | 401 | 126 ms | 0 | **required** | n/a | works with a free key |
| forex | Twelve Data (key) | **200** | – | **50** | free key | **no** | verified separately, see below |
| forex | Binance `EURUSDT` | 200 | 550 ms | 1 | none | n/a | **not forex** — stablecoin pair |
| control | Yahoo `GC=F` | 200 | 175 ms | 997 | none | yes | the app's existing gold proxy |
| control | Yahoo `BTC-USD` | 200 | 232 ms | 1265 | none | yes | fine, but Binance is better |

### The app's own adapters, run against the same venues

Same run, but this time through `signals.providers` — the exact code path
`python3 -m signals --preset btc` uses. This is what catches a 200 that returns candles the engine
cannot use:

| adapter | HTTP | ms | bars | volume | resolved symbol | note |
|---|---|---|---|---|---|---|
| `signals.providers/binance` | 200 | 684 | 199 | yes | `BTCUSDT` | dropped 1 in-progress candle |
| `signals.providers/kraken` | 200 | 173 | 720 | yes | `XBTUSD` → `XXBTZUSD` | dropped 1 in-progress candle |
| `signals.providers/coinbase` | 200 | 50 | 350 | yes | `BTC-USD` | max 300/call, newest first |
| `signals.providers/okx` | 200 | 207 | 199 | yes | `BTC-USDT` | unconfirmed tail row skipped |
| `signals.providers/bitstamp` | 200 | 473 | 199 | yes | `btcusd` | dropped 1 in-progress candle |
| `signals.providers/deribit` | 200 | 226 | 212 | yes | `BTC-PERPETUAL` | dropped 1 in-progress candle |
| `signals.providers/bybit` | **ERR** | 51 | 0 | – | `BTCUSDT` | **HTTP 403** from the runner |
| Deribit OI levels | 200 | 736 | 930 contracts | n/a | 50 strikes in ±25% of spot | spot 81 890 → put wall 75 000, flip 90 000, call wall 95 000 |

And the engine actually consumed a live bar (`build/live_once_btc.json`, run `39752590718`):

```
state neutral  reason NoSetup  price 81861.24  bar 2026-10-22T08:55:00Z
data  binance:BTCUSDT  quality live  bars 499  fresh true
stats evaluations 1  signals 0  by_reason {NoSetup: 1}  engine 5.685 ms
```

Re-run any time: `python3 tools/probe/live_data_probe.py` (or trigger the `live-data-probe` workflow,
which commits `tools/probe/out/live_probe.{json,md}` and `tools/probe/out/app_doctor.txt`).

## What each provider actually gives you

| provider | endpoint | max bars/call | granularity | rate limit (free) | volume | gotchas |
|---|---|---|---|---|---|---|
| Binance | `data-api.binance.vision/api/v3/klines` | 1000 | 1m…1w | ~1200 weight/min per IP; klines = 2 weight | yes | `api.binance.com` returns **451** from US IPs; the `data-api` mirror does not |
| Kraken | `api.kraken.com/0/public/OHLC` | 720 | 1m…1d | ~1 req/s | yes | **always drop the last row** (in progress) |
| Coinbase | `api.exchange.coinbase.com/products/{id}/candles` | 300 | 60s…1d | ~10 req/s public | yes | column order is `[t, low, high, open, close, volume]` |
| OKX | `www.okx.com/api/v5/market/candles` | 300 | 1m…1d | 20 req/2s | yes | newest first; `confirm=0` rows are still forming |
| Bitstamp | `www.bitstamp.net/api/v2/ohlc/{pair}/` | 1000 | 60s…1d | ~4000/10min | yes | |
| Bybit | `api.bybit.com/v5/market/kline` | 1000 | 1m…1d | 20 req/s | yes | **403 in the probe** — treat as optional |
| Deribit | `get_tradingview_chart_data` | 1000 | 1m…1d | generous | yes | perp volume can be 0 on quiet bars |
| Yahoo | `query1.finance.yahoo.com/v8/finance/chart/{sym}` | 1000+ | 1m…1d | unofficial, may throttle | **no FX volume** | delayed; ToS restricts redistribution; 1m only ~7 days |
| Twelve Data | `api.twelvedata.com/time_series` | 5000 | 1m…1d | 800/day, 8/min | **no** | free key; FX **and** spot gold; `datetime` is account-local unless you pass `timezone=UTC` |

## Twelve Data with a free key (verified 2026-10-22)

The key was exercised against the live API from this session. What it actually returns:

| request | result |
|---|---|
| `time_series?symbol=EUR/USD&interval=5min&timezone=UTC` | **200**, 5-minute **spot** EUR/USD (1.12309 at 09:30Z), **no volume** |
| `time_series?symbol=XAU/USD&interval=5min&timezone=UTC` | **200**, 5-minute **spot gold** (4084.04 at 09:30Z), **no volume** |
| `time_series?symbol=XAU/USD&interval=1min&timezone=UTC` | **200** — 1-minute gold works on the free plan |
| `api_usage` | `plan_category: basic`, **8 req/min**, **800 req/day** (4 used at test time) |
| `time_series` without a key | **401** (as the probe table shows) |

### Three traps this API sets

1. **`datetime` is in your account's timezone, not UTC.** This account defaults to UTC+11: the same
   request returned `20:25` for a bar that closed at `09:25` UTC. The adapter pins
   `timezone=UTC` on every call; without it every bar lands 11 hours in the future, the freshness
   check passes on bars that do not exist yet, and session labels (London/New York) are wrong.
   *Verified:* the same query with `&timezone=UTC` returned bars aligned to wall-clock UTC.
2. **Errors arrive as HTTP 200.** A rate-limit or bad symbol returns
   `{"code": 429, "status": "error", "message": ...}`, so the body is checked, not just the status.
3. **No volume on either FX or spot gold.** The engine's order-flow confirmation needs volume, so on
   this feed it can never pass. The app now says so on the dashboard, in `doctor`, and in the health
   block, and `--no-order-flow` is the explicit way to run without it. It is **not** silently
   disabled.

### Using it

```bash
export TWELVEDATA_API_KEY=...            # never committed; never in a JSON config

# spot gold, 5-minute bars, order-flow off (Twelve Data publishes no volume)
python3 -m signals --preset xauusd --provider twelvedata --no-order-flow doctor
python3 -m signals --preset xauusd --provider twelvedata --no-order-flow serve --port 8080

# spot EUR/USD
python3 -m signals --preset eurusd --provider twelvedata --no-order-flow once
```

The key is read from the environment variable named by `apikey_env` (default
`TWELVEDATA_API_KEY`). `SignalConfig` stores **only the variable name**; a test asserts the key
value never appears in the serialised config.

**Where the key lives:** locally in your shell environment, and in CI as the repository secret
`TWELVEDATA_API_KEY`. The CI probe and the live-smoke job read it via `${{ secrets.TWELVEDATA_API_KEY }}`
and report **SKIP** (not failure) when it is absent. The GitHub token available in this session could
not create repository secrets (HTTP 403), so **adding the secret is a one-time manual step**:
Settings → Secrets and variables → Actions → *New repository secret* → name `TWELVEDATA_API_KEY`.
After that the workflow verifies the key end-to-end on every push that touches `signals/`.

### Gold: spot vs the futures proxy

At the same instant, Twelve Data's **spot XAU/USD** printed `4084.04` while the `GC=F` futures proxy
used by the default gold config printed `4152.10` — a ~$68 basis. They are different instruments, so
signals computed on one must not be described as if they came from the other. The app labels the
feed `XAUUSD (spot, Twelve Data; no volume)` or `XAUUSD (proxy: COMEX GC=F)` accordingly, and the
risk arithmetic ($1.00 per 1.00 price move at 0.01 lot) is identical for both.

## Why forex is the weak spot

* **No keyless intraday forex feed exists.** Every "free forex API" with 5-minute bars either needs
  a key (Twelve Data, Alpha Vantage — and `FX_INTRADAY` is now a **premium** endpoint at 25 req/day)
  or only publishes **daily** fixings (Frankfurter / ECB, open.er-api).
* **OANDA** has no free API tier anymore; **live-rates.com** allows 3 requests/hour, unusable for 5m.
* **Twelve Data's free key is the only genuinely free intraday option**, and it publishes no volume.
* **Yahoo `EURUSD=X`** is the only *keyless* intraday option, and it has **no volume**. The engine's
  order-flow confirmation is a CLV/volume proxy, so with this feed it cannot work at all. The app
  therefore sets `require_order_flow=false` in the EURUSD preset instead of silently computing
  confirmations from a column that is always zero.
* **Binance `EURUSDT` is not EUR/USD.** It is a stablecoin pair. Do not use it as a forex feed.

## What is now built into the app

`signals/providers.py` implements the ten adapters above. They all:

1. **drop the still-forming candle** before the engine sees it (a decision on an open bar repaints);
2. normalise everything to UTC and sort ascending;
3. expose a real `volume` column, and **say so loudly** when a symbol has none;
4. **fail loudly** when an endpoint changes shape, instead of returning zero bars.

`load_feed()` in `signals/datafeed.py` uses a **tested fallback chain** when no provider is named:
`binance → kraken → coinbase → okx → bitstamp → deribit → yahoo → bybit`, and the note on the feed
records which providers failed. Bybit is last on purpose: it returned **403** to the CI runner. If every provider fails you get `DATA:INVALID`, never a silent
empty feed.

### Running it

```bash
# BTC, keyless, Binance spot (default symbol BTCUSDT)
python3 -m signals --preset btc doctor
python3 -m signals --preset btc once
python3 -m signals --preset btc serve --port 8080

# same, but pin a venue (the symbol spelling is resolved per venue: BTCUSDT / BTC-USD / XBTUSD)
python3 -m signals --preset btc --provider kraken once
python3 -m signals --preset btc --provider coinbase once
python3 -m signals --preset btc --provider kraken --symbol XBTUSD once   # explicit override

# EUR/USD via Yahoo (no volume -> order-flow confirmation disabled by the preset)
python3 -m signals --preset eurusd once

# forex with a free key (800 req/day)
export TWELVEDATA_API_KEY=...
python3 -m signals --preset eurusd --provider twelvedata --symbol EUR/USD once

# the original gold setup is untouched
python3 -m signals --preset xauusd doctor
```

`--preset` sets the **instrument arithmetic** as well as the feed, and never touches the risk
mandate ($5/trade, $15/day, $30 DD, fixed volume, no resizing):

| preset | units per 1.00 lot | $ per 1.00 move | max structural stop inside $5 | order-flow |
|---|---|---|---|---|
| `xauusd` | 100 oz | $1.00 | 4.81 price units (481 ticks) | on (GC=F has volume) |
| `btc` | 1 BTC (0.005 lots) | $0.005 | 995.5 USD | on (venue volume is real) |
| `eurusd` | 100 000 EUR | $1000.00 | 0.0049 price units (49 pips) | **off** (no volume) |

## BTC options positioning (replaces nothing, adds a real input)

`gex_source: deribit` pulls the **whole BTC options chain in one keyless call**
(`public/get_book_summary_by_currency`, 930 contracts, 741 with open interest) and derives:

* **call wall** — strike with the largest call open interest near spot,
* **put wall** — strike with the largest put open interest near spot,
* **flip** — strike where cumulative call-minus-put open interest changes sign.

**Honesty note:** Deribit's summary endpoint publishes `mark_iv` and `open_interest` but **no
gamma** - and it does not even return a `strike_price` field, so the strike is parsed out of the
instrument name (`BTC-27MAR26-100000-C`). These are therefore *open-interest concentrations*, not
dealer gamma. The status string says
`oi-derived` and the payload carries the same disclaimer. This is still strictly better than the
file-based `GLD` proxy that was previously the only option, which is why `enable_gex` remains
`false` by default: turning it on now means something.

```json
{ "enable_gex": true, "gex_source": "deribit", "gex_currency": "BTC" }
```

## What this does NOT solve

* **XAUUSD still has no free keyless intraday source.** `GC=F` remains a *proxy* (COMEX front
  month, delayed), and every signal produced from it inherits that caveat. Nothing in this work
  changes that; it is documented rather than papered over.
* **No consolidated tape.** Each venue publishes its own book. BTC basis across venues is small
  but non-zero, so a signal from Binance is a Binance signal, not "the" BTC price.
* **Venue risk is real.** Bybit and `api.binance.com` were blocked from the CI IP; a feed that
  works today can be geo-blocked tomorrow, which is exactly why the fallback chain exists.
* **Rate limits are per IP and can change without notice.** Polling every 60 s is nowhere near any
  of them; polling the fallback chain every 5 s would not be.
