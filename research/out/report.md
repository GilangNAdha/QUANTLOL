# QuantLOL XAUUSD research report

Generated: 2026-10-21T20:28:02.022338+00:00 (UTC)

> **Read this first.** Nothing in this report is a profitability claim. It is a measurement of a specific rule set on specific free data, with explicit costs and explicit sample sizes. Small samples cannot establish an edge, and a backtest is not a forward test.

## 1. Data

| dataset | rows | first | last | source |
|---|---|---|---|---|
| gc_1d | 2514 | 2016-10-21 04:00:00 | 2026-10-21 04:00:00 | Yahoo Finance chart API |
| gc_5m | 13612 | 2026-08-12 04:00:00 | 2026-10-21 19:54:13 | Yahoo Finance chart API |
| gc_60m | 13737 | 2024-05-30 04:00:00 | 2026-10-21 19:54:20 | Yahoo Finance chart API |
| vix_1d | 9298 | 1990-01-02 00:00:00 | 2026-10-20 00:00:00 | Cboe VIX_History.csv (public) |

Datasets used in this run:

| dataset | bars | start | end |
|---|---|---|---|
| gc_5m | 13612 | 2026-08-12T04:00 | 2026-10-21T19:54 |
| gc_60m | 13737 | 2024-05-30T04:00 | 2026-10-21T19:54 |

### Assumptions and their status

- **commission**: 0.07 USD per side (7 USD per lot round turn) - configurable, verify with your broker
- **fills**: decision on bar close -> fill at next bar open; stop assumed before target when both are touched
- **instrument**: COMEX GC=F continuous front month (Yahoo Finance, delayed) used as the XAUUSD research proxy
- **risk_cap**: 5 USD planned risk per trade at 0.01 lot; structural stops wider than ~4.81 USD are SKIPPED, never tightened
- **slippage**: 5 ticks (0.05 USD) adverse on entry and on stop exits; spreads paid in full
- **spread**: 30 ticks (0.30 USD) modelled constant; NOT measured historical spread. Sensitivity at 20/50 ticks reported.
- **volume**: 0.01 lot = 1 troy ounce (100 oz/lot contract); money per 1.0 price unit = 1.0 USD

Limitations that follow directly from the data:

- GC=F is COMEX **futures**, not spot XAUUSD: level differences and basis noise exist.

- Yahoo intraday history is limited (≈60 days of 5-minute bars, ≈2 years of 1-hour bars).

- **No historical spread is available.** A constant 30-tick spread is modelled; this is an assumption, not a measurement. Section 6 shows how much the result depends on it.

- No tick-level latency can be derived from bar data, so **no historical latency numbers are claimed** here.

## 2. Executable configuration (0.01 lot, 5 USD risk cap, account limits enforced)

These runs use the same risk rules as the cBot: 5 USD planned risk, 15 USD daily loss, 30 USD equity drawdown kill switch. When a limit trips, sampling stops, exactly as the bot would stop trading.

| dataset | variant | trades | setups | net USD | PF | expectancy | CI low | max DD | risk-cap rejections | stopped by limit |
|---|---|---|---|---|---|---|---|---|---|---|
| gc_5m | A_smc | 30 | 177 | 29.73 | 1.65 | 0.991 | -0.795 | 15.41 | 516 | no |
| gc_5m | B_smc_amt | 29 | 169 | 28.95 | 1.63 | 0.998 | -0.830 | 11.10 | 536 | no |
| gc_5m | C_smc_amt_gex | untested | - | - | - | - | - | - | - |
| gc_5m | D_smc_amt_of | 21 | 118 | 6.76 | 1.17 | 0.322 | -1.666 | 12.16 | 425 | no |
| gc_5m | E_complete | 21 | 118 | 6.76 | 1.17 | 0.322 | -1.666 | 12.16 | 425 | no |
| gc_60m | A_smc | 10 | 44 | -9.10 | 0.65 | -0.910 | -3.590 | 16.95 | 688 | no |
| gc_60m | B_smc_amt | 10 | 45 | -6.40 | 0.75 | -0.640 | -3.590 | 16.95 | 705 | no |
| gc_60m | C_smc_amt_gex | untested | - | - | - | - | - | - | - |
| gc_60m | D_smc_amt_of | 3 | 21 | 3.83 | 1.49 | 1.277 | n/a | 4.99 | 482 | no |
| gc_60m | E_complete | 3 | 21 | 3.83 | 1.49 | 1.277 | n/a | 4.99 | 481 | no |

