# Building, installing and verifying QuantLOL

This document is deliberately concrete: every command here has been run, and every check has a
pass/fail answer. Where something can only be verified on your broker's demo account, it says so
instead of pretending otherwise.

---

## 1. What the code is

| Item | Value |
|---|---|
| Language / runtime | C# on .NET 6 (`net6.0`), cTrader Automate API |
| SDK package | `cTrader.Automate` **1.0.21** (official, free, no paid dependency) |
| Bot project | `src/QuantLOL/QuantLOL.csproj` |
| Entry type | `QuantLOL.QuantLOLBot : Robot` (one file is `[Robot]`; the rest is pure logic) |
| Tests | `tests/QuantLOL.Tests` (net8.0, xUnit) — 73 cases |
| CI | `.github/workflows/build.yml` — Release build **and** `dotnet test`, exit codes recorded |
| Output | `src/QuantLOL/bin/Release/net6.0/src.algo` |

There is **no** paid API, subscription, VPS, or proprietary library. GEX/VIX are optional files or
public endpoints; when they are missing the bot still runs (see `docs/PARAMETERS.md`, group
"9. External data").

---

## 2. Build

### 2.1 On GitHub (what CI does, verified)

```bash
dotnet restore src/QuantLOL/QuantLOL.csproj
dotnet build   src/QuantLOL/QuantLOL.csproj -c Release --no-restore
dotnet test    tests/QuantLOL.Tests/QuantLOL.Tests.csproj -c Release
```

The workflow writes both exit codes to `build/ci_report_<GITHUB_RUN_ID>.txt` and fails the job if
either is non-zero. The newest report is the only trustworthy source of compile/test status; the
legacy `build/ci_report.txt` is kept for history and may be stale. Check the report's `.sha` file
against the run's `head_sha` before believing a failure.

### 2.2 On your machine

Prerequisites: .NET SDK 6.0 or newer (8.x builds `net6.0` fine) and internet access for the one
NuGet package.

```bash
git clone https://github.com/GilangNAdha/QUANTLOL.git
cd QUANTLOL
dotnet build src/QuantLOL/QuantLOL.csproj -c Release
dotnet test  tests/QuantLOL.Tests/QuantLOL.Tests.csproj -c Release
```

The bot project copies the compiled assembly to `cAlgo/Sources/Robots/src.algo` as part of the
build. cTrader does not load a raw `.dll`; it loads a `.algo` (a zip produced by the compiler).

### 2.3 Inside cTrader

1. Open **Automate** (cTrader 4.x/5.x) → **New → Build** or use an existing cBot project.
2. Either
   - add the `src/QuantLOL/*.cs` files to a cBot project and press **Build**, or
   - copy the produced `src.algo` into the cAlgo sources directory (`Documents/cAlgo/Sources/Robots/`
     on Windows) and press **Build**.
3. In the Automate window press **Reload** until `QuantLOL` appears in the cBots list.
4. Do **not** rename the symbol or attach it to anything other than the intended instrument: the bot
   refuses to trade when `SymbolName` does not match `Expected symbol` (default `XAUUSD`).

If the cTrader compiler reports an error the CI did not, the local SDK version is the usual cause;
attach the compiler output rather than guessing.

---

## 3. First run: demo account, checks in order

Run these in order. Do not skip to step 4.

### 3.1 Start in monitor mode

Set **Dry run (no orders) = true** for the first session. The bot computes decisions, writes the
journal, and never submits. Confirm from the start-up banner that it sees what you expect:

```
mode=LIVE/DEMO symbol=XAUUSD timeframe=... entryTF=M5
account: balance=1000.00 ... leverage=100 type=Demo
spec[XAUUSD] lots=0.0100 units=... (lotSize=...) tick=... tickValue=... pip=...
volMin=... step=... moneyPerPrice=... comm=... (model) verified=True
volume resolution: 0.0100 lot = ... units (broker min ..., step ...)
money-per-price-unit=... (contract cross-check ...)
risk cap: 5.00 USD implies a maximum structural stop distance of ... price units (... ticks)
limits: daily loss 15.00, equity drawdown 30.00, max positions 1, spread<= 40 ticks
external: GEX:... VIX:...
```

`verified=True` is the gate. `verified=False` prints a precise reason and the bot goes
monitor-only; it will not submit orders. Typical reasons: `LotSize` is zero, tick value does not
match the contract cross-check, the symbol name does not match, or the volume step cannot express
0.01 lot.

### 3.2 Verify 0.01 lot against the broker's own numbers

