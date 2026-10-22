# Free market-data endpoint probe

Generated 2026-10-22 09:11 UTC on Linux. Each row was actually requested; nothing is copied from docs.

| class | provider | HTTP | ms | bars/items | key | volume | notes |
|---|---|---|---|---|---|---|---|
| crypto | Binance (data-api mirror) | 200 | 496.5 | 50 | none | yes | 2026-10-22 05:05 .. 2026-10-22 09:10 \|  |
| crypto | Binance (api.binance.com) | 451 | 158.6 | 0 | none | no | HTTP 451 |
| crypto | Kraken | 200 | 240.8 | 721 | none | yes | 2026-10-19 21:10 .. 2026-10-22 09:10 \| last row is the in-progress candle and must be dropped |
| crypto | Coinbase Exchange | 200 | 160.6 | 350 | none | yes | 2026-10-21 04:00 .. 2026-10-22 09:05 \| newest first, max 300 candles |
| crypto | Bybit | 403 | 156.3 | 0 | none | yes | HTTP 403 |
| crypto | OKX | 200 | 258.2 | 50 | none | yes | 2026-10-22 05:05 .. 2026-10-22 09:10 \| newest first; last row may be unconfirmed |
| crypto | Bitstamp | 200 | 660.5 | 50 | none | yes | 2026-10-22 05:05 .. 2026-10-22 09:10 \|  |
| crypto | Deribit (perp OHLC) | 200 | 255.9 | 289 | none | yes | 2026-10-21 09:10 .. 2026-10-22 09:10 \| bars from the venue's own chart feed; low volume on perps can be 0 |
| crypto-options | Deribit (options chain) | 200 | 333.3 | 930 | none | n/a | instruments=930 .. with_oi=747 \| options chain: open_interest + mark_iv per strike -> real (not proxy) BTC exposure data |
| forex | Yahoo EURUSD=X | 200 | 309.1 | 1276 | none | no (zero/absent) | 2026-10-15 23:00 .. 2026-10-22 09:10 \|  |
| forex | Frankfurter (ECB) | 200 | 122.5 | 1 | none | no | 2026-10-21 .. EUR/USD 1.1234 \| ECB daily fixing only - not intraday |
| forex | open.er-api.com | 200 | 180.4 | 1 | none | no | Thu, 22 Oct 2026 .. rates=166 \| daily rates, no OHLC |
| forex | Twelve Data (no key) | 401 | 173.6 | 0 | REQUIRED | n/a | HTTP 401 |
| forex | Binance EURUSDT (not spot FX) | 200 | 490.6 | 1 | none | n/a | EURUSDT is a stablecoin pair, NOT spot EUR/USD |
| control | Yahoo GC=F | 200 | 209.5 | 996 | none | yes | 2026-10-18 22:10 .. 2026-10-22 09:01 \|  |
| control | Yahoo BTC-USD | 200 | 217.8 | 1264 | none | yes | 2026-10-18 00:00 .. 2026-10-22 09:11 \|  |
