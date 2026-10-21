# Research write-up: what was tested, what it showed, what it does not show

The full generated report (tables, every trade, every rejection) is in `research/out/report.md`,
`metrics.json`, `trades.csv` and `rejections.csv`. This page is the summary and the interpretation.
Numbers quoted here come from that run; if you change the harness or the data, re-read the report
instead of this page.

## 1. Question

Do the QuantLOL modules — SMC structure, AMT value context, order-flow confirmation, VIX context —
produce a risk-adjusted improvement over a plain SMC baseline, on XAUUSD-like intraday data, under the
**$5 / 0.01 lot** mandate?

Secondary question: how much does the answer depend on cost assumptions and parameter choices?

**Not** asked: whether the strategy is profitable in live trading. That requires a forward test
(`docs/FORWARD_TEST.md`), which has not been run.

## 2. Data and engine

| Item | Value |
|---|---|
| Price data | COMEX **GC=F** continuous front month via Yahoo Finance (free, delayed). This is a **proxy** for XAUUSD, not spot: level and basis differ. |
| 5-minute history | 13,612 bars, 2026-08-12 → 2026-10-21 |
| 1-hour history | 13,737 bars, 2024-05-30 → 2026-10-21 |
| VIX | Cboe public daily history, 9,298 observations (1990 → 2026-10-20) |
| Engine | `research/quantlol_ref.py`, a stdlib port of the C# core, pinned to it by `tests/QuantLOL.Tests/ParityTests.cs` |
| Determinism | no randomness except a fixed-seed bootstrap (seed 42) |

Execution model, deliberately pessimistic:

- a decision at bar close fills at the **next bar open**;
- the full modelled spread is paid (half on entry, half on exit);
- adverse slippage (5 ticks) on entry and on stop exits;
- if a bar touches both stop and target, the **stop** is assumed first;
- gaps through the stop fill at the gap price;
- break-even/trailing only move on **closed** bars (no intrabar clairvoyance);
- commission is round-turn per trade.

## 3. Configurations compared

| Variant | Contents |
|---|---|
| A | SMC only: sweep + confirmed CHoCH, dealing-range location |
| B | A + AMT (session profile, value-area scoring, profile-derived targets) |
| C | B + GEX levels — **untested**, no legitimate historical exposure series exists |
| D | B + order-flow (broker tick-volume delta proxy) confirmation |
| E | D + VIX context |

Each variant runs twice:

1. **Executable**: $5 risk cap, $15 daily loss, $30 equity-drawdown kill switch — the mandate.
2. **Counterfactual**: cap relaxed to $50 and account limits disabled, to obtain a sample large
   enough to compare modules. These runs are **not tradable** and the cBot never runs this way.

## 4. Headline results

Authoritative tables (PF, expectancy, intervals, drawdown, rejection counts) are in
`research/out/report.md` sections 2–6. The numbers below are copied from that run.

**Executable ($5 cap, account limits on)** — 95% bootstrap interval of expectancy in brackets:

| data | variant | trades | net USD | PF | expectancy | CI low |
|---|---|---|---|---|---|---|
| 5m | A | 30 | +29.73 | 1.65 | +0.99 | −0.80 |
| 5m | B | 29 | +28.95 | 1.63 | +1.00 | −0.83 |
| 5m | D / E | 21 | +6.76 | 1.17 | +0.32 | −1.67 |
| 60m | A | 10 | −9.10 | 0.65 | −0.91 | −3.59 |
| 60m | B | 10 | −6.40 | 0.75 | −0.64 | −3.59 |
| 60m | D / E | 3 | +3.83 | 1.49 | +1.28 | n/a (<5) |

**Research counterfactual ($50 cap, account limits off — not tradable):**

| data | variant | trades | net USD | PF | expectancy CI |
|---|---|---|---|---|---|
| 5m | A | 80 | +43.82 | 1.14 | −1.90 … +3.05 |
| 5m | B | 84 | +86.58 | 1.25 | −1.50 … +3.67 |
| 5m | D / E | 66 | +5.99 | 1.02 | −2.81 … +3.09 |
| 60m | A | 102 | −84.90 | 0.86 | −3.83 … +2.22 |
| 60m | B | 103 | −94.89 | 0.85 | −3.81 … +2.07 |
| 60m | D / E | 75 | −68.24 | 0.85 | −4.11 … +2.39 |

