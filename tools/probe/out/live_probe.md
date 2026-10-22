# Free market-data endpoint probe

Generated 2026-10-22 09:34 UTC on Linux. Each row was actually requested; nothing is copied from docs.

| class | provider | HTTP | ms | bars/items | key | volume | notes |
|---|---|---|---|---|---|---|---|
| crypto | Binance (data-api mirror) | 200 | 600.6 | 50 | none | yes | 2026-10-22 05:25 .. 2026-10-22 09:30 \|  |
| crypto | Binance (api.binance.com) | 451 | 180.0 | 0 | none | no | HTTP 451 |
| crypto | Kraken | 200 | 225.8 | 721 | none | yes | 2026-10-19 21:30 .. 2026-10-22 09:30 \| last row is the in-progress candle and must be dropped |
| crypto | Coinbase Exchange | 200 | 55.0 | 350 | none | yes | 2026-10-21 04:25 .. 2026-10-22 09:30 \| newest first, max 300 candles |
| crypto | Bybit | 403 | 189.8 | 0 | none | yes | HTTP 403 |
| crypto | OKX | 200 | 228.6 | 50 | none | yes | 2026-10-22 05:25 .. 2026-10-22 09:30 \| newest first; last row may be unconfirmed |
| crypto | Bitstamp | 200 | 671.4 | 50 | none | yes | 2026-10-22 05:25 .. 2026-10-22 09:30 \|  |
| crypto | Deribit (perp OHLC) | 200 | 246.4 | 289 | none | yes | 2026-10-21 09:30 .. 2026-10-22 09:30 \| bars from the venue's own chart feed; low volume on perps can be 0 |
| crypto-options | Deribit (options chain) | 200 | 195.2 | 930 | none | n/a | instruments=930 .. with_oi=749 \| options chain: open_interest + mark_iv per strike -> real (not proxy) BTC exposure data |
| forex | Yahoo EURUSD=X | 200 | 81.5 | 1280 | none | no (zero/absent) | 2026-10-15 23:00 .. 2026-10-22 09:34 \|  |
| forex | Frankfurter (ECB) | 200 | 128.2 | 1 | none | no | 2026-10-21 .. EUR/USD 1.1234 \| ECB daily fixing only - not intraday |
| forex | open.er-api.com | 200 | 90.1 | 1 | none | no | Thu, 22 Oct 2026 .. rates=166 \| daily rates, no OHLC |
| forex | Twelve Data (no key) | 401 | 162.4 | 0 | REQUIRED | n/a | HTTP 401 |
| forex | Twelve Data (key from CI secret) | SKIP | 0 | 0 | set? no | n/a | - .. - \| secret TWELVEDATA_API_KEY is not configured for this workflow run |
| forex | Binance EURUSDT (not spot FX) | 200 | 816.2 | 1 | none | n/a | EURUSDT is a stablecoin pair, NOT spot EUR/USD |
| control | Yahoo GC=F | 200 | 66.9 | 1000 | none | yes | 2026-10-18 22:10 .. 2026-10-22 09:24 \|  |
| control | Yahoo BTC-USD | 200 | 81.7 | 1268 | none | yes | 2026-10-18 00:00 .. 2026-10-22 09:34 \|  |
| app | signals.providers/binance | 200 | 528 | 199 | none | yes | 2026-10-21 16:55 .. 2026-10-22 09:25 \| binance:BTCUSDT \| venue spot klines (Binance) \| dropped 1 in-progress candle |
| app | signals.providers/coinbase | 200 | 46 | 349 | none | yes | 2026-10-21 04:25 .. 2026-10-22 09:25 \| coinbase:BTC-USD \| venue candles (Coinbase Exchange), max 300 \| dropped 1 in-progress candle |
| app | signals.providers/kraken | 200 | 214 | 720 | none | yes | 2026-10-19 21:30 .. 2026-10-22 09:25 \| kraken:XBTUSD \| venue OHLC (Kraken); last row is in progress \| dropped 1 in-progress candle |
| app | signals.providers/okx | 200 | 138 | 199 | none | yes | 2026-10-21 16:55 .. 2026-10-22 09:25 \| okx:BTC-USDT \| venue spot candles (OKX) |
| app | signals.providers/bitstamp | 200 | 392 | 199 | none | yes | 2026-10-21 16:55 .. 2026-10-22 09:25 \| bitstamp:btcusd \| venue OHLC (Bitstamp) \| dropped 1 in-progress candle |
| app | signals.providers/deribit | 200 | 247 | 212 | none | yes | 2026-10-21 15:50 .. 2026-10-22 09:25 \| deribit:BTC-PERPETUAL \| venue chart feed (Deribit perp) \| dropped 1 in-progress candle |
| app | signals.providers/bybit | ERR | 77 | 0 | none | ? | HTTP 403 |
| app | signals / Deribit OI levels | 200 | 695 | 930 | none | n/a | strikes=50 .. levels=3 \| spot=81772 \| put_wall@75000, gamma_flip@90000, call_wall@95000 \| OI concentration, not gamma |
| app | signals.providers/twelvedata | SKIP | 0 | 0 | not set | n/a | no TWELVEDATA_API_KEY in this run; the key was verified manually against the same endpoints (see docs/LIVE_DATA.md) |
