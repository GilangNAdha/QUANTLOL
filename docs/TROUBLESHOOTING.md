# Troubleshooting

Work top to bottom. The bot is designed to fail closed: when it cannot prove something, it refuses
to trade and says why in the log. "It does not trade" is usually a configured guard doing its job.

## 0. Where to look first

1. The start-up banner: `verified=`, `MONITOR-ONLY MODE`, `external:`, `limits:`.
2. The rejection journal. Every rejected setup has a reason; the top reason is usually the answer.
3. `PrintSummary()` at stop: trade count, rejections, latency table, performance markdown.

If you see no banner at all, the bot is not attached to a chart/symbol — that is a cTrader issue, not
a bot issue.

---

## 1. `verified=False` / monitor-only mode

The banner prints a reason. Typical ones:

| Reason | Meaning | Fix |
|---|---|---|
| `symbol name mismatch` | chart symbol ≠ `Expected symbol` | attach the correct symbol or change the parameter |
| `LotSize<=0` | broker did not report a contract size | wrong symbol or broker feed not loaded; do not trade |
| `tick value mismatch` | `TickValue` disagrees with `LotSize × TickSize` | the symbol's tick value is non-standard; the bot refuses rather than mis-size risk |
| `volume step cannot express 0.01 lot` | broker minimum/step larger than a 0.01-lot unit | the broker cannot accept the mandated size; stop |
| `min stop distance > requested` | `MinStopLossDistance` is wider than the structural stop | the broker cannot host this stop; the bot skips, it does not tighten |
| commission unknown | commission model not in the supported set | set `Commission per lot (USD)` explicitly; verify on a statement |

Monitor-only is intentional. Do not edit the code to bypass it; fix the cause.

## 2. It never opens a trade

Check the rejection reasons in order of likelihood:

| Reason | What it means |
|---|---|
| `RiskCapExceeded` | the **structural** stop is wider than ~4.81 USD allows at 0.01 lot. Correct behaviour; the bot will not tighten the stop to fit. Expect this to dominate on gold. |
| `NoSetup` | no sweep + confirmation in the window. Normal on most bars. |
| `RewardTooLow` | structure offered less than the minimum net R:R after costs. |
| `SpreadTooWide` | spread above `Max spread (ticks)`; check session/time of day. |
| `VixFilter` | VIX at/above the threshold and `Block on high VIX` is on. |
| `StaleCoreData` / `QuoteStale` | bars or quotes older than allowed. |
| `ExternalDataRequired` | GEX/VIX required by config but unavailable. |
| `DailyLossLimit` / `EquityDrawdownLimit` / kill switch | the mandate says stop. Wait for the next day, or reset deliberately. |
| `PositionOpen` | one position at a time. |
| `Cooldown` / `DuplicateSuppressed` | too soon after the previous trade/signal. |
| `EntryDrift` | price moved more than `Max entry drift (ticks)` between decision and fill. |
| `MarketClosed` | outside trading hours or a holiday. |
| `SpecUnverified` | see section 1. |

If the reason is `RiskCapExceeded` for weeks, the honest conclusion is that a $5 cap at 0.01 lot
does not fit this instrument's normal stop distances. Raising the cap is a mandate change, not a
bug fix.

## 3. Orders are rejected

| Broker error | Meaning | What the bot does |
|---|---|---|
| `BadVolume` | volume not expressible/too small/large | logs and stops trying that signal; never resizes |
| `NoMoney` | insufficient free margin for 0.01 lot | logs; check the margin estimate in the banner |
| `InvalidStopLossTakeProfit` | stop too close or on the wrong side | logs; the plan is skipped |
| `MarketClosed` | symbol closed | logs |
| `Disconnected` | connection lost | logs, reconciles when reconnected |
| `Timeout` | no answer in time | reconciles by position/order id; does not resend blindly |
| `TechnicalError` | broker-side failure | logs; one in-flight order rule still applies |

If a position exists without a stop after a rejection, the next tick's `EnsureProtectiveStop` puts
it back (Absolute protection), and that event is logged.

## 4. Duplicate orders after a restart

The bot's design is: one in-flight order, label `PREFIX-signalKey`, reconcile positions by label and
id at start, never re-submit an order it cannot account for. If you see two positions:

1. Check whether they carry the same signal key (a duplicate bug — report it) or different keys (two
   signals, one per bar, which the cooldown should have prevented).
2. Check the restart log: did reconciliation run before `OnStart` returned?
3. The duplicate-suppression check uses the signal key, which contains direction, bar time and entry
   price. A new bar with a new key is a new signal by design.

## 5. External data

| Symptom | Check |
|---|---|
| `GEX:INVALID` | file missing/unreadable, wrong instrument, or levels outside the sane band around live spot |
| `GEX:STALE` | `as_of` older than the allowed age; the `StalePolicy` decides block vs ignore |
| `VIX:STALE` | daily VIX is *expected* to be a day old; the freshness window is in minutes, adjust it deliberately |
| levels rejected with "outside sane range" | the file is on the wrong price scale (e.g. a raw GLD share price instead of USD/oz). Convert it and label it `proxy`. |
| HTTP errors | the bot backs off and retries; it never blocks a tick on the network |

Never "fix" a missing GEX by deriving it from gold candles. The bot will not do it, and neither
should you.

## 6. Journal and reporting

| Symptom | Cause |
|---|---|
| empty trades CSV | no trades executed, or `Dry run` is on |
| empty rejections CSV | nothing was evaluated (wrong timeframe / no history) |
| CSV missing columns | an old file from a previous version; the header is versioned — archive and start a new file |
| `summary failed: ...` in the log | the report threw; the trades are still in the CSV. Report the message. |

## 7. Backtest vs demo differences

These are expected and are why forward testing exists:

- backtest fills at the next bar open; live fills at the current ask/bid with slippage;
- backtest spread is a constant assumption; live spread varies by session and news;
- backtest has no requotes/rejects/disconnects;
- backtest latency is zero; live latency is measured and reported separately.

If the demo's realised slippage is consistently worse than `Slippage allowance (ticks)`, the
backtest is optimistic by exactly that difference times `money_per_price` per trade.

## 8. Things that are **not** bugs

- No trades for days at a time: the filters are strict by design (`NoSetup` is the base rate).
- Rejecting an attractive setup because its stop is 6 USD wide: that is the $5 mandate.
- Kill switch stays latched after equity recovers: by design; only an explicit reset clears it.
- Sharpe is `NaN`: fewer than 20 trading days.
- A confidence interval is missing: fewer than 5 trades.
