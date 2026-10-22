# Free market-data endpoint probe

Generated 2026-10-22 10:04 UTC on Linux. Each row was actually requested; nothing is copied from docs.

| class | provider | HTTP | ms | bars/items | key | volume | notes |
|---|---|---|---|---|---|---|---|
| crypto | Binance (data-api mirror) | 200 | 685.5 | 50 | none | yes | 2026-10-22 05:55 .. 2026-10-22 10:00 \|  |
| crypto | Binance (api.binance.com) | 451 | 210.9 | 0 | none | no | HTTP 451 |
| crypto | Kraken | 200 | 162.9 | 721 | none | yes | 2026-10-19 22:00 .. 2026-10-22 10:00 \| last row is the in-progress candle and must be dropped |
| crypto | Coinbase Exchange | 200 | 38.8 | 350 | none | yes | 2026-10-21 04:55 .. 2026-10-22 10:00 \| newest first, max 300 candles |
| crypto | Bybit | 403 | 154.0 | 0 | none | yes | HTTP 403 |
| crypto | OKX | 200 | 244.8 | 50 | none | yes | 2026-10-22 05:55 .. 2026-10-22 10:00 \| newest first; last row may be unconfirmed |
| crypto | Bitstamp | 200 | 383.5 | 50 | none | yes | 2026-10-22 05:55 .. 2026-10-22 10:00 \|  |
| crypto | Deribit (perp OHLC) | 200 | 155.6 | 289 | none | yes | 2026-10-21 10:00 .. 2026-10-22 10:00 \| bars from the venue's own chart feed; low volume on perps can be 0 |
| crypto-options | Deribit (options chain) | 200 | 162.4 | 930 | none | n/a | instruments=930 .. with_oi=752 \| options chain: open_interest + mark_iv per strike -> real (not proxy) BTC exposure data |
| forex | Yahoo EURUSD=X | 200 | 82.6 | 1286 | none | no (zero/absent) | 2026-10-15 23:00 .. 2026-10-22 10:04 \|  |
| forex | Frankfurter (ECB) | 200 | 29.0 | 1 | none | no | 2026-10-21 .. EUR/USD 1.1234 \| ECB daily fixing only - not intraday |
| forex | open.er-api.com | 200 | 68.0 | 1 | none | no | Thu, 22 Oct 2026 .. rates=166 \| daily rates, no OHLC |
| forex | Twelve Data (no key) | 401 | 36.1 | 0 | REQUIRED | n/a | HTTP 401 |
| forex | Twelve Data (key from CI secret) | SKIP | 0 | 0 | set? no | n/a | - .. - \| secret TWELVEDATA_API_KEY is not configured for this workflow run |
| forex | Binance EURUSDT (not spot FX) | 200 | 643.7 | 1 | none | n/a | EURUSDT is a stablecoin pair, NOT spot EUR/USD |
| control | Yahoo GC=F | 200 | 83.8 | 1006 | none | yes | 2026-10-18 22:10 .. 2026-10-22 09:54 \|  |
| control | Yahoo BTC-USD | 200 | 92.5 | 1274 | none | yes | 2026-10-18 00:00 .. 2026-10-22 10:04 \|  |
| app | signals.providers/binance | 200 | 800 | 199 | none | yes | 2026-10-21 17:25 .. 2026-10-22 09:55 \| binance:BTCUSDT \| venue spot klines (Binance) \| dropped 1 in-progress candle |
| app | signals.providers/coinbase | 200 | 23 | 349 | none | yes | 2026-10-21 04:55 .. 2026-10-22 09:55 \| coinbase:BTC-USD \| venue candles (Coinbase Exchange), max 300 \| dropped 1 in-progress candle |
| app | signals.providers/kraken | 200 | 69 | 720 | none | yes | 2026-10-19 22:00 .. 2026-10-22 09:55 \| kraken:XBTUSD \| venue OHLC (Kraken); last row is in progress \| dropped 1 in-progress candle |
| app | signals.providers/okx | 200 | 206 | 199 | none | yes | 2026-10-21 17:25 .. 2026-10-22 09:55 \| okx:BTC-USDT \| venue spot candles (OKX) |
| app | signals.providers/bitstamp | 200 | 225 | 199 | none | yes | 2026-10-21 17:25 .. 2026-10-22 09:55 \| bitstamp:btcusd \| venue OHLC (Bitstamp) \| dropped 1 in-progress candle |
| app | signals.providers/deribit | 200 | 151 | 212 | none | yes | 2026-10-21 16:20 .. 2026-10-22 09:55 \| deribit:BTC-PERPETUAL \| venue chart feed (Deribit perp) \| dropped 1 in-progress candle |
| app | signals.providers/bybit | ERR | 20 | 0 | none | ? | HTTP 403 |
| app | signals / Deribit OI levels | 200 | 765 | 930 | none | n/a | strikes=50 .. levels=3 \| spot=81949 \| put_wall@75000, gamma_flip@90000, call_wall@95000 \| OI concentration, not gamma |
| app | signals.providers/twelvedata | SKIP | 0 | 0 | not set | n/a | no TWELVEDATA_API_KEY in this run; the key was verified manually against the same endpoints (see docs/LIVE_DATA.md) |
