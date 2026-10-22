# What the signal application covers

Your brief lists 16 subsystems. This table says, for each one, what the **standalone signal app**
does today, what it does **not** do, and why. Nothing here is aspirational: if a row says
"not implemented", the code does not contain it.

Legend: ✅ implemented and tested · ◑ partial (stated below) · ✗ not implemented (reason given)

| # | Requirement | Status | Where / why |
|---|---|---|---|
| 1 | SMC: swings, sweeps, BOS, CHoCH, FVG, order blocks, deterministic, no repaint | ✅ | Shared engine `research/quantlol_ref.py` (fractal swings confirmed *k* bars later, BOS/CHoCH from closed bars only, sweeps need pierce + reclaim, FVG/OB from the same bars). Parity-pinned to the C# core. |
| 2 | AMT: POC/VAH/VAL, prior + developing session, initial balance, balance vs discovery | ◑ | Session profile (POC/VAH/VAL, session high/low, initial balance) is built per bar and used for scoring and targets. A **regime label** (balance vs discovery) is shown in the cBot journal; the signal app reports session and profile but does not yet expose a separate balance/discovery regime field. |
| 3 | GEX: gamma flip, walls, pos/neg regime, changes | ◑ | Levels (flip/walls) are loaded from a **file or URL you supply**, validated (instrument, price scale, timestamp, expiry) and used as confluence + targets. `net_gex` sign is carried for regime labelling. It is **never derived from gold candles**. No automatic delta-of-exposure ("changes") tracking between snapshots. |
| 4 | Delta / Vanna | ◑ | `delta_exposure` and `vanna` are parsed, stored and displayed with their units note. They **do not vote** on a signal, because no legitimate source was available to validate a sign convention. The app says "unavailable" rather than scoring them. |
| 5 | Order flow from a legitimate source | ◑ | CLV/close-location delta proxy from **broker tick volume** (for GC=F, exchange volume). Explicitly labelled a proxy, never "centralised volume". |
| 6 | Order-book depth / footprint / real delta | ✗ | Requires L2/DOM data, which needs a paid feed. Out of scope by your "no mandatory paid API" rule. |
| 7 | VIX as broad context only | ✅ | Daily Cboe file → JSON (`tools/vix_to_bot_json.py`). Used as a veto at/above a threshold; freshness enforced; stale VIX is reported and does not veto. |
| 8 | Signal engine: deterministic, bullish/bearish/neutral/unavailable, explicit rejection reasons | ✅ | Same function the cBot calls. Every non-signal carries a machine-readable reason (`NoSetup`, `RiskCapExceeded`, `VixFilter`, `SpreadTooWide`, `Conflict`, `DataStale`, …) and the dashboard shows the breakdown. |
| 9 | Risk: fixed 0.01 lot, $5 cap, daily $15, $30 DD, one position | ◑ | The **$5 per-trade cap is enforced** on every candidate signal (a wider structural stop is rejected, never tightened). Daily/equity limits are **not** evaluated by the app because it has no account — it is advisory and stateless about fills. The cBot and the backtest enforce them. |
| 10 | Per-trade plan: structural SL, targets from structure/GEX/AMT, R:R | ✅ | Every actionable record carries entry/stop/target, risk USD, net R:R, tick distances, and whether the target is structural or a flagged synthetic fallback. |
| 11 | Instrumentation of every signal **and** every rejection | ✅ | `signals/out/signals.jsonl` + `signals.csv` (one row per emitted signal) and `evaluations.jsonl` (one row per evaluated bar, including rejections). Live counters in `/api/state`. |
| 12 | Latency measurement | ◑ | Engine time per evaluation is measured (`engine_ms`). Because there is **no order path**, submit/ack/fill stages do not exist here — those belong to the cBot and are measured there. No latency number is claimed for a broker round trip. |
| 13 | Dashboard: net P&L, PF, expectancy, win rate, DD, Sharpe/Sortino, MAE/MFE, latency percentiles, breakdowns | ✗ (partly) | The app shows the **decision** view (current read, plan, rejection mix, data quality, context). P&L/performance analytics require executed trades — that is the cBot journal + `docs/TRADE_REPORT_TEMPLATE.md`. Inventing performance for signals that were never traded would be fabrication. |
| 14 | Reproducible evaluation A–E, walk-forward, sensitivity, Monte Carlo | ✅ (in research) | `research/run_pipeline.py` does exactly this and `research/out/report.md` holds the results, including "untested" rows for GEX and an explicit statement that costs are assumed. |
| 15 | Realistic costs and latency assumptions | ◑ | Commission, slippage and spread are explicit, configurable **assumptions** used in the risk check and labelled as such everywhere. No historical bid/ask was available (see the report's cost-sensitivity table). |
| 16 | Modularity, validation, degraded operation, no credentials | ✅ | `signals/{config,datafeed,engine,service,store,server,cli}.py`; config validated at start-up; every loader returns an explicit INVALID/STALE state instead of throwing; no secrets anywhere in the tree. |

## Deliberate omissions and why

- **No orders, no broker class, no order ids.** The app cannot place, modify or close anything, and
  the test suite asserts that no such function or import exists. If you want execution you run the
  cBot, which shares the same rules.
- **No account-level drawdown state.** Without fills there is no realised equity. The app shows the
  *per-trade* cap only; the $15/$30 rules live in the bot and the backtest, where they are tested.
- **No position sizing.** Volume is fixed by configuration (0.01 lot). The app cannot resize.
- **No L2/order-book data, no footprint, no real delta.** Paid feeds are excluded by the brief.

## If you need the missing pieces

| Missing | Fastest legitimate route |
|---|---|
| Real delta / footprint | Broker DOM + a data vendor with tick-by-tick aggressor flags (paid). Until then, the CLV proxy is labelled. |
| GEX changes over time | Keep snapshots (one JSON per expiry/session) and diff them; the loader already validates each file. |
| Performance analytics for signals | Log the signals, then `python3 research/backtest.py`-style evaluation, or run the cBot on demo and use its journal. |
| Account-level risk in the app | Run the cBot, or feed fills into `quantlol_ref.DrawdownGuard` (already implemented and tested). |
