# Free market-data endpoint probe

Generated 2026-10-22 09:04 UTC on Linux. Each row was actually requested; nothing is copied from docs.

| class | provider | HTTP | ms | bars/items | key | volume | notes |
|---|---|---|---|---|---|---|---|
| crypto | Binance (data-api mirror) | 200 | 583.4 | 50 | none | yes | 2026-10-22 04:55 .. 2026-10-22 09:00 \|  |
| crypto | Binance (api.binance.com) | 451 | 324.2 | 0 | none | no | HTTP 451 |
| crypto | Kraken | 200 | 193.0 | 721 | none | yes | 2026-10-19 21:00 .. 2026-10-22 09:00 \| last row is the in-progress candle and must be dropped |
| crypto | Coinbase Exchange | 200 | 100.9 | 350 | none | yes | 2026-10-21 03:55 .. 2026-10-22 09:00 \| newest first, max 300 candles |
| crypto | Bybit | 403 | 127.0 | 0 | none | yes | HTTP 403 |
| crypto | OKX | 200 | 194.2 | 50 | none | yes | 2026-10-22 04:55 .. 2026-10-22 09:00 \| newest first; last row may be unconfirmed |
| crypto | Bitstamp | 200 | 285.2 | 50 | none | yes | 2026-10-22 04:55 .. 2026-10-22 09:00 \|  |
| crypto | Deribit (perp OHLC) | 200 | 224.9 | 289 | none | yes | 2026-10-21 09:00 .. 2026-10-22 09:00 \| bars from the venue's own chart feed; low volume on perps can be 0 |
| crypto-options | Deribit (options chain) | 200 | 208.4 | 930 | none | n/a | instruments=930 .. with_oi=742 \| options chain: open_interest + mark_iv per strike -> real (not proxy) BTC exposure data |
| forex | Yahoo EURUSD=X | 200 | 327.5 | 1274 | none | no (zero/absent) | 2026-10-15 23:00 .. 2026-10-22 09:04 \|  |
| forex | Frankfurter (ECB) | 200 | 114.6 | 1 | none | no | 2026-10-21 .. EUR/USD 1.1234 \| ECB daily fixing only - not intraday |
| forex | open.er-api.com | 200 | 101.7 | 1 | none | no | Thu, 22 Oct 2026 .. rates=166 \| daily rates, no OHLC |
| forex | Twelve Data (no key) | 401 | 129.6 | 0 | REQUIRED | n/a | HTTP 401 |
| forex | Binance EURUSDT (not spot FX) | 200 | 855.9 | 1 | none | n/a | EURUSDT is a stablecoin pair, NOT spot EUR/USD |
| control | Yahoo GC=F | 200 | 244.9 | 994 | none | yes | 2026-10-18 22:10 .. 2026-10-22 08:54 \|  |
| control | Yahoo BTC-USD | 200 | 257.3 | 1262 | none | yes | 2026-10-18 00:00 .. 2026-10-22 09:04 \|  |
