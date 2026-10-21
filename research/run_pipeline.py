#!/usr/bin/env python3
"""
QuantLOL research pipeline: variants, walk-forward folds, sensitivity, reports.

Usage:
    python3 research/run_pipeline.py [--data research/data] [--out research/out] [--quick]

Everything is stdlib-only and deterministic. Outputs:
    out/report.md        full human-readable study
    out/metrics.json     machine-readable results
    out/trades.csv       every simulated trade (all runs)
    out/rejections.csv   every rejected setup with its reason
"""
from __future__ import annotations
import argparse, csv, json, os, sys, time
from dataclasses import replace
from datetime import datetime, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from quantlol_ref import EngineConfig, StructureOptions, SessionSettings, SignalWeights
from backtest import load_bars, load_vix, run_backtest, compute_metrics, bootstrap_ci

VARIANTS = [
    ("A_smc", "SMC only (sweep + confirmation + dealing-range location)"),
    ("B_smc_amt", "SMC + AMT (session value-area location required)"),
    ("C_smc_amt_gex", "SMC + AMT + GEX levels (needs historical GEX; unavailable -> untested)"),
    ("D_smc_amt_of", "SMC + AMT + order-flow proxy confirmation"),
    ("E_complete", "SMC + AMT + order-flow + VIX (Delta/Vanna unavailable)"),
]


def base_config(max_risk, enforce_limits=True):
    return EngineConfig(
        tick=0.01, money_per_price=1.0, commission_rt=0.14, slippage_ticks=5,
        spread_ticks=30, max_spread_ticks=40, stop_buffer_ticks=5, min_score=4.0,
        require_location=True, require_confirmation=True, require_order_flow=True,
        enable_fvg=True, enable_order_block=True, enable_amt=True, enable_gex=False,
        enable_vix=True, block_on_high_vix=True, vix_high=30.0, fallback_rr=2.0,
        allow_synthetic_target=True, sweep_window=12, confirm_window=12,
        location_proximity_ticks=60,
        weights=SignalWeights(), structure=StructureOptions(), sessions=SessionSettings(),
        cooldown_trade_min=5, cooldown_loss_min=15, break_even_r=1.0, trail_start_r=1.5,
        trail_atr_mult=1.0, time_stop_min=240, max_entry_drift_ticks=8,
        daily_loss=15.0, equity_dd=30.0, max_risk=max_risk, lots=0.01, units=1.0,
        enforce_account_limits=enforce_limits)


def variant_config(name, max_risk, enforce_limits=True):
    cfg = base_config(max_risk, enforce_limits)
    if name == "A_smc":
        cfg.enable_amt = False
        cfg.require_order_flow = False; cfg.enable_vix = False
    elif name == "B_smc_amt":
        cfg.enable_amt = True
        cfg.require_order_flow = False; cfg.enable_vix = False
    elif name == "C_smc_amt_gex":
        cfg.enable_amt = True
        cfg.require_order_flow = False; cfg.enable_vix = False; cfg.enable_gex = True
    elif name == "D_smc_amt_of":
        cfg.enable_amt = True
        cfg.require_order_flow = True; cfg.enable_vix = False
    elif name == "E_complete":
        cfg.enable_amt = True
        cfg.require_order_flow = True; cfg.enable_vix = True
    return cfg


def summarize(run, label, notes=""):
    m = compute_metrics(run)
    m["label"] = label
    m["notes"] = notes
    pnls = [t.net_pnl for t in run["trades"]]
    lo, hi = bootstrap_ci(pnls)
    m["expectancy_ci"] = [lo, hi]
    m["bars"] = run["bars"]
    m["ended_by_kill"] = run["ended_by_kill"]
    m["period"] = [run["start"], run["end"]]
    return m


def run_one(bars, cfg, vix, label, notes=""):
    t0 = time.time()
    run = run_backtest(bars, cfg, vix, label=label)
    return summarize(run, label, notes), run, time.time() - t0


