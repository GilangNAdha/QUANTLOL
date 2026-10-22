# Free market-data endpoint probe

Generated 2026-10-22 09:22 UTC on Linux. Each row was actually requested; nothing is copied from docs.

| class | provider | HTTP | ms | bars/items | key | volume | notes |
|---|---|---|---|---|---|---|---|
| crypto | Binance (data-api mirror) | 200 | 470.3 | 50 | none | yes | 2026-10-22 05:15 .. 2026-10-22 09:20 \|  |
| crypto | Binance (api.binance.com) | 451 | 99.6 | 0 | none | no | HTTP 451 |
| crypto | Kraken | 200 | 63.7 | 721 | none | yes | 2026-10-19 21:20 .. 2026-10-22 09:20 \| last row is the in-progress candle and must be dropped |
| crypto | Coinbase Exchange | 200 | 78.4 | 350 | none | yes | 2026-10-21 04:10 .. 2026-10-22 09:15 \| newest first, max 300 candles |
| crypto | Bybit | 403 | 90.9 | 0 | none | yes | HTTP 403 |
| crypto | OKX | 200 | 248.6 | 50 | none | yes | 2026-10-22 05:15 .. 2026-10-22 09:20 \| newest first; last row may be unconfirmed |
| crypto | Bitstamp | 200 | 467.0 | 50 | none | yes | 2026-10-22 05:15 .. 2026-10-22 09:20 \|  |
| crypto | Deribit (perp OHLC) | 200 | 237.6 | 289 | none | yes | 2026-10-21 09:20 .. 2026-10-22 09:20 \| bars from the venue's own chart feed; low volume on perps can be 0 |
| crypto-options | Deribit (options chain) | 200 | 215.9 | 930 | none | n/a | instruments=930 .. with_oi=747 \| options chain: open_interest + mark_iv per strike -> real (not proxy) BTC exposure data |
| forex | Yahoo EURUSD=X | 200 | 199.5 | 1278 | none | no (zero/absent) | 2026-10-15 23:00 .. 2026-10-22 09:22 \|  |
| forex | Frankfurter (ECB) | 200 | 96.6 | 1 | none | no | 2026-10-21 .. EUR/USD 1.1234 \| ECB daily fixing only - not intraday |
| forex | open.er-api.com | 200 | 80.7 | 1 | none | no | Thu, 22 Oct 2026 .. rates=166 \| daily rates, no OHLC |
| forex | Twelve Data (no key) | 401 | 140.3 | 0 | REQUIRED | n/a | HTTP 401 |
| forex | Binance EURUSDT (not spot FX) | 200 | 447.0 | 1 | none | n/a | EURUSDT is a stablecoin pair, NOT spot EUR/USD |
| control | Yahoo GC=F | 200 | 181.5 | 998 | none | yes | 2026-10-18 22:10 .. 2026-10-22 09:12 \|  |
| control | Yahoo BTC-USD | 200 | 194.4 | 1266 | none | yes | 2026-10-18 00:00 .. 2026-10-22 09:22 \|  |
| app | signals.providers/binance | 200 | 559 | 199 | none | yes | 2026-10-21 16:45 .. 2026-10-22 09:15 \| binance:BTCUSDT \| venue spot klines (Binance) \| dropped 1 in-progress candle |
| app | signals.providers/coinbase | 200 | 44 | 350 | none | yes | 2026-10-21 04:10 .. 2026-10-22 09:15 \| coinbase:BTC-USD \| venue candles (Coinbase Exchange), max 300 |
| app | signals.providers/kraken | 200 | 214 | 720 | none | yes | 2026-10-19 21:20 .. 2026-10-22 09:15 \| kraken:XBTUSD \| venue OHLC (Kraken); last row is in progress \| dropped 1 in-progress candle |
| app | signals.providers/okx | 200 | 148 | 199 | none | yes | 2026-10-21 16:45 .. 2026-10-22 09:15 \| okx:BTC-USDT \| venue spot candles (OKX) |
| app | signals.providers/bitstamp | 200 | 833 | 199 | none | yes | 2026-10-21 16:45 .. 2026-10-22 09:15 \| bitstamp:btcusd \| venue OHLC (Bitstamp) \| dropped 1 in-progress candle |
| app | signals.providers/deribit | 200 | 227 | 212 | none | yes | 2026-10-21 15:40 .. 2026-10-22 09:15 \| deribit:BTC-PERPETUAL \| venue chart feed (Deribit perp) \| dropped 1 in-progress candle |
| app | signals.providers/bybit | ERR | 33 | 0 | none | ? | HTTP 403 |
| app | signals / Deribit OI levels | 200 | 735 | 930 | none | n/a | strikes=50 .. levels=3 \| spot=81926 \| put_wall@75000, gamma_flip@90000, call_wall@95000 \| OI concentration, not gamma |
