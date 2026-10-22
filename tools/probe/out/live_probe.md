# Free market-data endpoint probe

Generated 2026-10-22 09:18 UTC on Linux. Each row was actually requested; nothing is copied from docs.

| class | provider | HTTP | ms | bars/items | key | volume | notes |
|---|---|---|---|---|---|---|---|
| crypto | Binance (data-api mirror) | 200 | 597.2 | 50 | none | yes | 2026-10-22 05:10 .. 2026-10-22 09:15 \|  |
| crypto | Binance (api.binance.com) | 451 | 130.0 | 0 | none | no | HTTP 451 |
| crypto | Kraken | 200 | 156.5 | 721 | none | yes | 2026-10-19 21:15 .. 2026-10-22 09:15 \| last row is the in-progress candle and must be dropped |
| crypto | Coinbase Exchange | 200 | 44.7 | 350 | none | yes | 2026-10-21 04:05 .. 2026-10-22 09:10 \| newest first, max 300 candles |
| crypto | Bybit | 403 | 86.7 | 0 | none | yes | HTTP 403 |
| crypto | OKX | 200 | 238.9 | 50 | none | yes | 2026-10-22 05:10 .. 2026-10-22 09:15 \| newest first; last row may be unconfirmed |
| crypto | Bitstamp | 200 | 432.5 | 50 | none | yes | 2026-10-22 05:10 .. 2026-10-22 09:15 \|  |
| crypto | Deribit (perp OHLC) | 200 | 220.2 | 289 | none | yes | 2026-10-21 09:15 .. 2026-10-22 09:15 \| bars from the venue's own chart feed; low volume on perps can be 0 |
| crypto-options | Deribit (options chain) | 200 | 183.5 | 930 | none | n/a | instruments=930 .. with_oi=747 \| options chain: open_interest + mark_iv per strike -> real (not proxy) BTC exposure data |
| forex | Yahoo EURUSD=X | 200 | 280.9 | 1277 | none | no (zero/absent) | 2026-10-15 23:00 .. 2026-10-22 09:18 \|  |
| forex | Frankfurter (ECB) | 200 | 101.0 | 1 | none | no | 2026-10-21 .. EUR/USD 1.1234 \| ECB daily fixing only - not intraday |
| forex | open.er-api.com | 200 | 100.1 | 1 | none | no | Thu, 22 Oct 2026 .. rates=166 \| daily rates, no OHLC |
| forex | Twelve Data (no key) | 401 | 125.6 | 0 | REQUIRED | n/a | HTTP 401 |
| forex | Binance EURUSDT (not spot FX) | 200 | 550.0 | 1 | none | n/a | EURUSDT is a stablecoin pair, NOT spot EUR/USD |
| control | Yahoo GC=F | 200 | 175.4 | 997 | none | yes | 2026-10-18 22:10 .. 2026-10-22 09:08 \|  |
| control | Yahoo BTC-USD | 200 | 232.0 | 1265 | none | yes | 2026-10-18 00:00 .. 2026-10-22 09:18 \|  |
| app | signals.providers/binance | 200 | 684 | 199 | none | yes | 2026-10-21 16:40 .. 2026-10-22 09:10 \| binance:BTCUSDT \| venue spot klines (Binance) \| dropped 1 in-progress candle |
| app | signals.providers/coinbase | 200 | 50 | 350 | none | yes | 2026-10-21 04:05 .. 2026-10-22 09:10 \| coinbase:BTC-USD \| venue candles (Coinbase Exchange), max 300 |
| app | signals.providers/kraken | 200 | 173 | 720 | none | yes | 2026-10-19 21:15 .. 2026-10-22 09:10 \| kraken:XBTUSD \| venue OHLC (Kraken); last row is in progress \| dropped 1 in-progress candle |
| app | signals.providers/okx | 200 | 207 | 199 | none | yes | 2026-10-21 16:40 .. 2026-10-22 09:10 \| okx:BTC-USDT \| venue spot candles (OKX) |
| app | signals.providers/bitstamp | 200 | 473 | 199 | none | yes | 2026-10-21 16:40 .. 2026-10-22 09:10 \| bitstamp:btcusd \| venue OHLC (Bitstamp) \| dropped 1 in-progress candle |
| app | signals.providers/deribit | 200 | 226 | 212 | none | yes | 2026-10-21 15:35 .. 2026-10-22 09:10 \| deribit:BTC-PERPETUAL \| venue chart feed (Deribit perp) \| dropped 1 in-progress candle |
| app | signals.providers/bybit | ERR | 51 | 0 | none | ? | HTTP 403 |
| app | signals / Deribit OI levels | 200 | 736 | 930 | none | n/a | strikes=50 .. levels=3 \| spot=81890 \| put_wall@75000, gamma_flip@90000, call_wall@95000 \| OI concentration, not gamma |