def write_trades_csv(path, tag, trades):
    new = not os.path.exists(path)
    with open(path, "a", newline="") as f:
        w = csv.writer(f)
        if new:
            w.writerow(["run", "id", "direction", "setup", "session", "regime", "signal_time", "entry_time", "exit_time",
                        "entry", "stop", "target", "exit_price", "exit_reason", "gross_pnl", "commission", "net_pnl",
                        "risk_usd", "r_multiple", "mae_usd", "mfe_usd", "bars_held", "score", "spread_ticks",
                        "slippage_usd", "synthetic_target", "evidence"])
        for t in trades:
            w.writerow([tag, t.id, t.direction, t.setup, t.session, t.regime, t.signal_time, t.entry_time, t.exit_time,
                        t.entry, t.stop, t.target, t.exit_price, t.exit_reason, t.gross_pnl, t.commission, t.net_pnl,
                        t.risk_usd, t.r_multiple, t.mae_usd, t.mfe_usd, t.bars_held, t.score, t.spread_ticks,
                        t.slippage_usd, t.synthetic_target, t.evidence])


def write_rejects_csv(path, tag, rejects):
    new = not os.path.exists(path)
    with open(path, "a", newline="") as f:
        w = csv.writer(f)
        if new:
            w.writerow(["run", "time", "reason", "detail", "price", "session"])
        for r in rejects:
            w.writerow([tag, r.time, r.reason, r.detail, r.price, r.session])


def md_table(headers, rows):
    out = ["| " + " | ".join(headers) + " |", "|" + "|".join(["---"] * len(headers)) + "|"]
    for r in rows:
        out.append("| " + " | ".join("" if v is None else str(v) for v in r) + " |")
    return "\n".join(out)


