# Free market-data endpoint probe

Generated 2026-10-22 09:08 UTC on Linux. Each row was actually requested; nothing is copied from docs.

| class | provider | HTTP | ms | bars/items | key | volume | notes |
|---|---|---|---|---|---|---|---|
| crypto | Binance (data-api mirror) | 200 | 523.7 | 50 | none | yes | 2026-10-22 05:00 .. 2026-10-22 09:05 \|  |
| crypto | Binance (api.binance.com) | 451 | 160.4 | 0 | none | no | HTTP 451 |
| crypto | Kraken | 200 | 210.9 | 721 | none | yes | 2026-10-19 21:05 .. 2026-10-22 09:05 \| last row is the in-progress candle and must be dropped |
| crypto | Coinbase Exchange | 200 | 113.3 | 350 | none | yes | 2026-10-21 03:55 .. 2026-10-22 09:00 \| newest first, max 300 candles |
| crypto | Bybit | 403 | 133.6 | 0 | none | yes | HTTP 403 |
| crypto | OKX | 200 | 313.5 | 50 | none | yes | 2026-10-22 05:00 .. 2026-10-22 09:05 \| newest first; last row may be unconfirmed |
| crypto | Bitstamp | 200 | 646.1 | 50 | none | yes | 2026-10-22 05:00 .. 2026-10-22 09:05 \|  |
| crypto | Deribit (perp OHLC) | 200 | 280.9 | 289 | none | yes | 2026-10-21 09:05 .. 2026-10-22 09:05 \| bars from the venue's own chart feed; low volume on perps can be 0 |
| crypto-options | Deribit (options chain) | 200 | 267.2 | 930 | none | n/a | instruments=930 .. with_oi=747 \| options chain: open_interest + mark_iv per strike -> real (not proxy) BTC exposure data |
| forex | Yahoo EURUSD=X | 200 | 255.8 | 1275 | none | no (zero/absent) | 2026-10-15 23:00 .. 2026-10-22 09:07 \|  |
| forex | Frankfurter (ECB) | 200 | 105.0 | 1 | none | no | 2026-10-21 .. EUR/USD 1.1234 \| ECB daily fixing only - not intraday |
| forex | open.er-api.com | 200 | 139.4 | 1 | none | no | Thu, 22 Oct 2026 .. rates=166 \| daily rates, no OHLC |
| forex | Twelve Data (no key) | 401 | 140.8 | 0 | REQUIRED | n/a | HTTP 401 |
| forex | Binance EURUSDT (not spot FX) | 200 | 515.4 | 1 | none | n/a | EURUSDT is a stablecoin pair, NOT spot EUR/USD |
| control | Yahoo GC=F | 200 | 195.9 | 995 | none | yes | 2026-10-18 22:10 .. 2026-10-22 08:58 \|  |
| control | Yahoo BTC-USD | 200 | 201.3 | 1263 | none | yes | 2026-10-18 00:00 .. 2026-10-22 09:08 \|  |