The risk-cap column is the central finding of this section: with a 0.01-lot (1 oz) position, a 5 USD cap leaves ~4.81 USD for the structural stop after costs. Most intraday gold setups are wider than that, so they are **skipped, not resized**. This is the mandate doing its job, and it is why the trade counts are small. Expectancy intervals that straddle zero mean the sample cannot distinguish this rule set from noise.

## 3. Research counterfactual (cap relaxed to 50 USD, account limits off)

> These runs exist **only** to obtain a sample large enough to compare modules. They are **not** a tradable configuration under the stated mandate: the extra risk is real, and disabling the daily/kill limits would be unacceptable in production. The cBot never runs this way.

| dataset | variant | trades | net | PF | expectancy | CI low | CI high | win % | avg R | max DD |
|---|---|---|---|---|---|---|---|---|---|---|
| gc_5m | A_smc | 80 | 43.82 | 1.14 | 0.548 | -1.896 | 3.051 | 31.25 | -0.042 | 103.05 |
| gc_5m | B_smc_amt | 84 | 86.58 | 1.25 | 1.031 | -1.504 | 3.669 | 33.33 | -0.011 | 100.77 |
| gc_5m | D_smc_amt_of | 66 | 5.99 | 1.02 | 0.091 | -2.806 | 3.089 | 30.30 | -0.146 | 121.36 |
| gc_5m | E_complete | 66 | 5.99 | 1.02 | 0.091 | -2.806 | 3.089 | 30.30 | -0.146 | 121.36 |
| gc_60m | A_smc | 102 | -84.90 | 0.86 | -0.832 | -3.832 | 2.220 | 39.22 | -0.087 | 242.29 |
| gc_60m | B_smc_amt | 103 | -94.89 | 0.85 | -0.921 | -3.807 | 2.072 | 39.81 | -0.091 | 257.09 |
| gc_60m | D_smc_amt_of | 75 | -68.24 | 0.85 | -0.910 | -4.107 | 2.386 | 41.33 | -0.089 | 206.60 |
| gc_60m | E_complete | 75 | -68.24 | 0.85 | -0.910 | -4.107 | 2.386 | 41.33 | -0.089 | 206.60 |

Read module contributions across a row group (same dataset), never across datasets. A difference smaller than the bootstrap interval is noise.

### Reading the module deltas

- **AMT (B vs A)**: AMT contributes in two ways: a score bonus when a long sits at/below VAL (or a short at/above VAH), and VAH/VAL/session levels added to the target set. It is not a veto, so it changes *which* target is chosen more often than whether a trade happens.

- **Order flow (D vs B)**: the confirmation is a broker-tick-volume delta proxy over a short window. It removes signals; it does not add them. Its effect flips sign between the two datasets in this run (it helps on the hourly sample and hurts on the 5-minute sample), which is what a noisy filter looks like. It is reported as mixed, not as a win.

- **VIX (E vs D)**: VIX only removes signals when it is at or above the configured 30 threshold. In quiet samples E and D are identical by construction, which is visible in the rejection table rather than being papered over.

- **GEX (C)**: **untested.** No legitimate historical dealer-gamma series exists in this repository, and the bot refuses to derive one from gold candles. A row is left empty instead of inventing one.

## 4. Walk-forward (3 chronological folds, fixed parameters, limits off)

No parameter is fitted on any fold. The folds exist to show regime dependence and stability, not to select a winner.

| variant | pooled trades | pooled net | pooled expectancy | CI low | CI high |
|---|---|---|---|---|---|
| A_smc | 97 | -127.62 | -1.316 | -4.274 | 1.788 |
| B_smc_amt | 98 | -137.61 | -1.404 | -4.364 | 1.625 |
| D_smc_amt_of | 72 | -104.27 | -1.448 | -4.325 | 1.572 |
| E_complete | 72 | -104.27 | -1.448 | -4.325 | 1.572 |

**A_smc**

| fold | period | trades | net | PF | expectancy |
|---|---|---|---|---|---|
| 1 | 2024-05-30 -> 2025-03-19 | 43 | -28.80 | 0.83 | -0.670 |
| 2 | 2025-03-19 -> 2026-01-05 | 33 | -144.89 | 0.45 | -4.391 |
| 3 | 2026-01-05 -> 2026-10-21 | 21 | 46.07 | 1.28 | 2.194 |