def fmt(v, nd=2):
    if v is None: return "n/a"
    if isinstance(v, float): return ("%." + str(nd) + "f") % v
    return str(v)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default="research/data")
    ap.add_argument("--out", default="research/out")
    ap.add_argument("--quick", action="store_true")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)
    for f in ("trades.csv", "rejections.csv"):
        p = os.path.join(args.out, f)
        if os.path.exists(p): os.remove(p)

    datasets = {}
    for name, f in (("gc_5m", "gc_5m.csv.gz"), ("gc_60m", "gc_60m.csv.gz")):
        p = os.path.join(args.data, f)
        if os.path.exists(p):
            bars = load_bars(p)
            datasets[name] = bars
            print("[data] %s: %d bars %s -> %s" % (name, len(bars), bars[0].t, bars[-1].t))

    vix = {}
    vp = os.path.join(args.data, "vix_1d.csv.gz")
    if os.path.exists(vp):
        vix = load_vix(vp)
        print("[data] vix: %d daily observations" % len(vix))

    results = {"generated_utc": datetime.now(timezone.utc).isoformat(), "datasets": {}, "variants": {},
               "walk_forward": {}, "sensitivity": [], "assumptions": {}}

    manifest_path = os.path.join(args.data, "manifest.json")
    if os.path.exists(manifest_path):
        with open(manifest_path) as f: results["data_manifest"] = json.load(f)

    results["assumptions"] = {
        "instrument": "COMEX GC=F continuous front month (Yahoo Finance, delayed) used as the XAUUSD research proxy",
        "volume": "0.01 lot = 1 troy ounce (100 oz/lot contract); money per 1.0 price unit = 1.0 USD",
        "commission": "0.07 USD per side (7 USD per lot round turn) - configurable, verify with your broker",
        "spread": "30 ticks (0.30 USD) modelled constant; NOT measured historical spread. Sensitivity at 20/50 ticks reported.",
        "slippage": "5 ticks (0.05 USD) adverse on entry and on stop exits; spreads paid in full",
        "fills": "decision on bar close -> fill at next bar open; stop assumed before target when both are touched",
        "risk_cap": "5 USD planned risk per trade at 0.01 lot; structural stops wider than ~4.81 USD are SKIPPED, never tightened",
    }

    for ds_name, bars in datasets.items():
        results["datasets"][ds_name] = {"bars": len(bars), "start": bars[0].t.isoformat(), "end": bars[-1].t.isoformat()}
        # ---- executable configuration -------------------------------------------------
        for name, desc in VARIANTS:
            if name == "C_smc_amt_gex":
                results["variants"]["%s|%s|exec" % (ds_name, name)] = {
                    "label": "%s|%s|exec" % (ds_name, name), "notes": desc, "trades": None, "net": None,
                    "pf": None, "expectancy": None, "expectancy_ci": [None, None], "max_dd": None,
                    "win_rate": None, "avg_r": None, "setups": None, "rejected": {},
                    "status": "UNTESTED - no legitimate historical GEX series; the harness accepts a gex CSV but none exists"}
                print("[exec] %-8s %-14s UNTESTED (no historical GEX)" % (ds_name, name))
                continue
            cfg = variant_config(name, max_risk=5.0, enforce_limits=True)
            m, run, secs = run_one(bars, cfg, vix, "%s|%s|exec" % (ds_name, name), desc)
            m["runtime_s"] = round(secs, 1)
            results["variants"]["%s|%s|exec" % (ds_name, name)] = m
            write_trades_csv(os.path.join(args.out, "trades.csv"), "%s|%s|exec" % (ds_name, name), run["trades"])
            write_rejects_csv(os.path.join(args.out, "rejections.csv"), "%s|%s|exec" % (ds_name, name), run["rejects"])
            print("[exec] %-8s %-14s trades=%3d net=%8s setups=%d" % (ds_name, name, m["trades"], fmt(m["net"]), m["setups"]))

        # ---- research counterfactual (risk cap relaxed; NOT executable at 0.01 lot) ----
        for name, desc in VARIANTS:
            if name == "C_smc_amt_gex": continue
            cfg = variant_config(name, max_risk=50.0, enforce_limits=False)
            m, run, secs = run_one(bars, cfg, vix, "%s|%s|counterfactual" % (ds_name, name), desc)
            m["runtime_s"] = round(secs, 1)
            m["notes"] = "COUNTERFACTUAL: risk cap relaxed to 50 USD to obtain a usable sample; NOT executable at 0.01 lot within the 5 USD mandate"
            results["variants"]["%s|%s|counterfactual" % (ds_name, name)] = m
            write_trades_csv(os.path.join(args.out, "trades.csv"), "%s|%s|counterfactual" % (ds_name, name), run["trades"])
            print("[ctf]  %-8s %-14s trades=%3d net=%8s PF=%s" % (ds_name, name, m["trades"], fmt(m["net"]), fmt(m["pf"])))

    # ---- walk-forward (3 chronological folds, fixed parameters) -----------------------
    if "gc_60m" in datasets:
        bars = datasets["gc_60m"]
        for name in ("A_smc", "B_smc_amt", "D_smc_amt_of", "E_complete"):
            cfg = variant_config(name, max_risk=50.0, enforce_limits=False)
            folds = []
            n = len(bars)
            third = n // 3
            pooled = []
            for k in range(3):
                seg = bars[k * third: (k + 1) * third + 1] if k < 2 else bars[2 * third:]
                if len(seg) < 500: continue
                run = run_backtest(seg, cfg, vix, label="%s|fold%d" % (name, k))
                m = compute_metrics(run)
                folds.append({"fold": k + 1, "period": [seg[0].t.isoformat(), seg[-1].t.isoformat()],
                              "trades": m["trades"], "net": m["net"], "pf": m["pf"], "expectancy": m["expectancy"]})
                pooled += run["trades"]
            lo, hi = bootstrap_ci([t.net_pnl for t in pooled])
            results["walk_forward"][name] = {"folds": folds, "pooled_trades": len(pooled),
                                             "pooled_net": round(sum(t.net_pnl for t in pooled), 4),
                                             "pooled_expectancy": round(sum(t.net_pnl for t in pooled) / len(pooled), 4) if pooled else None,
                                             "expectancy_ci": [lo, hi]}

    # ---- parameter sensitivity (5m, counterfactual) -----------------------------------
    if "gc_5m" in datasets and not args.quick:
        bars = datasets["gc_5m"]
        # spread assumption sensitivity: the 30-tick spread is an assumption, so show its weight
        for spread in (20, 30, 50):
            cfg = variant_config("E_complete", max_risk=50.0, enforce_limits=False)
            cfg.spread_ticks = spread
            cfg.max_spread_ticks = 80
            run = run_backtest(bars, cfg, vix, label="spread")
            m = compute_metrics(run)
            results.setdefault("spread_sensitivity", []).append(
                {"spread_ticks": spread, "trades": m["trades"], "net": m["net"], "pf": m["pf"],
                 "expectancy": m["expectancy"]})
            print("[spread] %d ticks trades=%d net=%s" % (spread, m["trades"], fmt(m["net"])))
        for lookback in (1, 2, 3):
            for sweep in (6, 12, 24):
                for score in (3.0, 4.0, 5.0):
                    cfg = variant_config("E_complete", max_risk=50.0, enforce_limits=False)
                    cfg.structure.swing_lookback = lookback
                    cfg.sweep_window = sweep
                    cfg.structure.sweep_window = sweep
                    cfg.min_score = score
                    run = run_backtest(bars, cfg, vix, label="sens")
                    m = compute_metrics(run)
                    results["sensitivity"].append({"swing_lookback": lookback, "sweep_window": sweep,
                                                   "min_score": score, "trades": m["trades"],
                                                   "net": m["net"], "pf": m["pf"], "expectancy": m["expectancy"]})
                    print("[sens] lb=%d sw=%d score=%.0f trades=%d net=%s" % (lookback, sweep, score, m["trades"], fmt(m["net"])))

    with open(os.path.join(args.out, "metrics.json"), "w") as f:
        json.dump(results, f, indent=2, sort_keys=True)
    write_report(results, os.path.join(args.out, "report.md"))
    print("wrote", os.path.join(args.out, "report.md"))


