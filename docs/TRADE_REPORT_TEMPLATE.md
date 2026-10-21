# Trade-performance report template

Copy, fill, delete nothing. Every table needs its **n** and, where relevant, its interval.

**Read this before filling it in**

- Fewer than 30 trades: describe the sample, do not describe an edge.
- Fewer than 20 trading days: Sharpe/Sortino are undefined here; the bot prints NaN for a reason.
- A positive net P&L over 10 trades is a coin toss, not a result.
- Backtest, in-sample, out-of-sample and forward results go in **separate sections** and are never
  pooled into one "overall performance".

---

## 0. Run identity

| Item | Value |
|---|---|
| Period | ___ to ___ |
| Phase | backtest / in-sample / out-of-sample / demo forward / live forward |
| Instrument / data | XAUUSD (or GC=F proxy: say so) |
| Bot commit | ___ |
| Parameters changed vs previous phase | ___ |
| Account / balance start / end | ___ / ___ / ___ |
| Volume | 0.01 lot, fixed (yes / no) |

## 1. Headline (with sample size and uncertainty)

| Metric | Value | n | Interval / comment |
|---|---|---|---|
| Trades | ___ | | |
| Net P&L | ___ | | |
| Expectancy / trade | ___ | ___ | 95% bootstrap CI: ___ to ___ |
| Profit factor | ___ | | CI (if computed): ___ to ___ |
| Win rate | ___ % | ___ | |
| Average win / average loss | ___ / ___ | ___ / ___ | |
| Payoff ratio | ___ | | |
| Max drawdown (closed trades) | ___ | | |
| Recovery factor | ___ | | |
| Sharpe (daily) | ___ | ___ days | undefined < 20 days |
| Sortino (daily) | ___ | ___ days | undefined < 20 days |
| Average holding time / median | ___ / ___ | | |
| Average MAE / MFE | ___ / ___ | | |

## 2. Costs

| Item | Value |
|---|---|
| Gross P&L | ___ |
| Commission | ___ |
| Modelled slippage (backtest) or measured slippage (forward) | ___ |
| Spread paid (average, measured) | ___ |
| Net P&L | ___ |
| Cost as % of gross | ___ |

If costs are larger than gross for a strategy with a small average win, say so plainly.

## 3. Breakdowns (each with n)

### By session
| Session | n | Net | PF | Expectancy |
|---|---|---|---|---|
| Asia | | | | |
| London | | | | |
| New York | | | | |

### By setup / regime
| Setup | n | Net | PF | Expectancy |
|---|---|---|---|---|

| Regime (balance / discovery) | n | Net | PF | Expectancy |
|---|---|---|---|---|

### By direction
| Side | n | Net | PF | Expectancy |
|---|---|---|---|---|

Breakdowns with n < 10 are anecdotes. Mark them as such.

## 4. Exits

| Exit reason | n | Net | Average R | Comment |
|---|---|---|---|---|
| stop_loss | | | | |
| stop_gap | | | | gap through the stop: the loss can exceed the plan |
| take_profit | | | | |
| break_even | | | | |
| trailing | | | | |
| time_stop | | | | |
| manual / other | | | | |

## 5. Excursions (MAE / MFE)

| Bucket | n | Average MAE | Average MFE | Comment |
|---|---|---|---|---|
| winners | | | | |
| losers | | | | |

State whether stops were ever "almost" hit before the target (MFE before stop). This is how you
learn whether the stop is inside normal noise — without moving it after the fact.

## 6. Latency observed during this run

| Stage | p50 | p95 | p99 | max | n |
|---|---|---|---|---|---|
| tick->signal_eval | | | | | |
| signal->risk_approval | | | | | |
| risk->order_submit | | | | | |
| submit->broker_ack | | | | | |
| submit->confirmed_fill | | | | | |
| decision->fill_total | | | | | |

Latency is not a P&L input in the backtest. If the forward run's fill latency is materially worse
than the assumed slippage, the backtest P&L is optimistic; say by how much.

## 7. Rejections

| Reason | Count | Share | Comment |
|---|---|---|---|

The rejection log is part of the result. "The bot rejected 92% of setups because their structural
stop exceeded $4.81 at 0.01 lot" is a finding about the mandate, not a malfunction.

## 8. Data quality

| Input | Source | Quality label | Fresh % of session | Notes |
|---|---|---|---|---|
| Bars / ticks | broker | broker tick volume (not centralised) | — | |
| GEX | manual file / adapter | exact / proxy / stale | | never derived from gold candles |
| VIX | Cboe daily file | delayed daily | | broad context only |
| Delta / Vanna | — | unavailable | | not used unless a legitimate source exists |

## 9. What this run does not show

- It does not show that the strategy is profitable in general.
- It does not show out-of-sample performance unless this section *is* out-of-sample.
- If trades < 30 or days < 20, it does not support any inference at all.
- Any conclusion that depends on the backtest's assumed spread/slippage is only as good as that
  assumption (see the cost-sensitivity table in `research/out/report.md`).

## 10. Decision

- Continue to the next phase? yes / no
- Why: ___
- What would falsify this decision: ___
