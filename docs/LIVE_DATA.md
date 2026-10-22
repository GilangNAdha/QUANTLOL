# Free live data: forex and BTC (verified, not from vendor marketing)

**Short answer:** for **BTC** there is a genuinely good free, keyless, intraday feed — use
**Binance** (`data-api.binance.vision`, spot klines, real volume) with Kraken / Coinbase / OKX /
Bitstamp / Deribit as fallbacks. For **forex** there is **no keyless intraday feed**; the honest
options are Yahoo `EURUSD=X` (5-minute bars, ~60 days, **no volume**, delayed) or a **free API key**
from Twelve Data (800 requests/day). For **XAUUSD there is nothing new** — this app's existing
`GC=F` Yahoo proxy remains the only free intraday source.

## How this was verified

This table is not copied from provider docs. Every row was requested for real by
`tools/probe/live_data_probe.py`, executed on GitHub Actions (the development sandbox has no
outbound network), run `39752161783` on 2026-10-22 09:01 UTC:

| class | provider | HTTP | latency | bars/items | key | volume | note |
|---|---|---|---|---|---|---|---|
| crypto | **Binance** (`data-api.binance.vision`) | **200** | 600 ms | 50 | none | yes | venue spot klines |
| crypto | Binance (`api.binance.com`) | **451** | 54 ms | 0 | none | – | **geo-blocked from CI** — use the `data-api` mirror |
| crypto | **Kraken** | **200** | 30 ms | 721 | none | yes | last row is the in-progress candle |
| crypto | **Coinbase Exchange** | **200** | 30 ms | 350 | none | yes | newest first, max 300 candles |
| crypto | **OKX** | **200** | 179 ms | 50 | none | yes | newest first, unconfirmed tail row |
| crypto | **Bitstamp** | **200** | 262 ms | 50 | none | yes | |
| crypto | Bybit | **403** | 33 ms | 0 | none | – | **blocked from CI** — do not make it the primary |
| crypto | **Deribit** (perp) | **200** | 255 ms | 289 | none | yes | venue chart feed |
| crypto-options | **Deribit** chain | **200** | 190 ms | 930 contracts | none | n/a | 741 with open interest |
| forex | **Yahoo `EURUSD=X`** | **200** | 108 ms | 1274 | none | **no** | 5m, ~60-day lookback |
| forex | Frankfurter (ECB) | 200 | 743 ms | 1 | none | no | **daily fixing only** |
| forex | open.er-api | 200 | 33 ms | 1 | none | no | daily rates, no OHLC |
| forex | Twelve Data | 401 | 108 ms | 0 | **required** | n/a | works with a free key |
| forex | Binance `EURUSDT` | 200 | 865 ms | 1 | none | n/a | **not forex** — stablecoin pair |
| control | Yahoo `GC=F` | 200 | 120 ms | 994 | none | yes | the app's existing gold proxy |
| control | Yahoo `BTC-USD` | 200 | 88 ms | 1262 | none | yes | fine, but Binance is better |

Re-run any time: `python3 tools/probe/live_data_probe.py` (or trigger the `live-data-probe` workflow).

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
| Twelve Data | `api.twelvedata.com/time_series` | 5000 | 1m…1d | 800/day, 8/min | optional | needs a free key; `time_series` FX is on the free tier |

## Why forex is the weak spot

* **No keyless intraday forex feed exists.** Every "free forex API" with 5-minute bars either needs
  a key (Twelve Data, Alpha Vantage — and `FX_INTRADAY` is now a **premium** endpoint at 25 req/day)
  or only publishes **daily** fixings (Frankfurter / ECB, open.er-api).
* **OANDA** has no free API tier anymore; **live-rates.com** allows 3 requests/hour, unusable for 5m.
* **Yahoo `EURUSD=X`** is the only keyless intraday option, and it has **no volume**. The engine's
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
`binance → bybit → okx → coinbase → kraken → bitstamp → deribit → yahoo`, and the note on the feed
records which providers failed. If every provider fails you get `DATA:INVALID`, never a silent
empty feed.

### Running it

```bash
# BTC, keyless, Binance spot (default symbol BTCUSDT)
python3 -m signals --preset btc doctor
python3 -m signals --preset btc once
python3 -m signals --preset btc serve --port 8080

# same, but pin a venue / symbol
python3 -m signals --preset btc --provider kraken --symbol XBTUSD once
python3 -m signals --preset btc --provider coinbase --symbol BTC-USD once

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
gamma**, so these are *open-interest concentrations*, not dealer gamma. The status string says
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