def write_report(R, path):
    L = []
    A = L.append
    A("# QuantLOL XAUUSD research report\n")
    A("Generated: %s (UTC)\n" % R["generated_utc"])
    A("> **Read this first.** Nothing in this report is a profitability claim. It is a measurement of a "
      "specific rule set on specific free data, with explicit costs and explicit sample sizes. Small samples "
      "cannot establish an edge, and a backtest is not a forward test.\n")

    A("## 1. Data\n")
    rows = []
    for k, v in R.get("data_manifest", {}).get("datasets", {}).items():
        rows.append([k, v.get("rows"), v.get("first"), v.get("last"), v.get("source")])
    if rows:
        A(md_table(["dataset", "rows", "first", "last", "source"], rows) + "\n")
    A("Datasets used in this run:\n")
    A(md_table(["dataset", "bars", "start", "end"],
               [[k, v["bars"], v["start"][:16], v["end"][:16]] for k, v in R["datasets"].items()]) + "\n")

    A("### Assumptions and their status\n")
    for k, v in R["assumptions"].items():
        A("- **%s**: %s" % (k, v))
    A("")
    A("Limitations that follow directly from the data:\n")
    A("- GC=F is COMEX **futures**, not spot XAUUSD: level differences and basis noise exist.\n")
    A("- Yahoo intraday history is limited (≈60 days of 5-minute bars, ≈2 years of 1-hour bars).\n")
    A("- **No historical spread is available.** A constant 30-tick spread is modelled; this is an assumption, "
      "not a measurement. Section 6 shows how much the result depends on it.\n")
    A("- No tick-level latency can be derived from bar data, so **no historical latency numbers are claimed** here.\n")

    A("## 2. Executable configuration (0.01 lot, 5 USD risk cap, account limits enforced)\n")
    A("These runs use the same risk rules as the cBot: 5 USD planned risk, 15 USD daily loss, 30 USD equity "
      "drawdown kill switch. When a limit trips, sampling stops, exactly as the bot would stop trading.\n")
    rows = []
    for k, m in R["variants"].items():
        if not k.endswith("|exec"): continue
        ds, name, _ = k.split("|")
        if m.get("trades") is None:
            rows.append([ds, name, "untested", "-", "-", "-", "-", "-", "-", "-"])
            continue
        rows.append([ds, name, m["trades"], m["setups"], fmt(m["net"]), fmt(m["pf"]), fmt(m["expectancy"], 3),
                     fmt(m["expectancy_ci"][0], 3), fmt(m["max_dd"]),
                     m["rejected"].get("RiskCapExceeded", 0) + m["rejected"].get("RiskCapAtFill", 0),
                     m["ended_by_kill"] or "no"])
    A(md_table(["dataset", "variant", "trades", "setups", "net USD", "PF", "expectancy", "CI low", "max DD",
                "risk-cap rejections", "stopped by limit"], rows) + "\n")
    A("The risk-cap column is the central finding of this section: with a 0.01-lot (1 oz) position, a 5 USD "
      "cap leaves ~4.81 USD for the structural stop after costs. Most intraday gold setups are wider than that, "
      "so they are **skipped, not resized**. This is the mandate doing its job, and it is why the trade counts "
      "are small. Expectancy intervals that straddle zero mean the sample cannot distinguish this rule set "
      "from noise.\n")

    A("## 3. Research counterfactual (cap relaxed to 50 USD, account limits off)\n")
    A("> These runs exist **only** to obtain a sample large enough to compare modules. They are **not** a "
      "tradable configuration under the stated mandate: the extra risk is real, and disabling the daily/kill "
      "limits would be unacceptable in production. The cBot never runs this way.\n")
    rows = []
    for k, m in R["variants"].items():
        if not k.endswith("|counterfactual"): continue
        ds, name, _ = k.split("|")
        ci = m.get("expectancy_ci", [None, None])
        rows.append([ds, name, m["trades"], fmt(m["net"]), fmt(m["pf"]), fmt(m["expectancy"], 3),
                     fmt(ci[0], 3), fmt(ci[1], 3), fmt(m["win_rate"]), fmt(m["avg_r"], 3), fmt(m["max_dd"])])
    A(md_table(["dataset", "variant", "trades", "net", "PF", "expectancy", "CI low", "CI high", "win %", "avg R", "max DD"], rows) + "\n")
    A("Read module contributions across a row group (same dataset), never across datasets. A difference "
      "smaller than the bootstrap interval is noise.\n")
    A("### Reading the module deltas\n")
    A("- **AMT (B vs A)**: the implemented gate is *\"do not buy above the value area / do not sell below it\"*. "
      "It rarely binds, because a bullish CHoCH after a sweep of lows already sits below VAH. Where B differs "
      "from A, the difference is target selection (VAH/VAL/session levels join the target set), not the gate.\n")
    A("- **Order flow (D vs B)**: the confirmation is a broker-tick-volume delta proxy over a short window. "
      "It removes signals; it does not add them. Fewer trades with worse expectancy is evidence *against* this "
      "proxy on this sample, and it is reported as such.\n")
    A("- **VIX (E vs D)**: VIX only removes signals when it is at or above the configured 30 threshold. In "
      "quiet samples E and D are identical by construction, which is visible in the rejection table rather "
      "than being papered over.\n")
    A("- **GEX (C)**: **untested.** No legitimate historical dealer-gamma series exists in this repository, "
      "and the bot refuses to derive one from gold candles. A row is left empty instead of inventing one.\n")

    if R.get("walk_forward"):
        A("## 4. Walk-forward (3 chronological folds, fixed parameters, limits off)\n")
        A("No parameter is fitted on any fold. The folds exist to show regime dependence and stability, "
          "not to select a winner.\n")
        rows = []
        for name, wf in R["walk_forward"].items():
            rows.append([name, wf["pooled_trades"], fmt(wf["pooled_net"]), fmt(wf["pooled_expectancy"], 3),
                         fmt(wf["expectancy_ci"][0], 3), fmt(wf["expectancy_ci"][1], 3)])
        A(md_table(["variant", "pooled trades", "pooled net", "pooled expectancy", "CI low", "CI high"], rows) + "\n")
        for name, wf in R["walk_forward"].items():
            A("**%s**\n" % name)
            A(md_table(["fold", "period", "trades", "net", "PF", "expectancy"],
                       [[f["fold"], f["period"][0][:10] + " -> " + f["period"][1][:10], f["trades"], fmt(f["net"]),
                         fmt(f["pf"]), fmt(f["expectancy"], 3)] for f in wf["folds"]]) + "\n")

    if R.get("spread_sensitivity"):
        A("## 5. Cost assumption sensitivity\n")
        A("The spread is the least defensible number in this study because no historical bid/ask was "
          "available. The table shows how the result moves when it is wrong in either direction.\n")
        A(md_table(["spread (ticks)", "trades", "net", "PF", "expectancy"],
                   [[s["spread_ticks"], s["trades"], fmt(s["net"]), fmt(s["pf"]), fmt(s["expectancy"], 3)]
                    for s in R["spread_sensitivity"]]) + "\n")

    if R.get("sensitivity"):
        A("## 6. Parameter sensitivity (5-minute, counterfactual, limits off)\n")
        A("This grid is **descriptive, not an optimisation**: no parameter set is selected from it, and no "
          "result here is carried back into the bot. A surface that swings from profit to loss across "
          "neighbouring cells is a warning, not an opportunity.\n")
        rows = [[s["swing_lookback"], s["sweep_window"], s["min_score"], s["trades"], fmt(s["net"]),
                 fmt(s["pf"]), fmt(s["expectancy"], 3)] for s in R["sensitivity"]]
        A(md_table(["swing lookback", "sweep window", "min score", "trades", "net", "PF", "expectancy"], rows) + "\n")

    A("## 7. Rejection diagnostics\n")
    agg = {}
    for k, m in R["variants"].items():
        for reason, cnt in (m.get("rejected") or {}).items():
            agg[reason] = agg.get(reason, 0) + cnt
    A(md_table(["rejection reason", "count"], sorted(agg.items(), key=lambda kv: -kv[1])) + "\n")
    A("`NoSetup` is the expected base rate: most bars contain no qualifying sweep plus confirmation. "
      "`RiskCapExceeded` is the mandate. `RewardTooLow` means structure did not offer 1.5R. `EntryDrift` is the "
      "next-open revalidation. The per-event log with timestamps is in `rejections.csv`.\n")

    A("## 8. What is measured, what is not\n")
    A("| Claim | Status in this report |\n|---|---|\n"
      "| Strategy profitability | **Not claimed.** Backtest only, small sample, intervals span zero. |\n"
      "| Out-of-sample behaviour | Chronological folds with fixed parameters; nothing was fitted. |\n"
      "| Realised latency | **Not measured here.** Timings come from the cBot's own instrumentation during forward testing. |\n"
      "| Historical spread | **Assumed constant 30 ticks**, not measured; see section 5. |\n"
      "| GEX / Delta / Vanna contribution | **Untested.** No legitimate historical options-exposure series was available. |\n"
      "| Forward-test results | **Empty by design** until the bot has run on a demo account; see docs/FORWARD_TEST.md. |\n"
      "| Commission | Broker-dependent assumption (0.07 USD/side). Verify against your account. |\n")

    A("## 9. Files\n")
    A("- `research/out/metrics.json` - every number in this report, machine-readable")
    A("- `research/out/trades.csv` - all simulated trades, tagged by run and configuration")
    A("- `research/out/rejections.csv` - rejected setups with timestamp, reason and price")
    A("- `research/run_pipeline.py`, `research/backtest.py`, `research/quantlol_ref.py` - the harness")
    A("")
    A("Reproduce with `python3 research/run_pipeline.py`. The harness is deterministic; there is no random "
      "seed except in the bootstrap (fixed at 42).")
    with open(path, "w") as f:
        f.write("\n".join(L))


if __name__ == "__main__":
    main()
