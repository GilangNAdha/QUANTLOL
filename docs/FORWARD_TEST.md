# Forward-test protocol (demo first, then a small live account if you choose)

The backtest cannot tell you how this bot behaves on your broker. This is the procedure that can,
and the rules that keep the result honest.

## 0. Rules that make the result meaningful

1. **Do not change parameters during a phase.** If you change anything, the run restarts and the
   previous weeks are a different experiment — keep their report but do not pool them.
2. **Record everything, including the boring weeks.** A forward test that only reports good weeks is
   a marketing document.
3. **Never resize to recover a loss.** 0.01 lot fixed. The bot itself cannot do it; do not do it
   manually either.
4. **Publish failures.** If the bot never trades because the $5 cap rejects every structural stop,
   that is the finding, not a bug to hide by widening the cap.

## 1. Phases

| Phase | Duration | Volume | Orders | What it proves |
|---|---|---|---|---|
| A. Dry run | ≥ 5 trading days | 0.01 | none | the bot runs, reconciles, computes decisions, logs, and does not crash |
| B. Demo | ≥ 20 trading days | 0.01 | demo | fills, spreads, slippage, latencies, risk enforcement on a real server |
| C. Live (optional) | ≥ 20 trading days | 0.01 | real | the same, with real money and real friction |

Twenty trading days is the **minimum to report Sharpe/Sortino at all** (the bot refuses to print
them below that). Thirty *trades* is the minimum before any expectancy interval is worth reading;
below that the report says so.

## 2. What to collect

The bot writes two CSVs (paths are printed in the banner) and a summary at stop. Keep all of it.

**Trades journal** — 35 columns, one row per executed trade: decision/fill/exit timestamps, bid/ask,
intended entry/stop/target, submitted and filled volume, realised spread, commission, slippage,
exit reason, gross/net P&L, MAE, MFE, holding time, session, regime, setup type, score, evidence,
data freshness for GEX/VIX, per-stage latencies, and the signal key.

**Rejections journal** — one row per rejected setup with the same decision context and the exact
reason (`SpreadTooWide`, `StaleCoreData`, `RiskCapExceeded`, `DailyLossLimit`, `EquityDrawdownLimit`,
`PositionOpen`, `RewardTooLow`, `Conflict`, `NoSetup`, `OrderRejected`, ...).

If a file is empty after phase A, that is a finding too: either nothing qualified (check the
rejection counts) or logging is broken (check the file path and permissions).

## 3. Weekly checkpoint (copy into your notes)

```
Week:                from ____ to ____          phase: A / B / C
Trading days:        ___
Signals evaluated:   ___   Rejected: ___   Executed: ___
Trades: ___   Wins: ___   Net P&L: ___   Max closed-trade DD: ___
Rejections by reason: (top 5)
Median / p95 decision->fill latency: ___ ms / ___ ms
External data: GEX fresh __% of session, VIX fresh __%
Incidents: (disconnects, rejects, restarts, manual interventions)
```

## 4. Restart / reconciliation check (do this at least once per phase)

1. Open a position on demo. Note its id and the signal key in the label.
2. Stop the bot. Restart it (or restart cTrader).
3. Confirm from the log that reconciliation **adopted** the position (same id), re-applied the
   protective stop, and did **not** open a second one.
4. Confirm the duplicate-suppression line: the same signal key cannot be entered twice.

If reconciliation cannot identify the position, the bot must go monitor-only. That is correct
behaviour — report it, don't disable it.

## 5. Reporting

Fill `docs/TRADE_REPORT_TEMPLATE.md` from the CSVs, not from memory. Keep:
- the in-sample backtest section separate from the forward section;
- the sample size next to every number;
- the confidence interval (the bot computes it for ≥ 5 trades) next to expectancy;
- a list of every parameter changed between phases.

## 6. Stopping rules

Stop the forward test and write it up if any of these happens:

- realised daily loss exceeds the configured limit (should be impossible; if it happens it is a bug
  or a gap — document which);
- the kill switch latches (the mandate says stop, not "wait for a better week");
- reconciliation fails or a duplicate order appears;
- a broker spec change is detected (`verified=False` with a different lot size or tick value);
- the measured `decision->fill` p95 exceeds your planned slippage allowance consistently.