Every interval spans zero. No row is evidence of an edge.

**Walk-forward (1-hour, three chronological folds, fixed parameters):** pooled A 97 trades
−127.62; B 98 −137.61; D/E 72 −104.27. Fold 3 (2026) is positive for every variant while folds
1–2 are negative, i.e. the result is period-dependent and the sign flips across regimes.

**Sensitivity (5m, counterfactual):** spread 20/30/50 ticks → +16.12 / +5.99 / −2.05 with the same
66 trades; swing lookback 1/2/3 → −27.01 / +5.99 / −134.81 with 70/66/65 trades. A plausible
parameter change moves the result by more than any measured "edge".

Three findings are stable and worth stating plainly:

1. **The mandate is the binding constraint.** At 0.01 lot, a $5 cap leaves about $4.81 of stop
   distance after costs. Most intraday gold setups have wider structural stops, so they are
   rejected (`RiskCapExceeded`: 425–711 per dataset) rather than resized. Executable trade counts are
   3–30 over months, and the 60-minute cells with three trades cannot establish anything at all.
2. **Module differences are smaller than the noise.** A vs B vs D differ by amounts that the
   bootstrap intervals swallow. Order-flow confirmation removes trades and moves net P&L in both
   directions depending on the period. There is no consistent module win in this sample.
3. **The cost assumption is as large as the signal.** On the 5-minute set the same configuration
   moves from positive to negative when the modelled spread goes from 20 to 50 ticks. Since the
   spread was **assumed, not measured**, the sign of the 5-minute result is not a property of the
   strategy; it is a property of the assumption.

VIX deserves a specific note: in the 5-minute window VIX ranged 14.2–20.0, never reaching the 30
threshold, so variant E is **identical to D by construction** there. On the hourly data the veto
removed signals but no accepted trade was affected, again making E equal to D. A test in which the
filter never binds is not evidence about the filter.

GEX/delta/vanna: **untested**. Deriving dealer exposure from gold candles would be fabrication; no
legitimate historical series was available in this environment, so the variant is left empty.
The harness accepts a valid GEX series if one is supplied, and the C# engine consumes it.

## 5. Walk-forward and parameter sensitivity

The hourly data is split into three chronological folds; parameters are fixed and never fitted, so
the folds are OOS in the weak sense (no look-ahead, no selection). The folds disagree with each
other in sign, which is the honest summary of a strategy whose per-trade edge is small relative to
its variance.

The sensitivity grid is descriptive, not optimisation. Two observations:

- `MinScore` 3/4/5 produce identical results: every qualifying setup scores the same, so the
  threshold does not bind. The parameter is therefore not a tuning knob on this rule set.
- Changing `SwingLookback` from 2 to 3 changes net P&L by more than the measured "edge". When a
  plausible parameter shift dominates the result, the result is not robust.

## 6. What would make this evidence stronger

1. **Measured spreads** (bid/ask history from the broker) instead of an assumption.
2. **A longer 5-minute sample** — 60 days is one regime.
3. **A legitimate options-exposure series** with documented methodology, timestamps and expiry, so
   C can be tested rather than left empty.
4. **A forward test** at 0.01 lot on demo, 20+ trading days, using `docs/TRADE_REPORT_TEMPLATE.md`.
5. **More trades under the real mandate**, which requires either a broker with a smaller gold
   contract or acceptance that a $5 cap at 0.01 lot is very restrictive on this instrument.

## 7. Reproduction

```bash
python3 research/fetch_data.py --out research/data   # refresh free data (needs network)
python3 research/run_pipeline.py                     # writes research/out/*
python3 research/make_parity.py                      # regenerates tests/fixtures/parity.json
dotnet test tests/QuantLOL.Tests                     # fails if C# and Python disagree
```

CI re-runs `make_parity.py` and fails if the committed fixture changes, so the research engine and
the C# engine cannot silently drift apart.

## 8. Conclusion

The deliverable is a **measurement instrument** with hard risk controls, not a validated strategy.
On this data, under this mandate, the rule set has no demonstrated edge; the strongest statements
the data supports are about cost sensitivity, the restrictiveness of the $5 cap, and the absence of
any module that consistently helps. Anyone presenting these numbers as a reason to trade real money
would be overclaiming.