The banner gives four numbers that must agree:

| Check | What it proves |
|---|---|
| `volume resolution: 0.0100 lot = N units` | the requested lot was converted with `Symbol.LotSize`, not a hard-coded 100 |
| `N >= volMin` and `N` is a multiple of `step` | the broker will accept the volume at all |
| `moneyPerPrice=...` vs `(contract cross-check ...)` | `Symbol.AmountRisked(1 unit, 1 price unit)` agrees with the lot math |
| `max structural stop distance = ... price units` | the $5 cap after commission and slippage |

Write those four numbers down. They are the evidence that a 0.01-lot trade risks what the mandate
says on **your** broker, not on a generic gold contract.

Then, with Dry run **off** on demo:

1. Wait for the first signal. Confirm the journal row: `volume_units` equals the banner's units,
   `risk_usd <= 5.00`, `stop` is on the structural side.
2. Confirm in the Positions panel that the position is 0.01 lot and that the protective stop exists
   immediately after the fill (`EnsureProtectiveStop` runs on every tick).
3. Confirm the account margin before and after: `GetEstimatedMargin` is logged in the banner and a
   margin failure is reported as `NoMoney`, not silently retried.

If the broker rejects the volume (`BadVolume`), the bot logs the error and does not resize. That is
the intended behaviour: a broker that cannot express 0.01 lot is a reason to stop, not to round up.

### 3.3 Verify the risk cap end to end

1. In the journal, find a rejected trade with `reject_reason = RiskCapExceeded`. Confirm the
   `intended_stop` was **not** moved closer: only the rejection is recorded.
2. Find an executed trade. Confirm `risk_usd = (entry - stop) * money_per_price + commission +
   slippage` is at or below 5.00 and that `r_multiple` is computed from **net** P&L.
3. Force a losing day on demo (or simulate one) until realised P&L ≤ −15.00. Confirm the next
   signal is rejected with `daily loss limit` and that trading resumes the next UTC day.
4. Drive equity 30.00 below the reference balance. Confirm the kill switch latches, that it is
   printed in the banner, and that it survives a restart (the bot reconciles it from history).

### 3.4 Performance / latency instrumentation

`PrintSummary()` prints the latency table (p50/p95/p99/max per stage) and the performance
markdown. The stages are:

`tick->signal_eval`, `signal->risk_approval`, `risk->order_submit`, `submit->broker_ack`,
`submit->confirmed_fill`, `decision->fill_total`, `slow_path_total`, `on_tick_total`,
`external_data_refresh`.

No latency number is claimed in this repository until it has been measured on a specific machine
and broker. Use `docs/BENCHMARK_TEMPLATE.md` to record it.

---

## 4. Acceptance criteria — current status

| # | Criterion | Status |
|---|---|---|
| 1 | Complete source + accurate compile status | **Met**: source in `src/QuantLOL`, Release build verified in CI (see `build/ci_report_<run>.txt`). |
| 2 | Demo verifies 0.01 lot | **Procedure defined, not yet executed**: steps 3.1–3.2. No demo result is claimed. |
| 3 | $5 risk verified against symbol specs | **Code + unit tests**; broker-specific confirmation is steps 3.2–3.3 and is **pending**. |
| 4 | Drawdown controls tested | **Unit tests** for daily loss, day equity DD, lifetime latch, reset. Demo confirmation pending. |
| 5 | Latency instrumentation measurable | **Met** (instrumented); values **not yet measured**. |
| 6 | Logs for every executed and rejected trade | **Met**: 35-column CSVs, buffered, flushed on stop. |
| 7 | Reports separate in-sample / OOS / forward | **Partially met**: backtest report separates chronological folds (`research/out/report.md`); forward section is empty by design until demo data exists. |
| 8 | External data timestamped / quality visible | **Met**: `as_of`, quality flags, freshness, provenance in the banner and journal. |
| 9 | No mandatory paid services | **Met**. |
| 10 | No unverified claims | Policy enforced in `docs/RESEARCH.md` and the generated report. |
| 11 | Deliverables (sources, docs, templates, troubleshooting) | **Met**: see the list in `README.md`. |

---

## 5. What this build is not

- It is not a proven profitable strategy. The backtest is small-sample and the intervals are wide.
- It is not a substitute for a demo/forward test. Costs, fills and latency in backtest are
  assumptions, not observations.
- It does not compute dealer gamma from gold candles. GEX is external input only, labelled by
  quality (manual / proxy / unavailable).