**B_smc_amt**

| fold | period | trades | net | PF | expectancy |
|---|---|---|---|---|---|
| 1 | 2024-05-30 -> 2025-03-19 | 44 | -23.99 | 0.86 | -0.545 |
| 2 | 2025-03-19 -> 2026-01-05 | 33 | -159.69 | 0.43 | -4.839 |
| 3 | 2026-01-05 -> 2026-10-21 | 21 | 46.07 | 1.28 | 2.194 |

**D_smc_amt_of**

| fold | period | trades | net | PF | expectancy |
|---|---|---|---|---|---|
| 1 | 2024-05-30 -> 2025-03-19 | 27 | -12.06 | 0.87 | -0.447 |
| 2 | 2025-03-19 -> 2026-01-05 | 29 | -127.73 | 0.45 | -4.405 |
| 3 | 2026-01-05 -> 2026-10-21 | 16 | 35.53 | 1.36 | 2.220 |

**E_complete**

| fold | period | trades | net | PF | expectancy |
|---|---|---|---|---|---|
| 1 | 2024-05-30 -> 2025-03-19 | 27 | -12.06 | 0.87 | -0.447 |
| 2 | 2025-03-19 -> 2026-01-05 | 29 | -127.73 | 0.45 | -4.405 |
| 3 | 2026-01-05 -> 2026-10-21 | 16 | 35.53 | 1.36 | 2.220 |

## 5. Cost assumption sensitivity

The spread is the least defensible number in this study because no historical bid/ask was available. The table shows how the result moves when it is wrong in either direction.

| spread (ticks) | trades | net | PF | expectancy |
|---|---|---|---|---|
| 20 | 66 | 16.12 | 1.05 | 0.244 |
| 30 | 66 | 5.99 | 1.02 | 0.091 |
| 50 | 66 | -2.05 | 0.99 | -0.031 |

## 6. Parameter sensitivity (5-minute, counterfactual, limits off)

This grid is **descriptive, not an optimisation**: no parameter set is selected from it, and no result here is carried back into the bot. A surface that swings from profit to loss across neighbouring cells is a warning, not an opportunity.

| swing lookback | sweep window | min score | trades | net | PF | expectancy |
|---|---|---|---|---|---|---|
| 1 | 12 | 4.0 | 70 | -27.01 | 0.91 | -0.386 |
| 2 | 12 | 4.0 | 66 | 5.99 | 1.02 | 0.091 |
| 3 | 12 | 4.0 | 65 | -134.81 | 0.64 | -2.074 |

## 7. Rejection diagnostics

| rejection reason | count |
|---|---|
| NoSetup | 196149 |
| RiskCapExceeded | 4451 |
| EntryDrift | 3484 |
| VixFilter | 690 |
| RewardTooLow | 531 |
| RiskCapAtFill | 74 |

`NoSetup` is the expected base rate: most bars contain no qualifying sweep plus confirmation. `RiskCapExceeded` is the mandate. `RewardTooLow` means structure did not offer 1.5R. `EntryDrift` is the next-open revalidation. The per-event log with timestamps is in `rejections.csv`.

## 8. What is measured, what is not

| Claim | Status in this report |
|---|---|
| Strategy profitability | **Not claimed.** Backtest only, small sample, intervals span zero. |
| Out-of-sample behaviour | Chronological folds with fixed parameters; nothing was fitted. |
| Realised latency | **Not measured here.** Timings come from the cBot's own instrumentation during forward testing. |
| Historical spread | **Assumed constant 30 ticks**, not measured; see section 5. |
| GEX / Delta / Vanna contribution | **Untested.** No legitimate historical options-exposure series was available. |
| Forward-test results | **Empty by design** until the bot has run on a demo account; see docs/FORWARD_TEST.md. |
| Commission | Broker-dependent assumption (0.07 USD/side). Verify against your account. |

## 9. Files

- `research/out/metrics.json` - every number in this report, machine-readable
- `research/out/trades.csv` - all simulated trades, tagged by run and configuration
- `research/out/rejections.csv` - rejected setups with timestamp, reason and price
- `research/run_pipeline.py`, `research/backtest.py`, `research/quantlol_ref.py` - the harness

Reproduce with `python3 research/run_pipeline.py`. The harness is deterministic; there is no random seed except in the bootstrap (fixed at 42).