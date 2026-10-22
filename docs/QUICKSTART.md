# How to run quantlol

A step-by-step runbook. Follow it in order; do not skip step 4.

---

## 1. Get the code and build it

```bash
git clone https://github.com/GilangNAdha/QUANTLOL.git
cd QUANTLOL
dotnet build src/QuantLOL/QuantLOL.csproj -c Release
dotnet test  tests/QuantLOL.Tests/QuantLOL.Tests.csproj
```

What you get:

- `src/QuantLOL/bin/Release/net6.0/src.algo` — the file cTrader loads
- `dotnet test` → 73 passing tests (if this fails, do not run the bot)

No .NET SDK? Install .NET 6 or 8 (`dotnet-sdk-8.0`) from Microsoft; the one NuGet package
(`cTrader.Automate`) is free.

## 2. Install in cTrader

1. Open cTrader → **Automate** (left sidebar) → **cBots**.
2. **Add → Build from source** is not needed if you have the `.algo`: copy
   `src.algo` into `Documents/cAlgo/Sources/Robots/` (Windows) or the equivalent
   cAlgo sources folder, then press **Build** in the Automate toolbar.
   Alternatively paste the `.cs` files of `src/QuantLOL` into a new cBot project and build there.
3. Press **Reload** until `QuantLOL` appears in the cBots list.
4. Attach it to an **XAUUSD** chart (any timeframe; the bot loads its own M1/M5/M15/D1 series).

## 3. Configure the parameters

All 88 parameters are documented in `docs/PARAMETERS.md`, grouped exactly as cTrader shows them.
The defaults **are** the mandate — no edits needed for a first run:

| Group | Parameter | Default | Leave it? |
|---|---|---|---|
| 1. Identity | `ExpectedSymbolName` | `XAUUSD` | must match your chart |
| 1. Identity | `DryRun` | `false` | **set `true` for the first session** |
| 2. Risk | `Lots` | `0.01` | yes — fixed, never resized |
| 2. Risk | `MaxRiskPerTradeUsd` | `5.0` | yes |
| 2. Risk | `DailyLossLimitUsd` | `15.0` | yes |
| 2. Risk | `EquityDrawdownLimitUsd` | `30.0` | yes |
| 2. Risk | `MaxOpenPositions` | `1` | yes |
| 2. Risk | `MinRewardRisk` | `1.5` | yes (net of costs) |
| 2. Risk | `RejectIfSpecUnverified` | `true` | yes |
| 9. External data | `EnableGex` / `EnableVix` | `false` / `false` | leave off until step 6 |
| 10. Diagnostics | `JournalDirectory` | `.` | set to a writable folder |

Leave GEX and VIX **off** for the first run. They are optional context; the bot must be shown to
work without them.

## 4. First session: dry run

1. Set **Dry run (no orders) = true**.
2. Start the bot. Read the banner — it is the whole diagnostic:

```
spec[XAUUSD] lots=0.0100 units=... (lotSize=...) tick=... tickValue=... pip=...
volMin=... step=... moneyPerPrice=... comm=... (model) verified=True
volume resolution: 0.0100 lot = ... units (broker min ..., step ...)
money-per-price-unit=... (contract cross-check ...)
risk cap: 5.00 USD implies a maximum structural stop distance of ... price units (... ticks)
limits: daily loss 15.00, equity drawdown 30.00, max positions 1, spread<= 40 ticks
external: GEX:disabled VIX:disabled
```

3. **`verified=True`** or stop here. `verified=False` prints the reason (wrong symbol, zero LotSize,
   tick-value mismatch, volume step cannot express 0.01 lot, minimum stop distance too wide). The bot
   then runs monitor-only and will not submit orders — that is the intended fail-closed behaviour.
4. Write down the four numbers (units, money-per-price, max stop distance in ticks, commission model).
   They are your proof that 0.01 lot on your broker risks what the mandate says.
5. Let it run for a few days and read the journal (`quantlol_trades.csv`, `quantlol_rejections.csv`).

## 5. Second session: demo with real orders

1. Set **Dry run = false**. Keep everything else at default.
2. When a trade opens, verify in the Positions panel:
   - volume is 0.01 lot,
   - a protective stop exists immediately,
   - the journal row shows `risk_usd <= 5.00` and `volume_units` equal to the banner value.
3. Check the rejection log. On gold, expect `RiskCapExceeded` to dominate — most structural stops are
   wider than ~4.81 USD. That is the mandate working, not a bug.
4. Follow `docs/FORWARD_TEST.md` for the 20-trading-day protocol, weekly checkpoints and the restart
   (reconciliation) test.

## 6. Optional: add GEX and/or VIX later

Only after phases A and B above. Both are **context**, not fast signals.

**VIX from the public Cboe daily file** (free, no key):

```bash
# converts the committed VIX history into the JSON the bot reads
python3 tools/vix_to_bot_json.py --out /path/to/cbot/vix.json
```

Then set `EnableVix = true`, `VixFilePath = /path/to/cbot/vix.json`, `VixMaxAgeMinutes = 1440`.
Schema: `{"vix": 17.31, "as_of": "2026-10-20T20:15:00Z", "quality": "delayed"}` — see
`research/samples/vix.json`. It is a **daily close**; the bot labels it delayed and only uses it as a
broad filter (block new entries when VIX ≥ threshold).

**GEX is manual or adapter-fed, never derived from gold candles.** Copy
`research/samples/gex_example.json`, replace the numbers with a real, documented exposure snapshot
(strikes, expiry, methodology), keep the price levels on the **USD/oz** scale, then set
`EnableGex = true` and `GexFilePath`. The file must contain `instrument`, `as_of`, and at least one
price level; GLD/IAU files are accepted only after conversion and are tagged `Proxy`.

If the file is stale and `StalePolicy = BlockEntries`, new entries are blocked — that is deliberate.

## 7. Reading the output

| File / output | What it is |
|---|---|
| `quantlol_trades.csv` | one row per executed trade, 35 columns (fills, costs, MAE/MFE, latencies, data freshness) |
| `quantlol_rejections.csv` | one row per rejected setup with the exact reason |
| log at stop | latency p50/p95/p99 table + performance markdown (net, PF, expectancy, CI, drawdown) |
| `docs/TRADE_REPORT_TEMPLATE.md` | fill this from the CSVs, not from memory |

## 8. What you must not do

- Do not raise the risk cap, resize lots, or disable the drawdown limits to get more trades.
- Do not tighten a stop to make a setup fit the cap — the bot refuses by design; do not "fix" it.
- Do not run it live before a demo phase. There is no forward-test evidence yet.
- Do not treat the backtest in `research/out/report.md` as a profit forecast: every interval there
  includes zero.

## 9. If something looks wrong

`docs/TROUBLESHOOTING.md` maps every rejection reason, broker error and external-data state to an
action. The two most common: `RiskCapExceeded` (correct behaviour on gold) and `verified=False`
(read the printed reason, fix the cause, never bypass it).
