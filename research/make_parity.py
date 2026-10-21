#!/usr/bin/env python3
"""
Generates tests/fixtures/parity.json.

The fixture is produced by the Python reference implementation (quantlol_ref.py) and
consumed by the C# test suite (ParityTests.cs). A failing parity test means the backtest
and the shipped cBot no longer implement the same rules - that is a bug, not a tolerance
to widen.

Inputs are a deterministic slice of the committed 5-minute research data, so the fixture
is reproducible from the repository alone (no network, no randomness).
"""
import gzip, json, os, sys
from datetime import datetime, timedelta, timezone
from types import SimpleNamespace

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from quantlol_ref import (EngineConfig, StructureOptions, analyze_structure, build_profile,
                          order_flow, evaluate_signal, planned_risk, DrawdownGuard)

HERE = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.join(HERE, "data", "gc_5m.csv.gz")
SLICE_START = 4200        # deterministic offset into the committed file
SLICE_LEN = 600


def load_rows(path):
    with gzip.open(path, "rt") as f:
        header = f.readline().strip().split(",")
        idx = {k: i for i, k in enumerate(header)}
        rows = []
        for line in f:
            p = line.strip().split(",")
            if len(p) < 6: continue
            t = datetime.strptime(p[idx["time_utc"]], "%Y-%m-%d %H:%M:%S").replace(tzinfo=timezone.utc)
            rows.append((t, float(p[idx["open"]]), float(p[idx["high"]]), float(p[idx["low"]]),
                         float(p[idx["close"]]), float(p[idx["volume"]])))
    return rows


class Bar:
    __slots__ = ("t", "o", "h", "l", "c", "v")
    def __init__(self, t, o, h, l, c, v):
        self.t, self.o, self.h, self.l, self.c, self.v = t, o, h, l, c, v


def to_bars(rows):
    return [Bar(*r) for r in rows]


def bar_json(b):
    return [b.t.strftime("%Y-%m-%d %H:%M:%S"), b.o, b.h, b.l, b.c, b.v]


def snap_fields(s):
    def sweep(x):
        return None if x is None else {"i": x["i"], "level": x["level"], "extreme": x["extreme"],
                                       "kind": x["kind"], "dir": x["dir"],
                                       "pierce_ticks": x["pierce_ticks"]}
    def fvg(x):
        return None if x is None else {"i": x["i"], "top": x["top"], "bottom": x["bottom"], "filled": x["filled"]}
    def ob(x):
        return None if x is None else {"top": x["top"], "bottom": x["bottom"]}
    return {"swings": [list(x) for x in s.swings],
            "last_choch_i": s.last_choch_i, "last_choch_dir": s.last_choch_dir,
            "last_choch_level": s.last_choch_level, "dealing_high": s.dealing_high,
            "dealing_low": s.dealing_low, "atr": s.atr,
            "bull_sweep": sweep(s.bull_sweep), "bear_sweep": sweep(s.bear_sweep),
            "bull_fvg": fvg(s.bull_fvg), "bear_fvg": fvg(s.bear_fvg),
            "bull_ob": ob(s.bull_ob), "bear_ob": ob(s.bear_ob)}


def ns(sd):
    return SimpleNamespace(**sd)


def targets_for(snap, prof):
    t = [(snap.dealing_high, "SwingHigh"), (snap.dealing_low, "SwingLow")]
    t += [(s[2], "SwingHigh" if s[3] else "SwingLow") for s in snap.swings[-8:]]
    if prof:
        t += [(prof["vah"], "ValueAreaHigh"), (prof["val"], "ValueAreaLow"),
              (prof["high"], "SessionHigh"), (prof["low"], "SessionLow")]
    return t


def cfg_json(c):
    return {"spread_ticks": c.spread_ticks, "max_spread_ticks": c.max_spread_ticks, "tick": c.tick,
            "min_score": c.min_score, "min_rr": c.min_rr, "max_risk": c.max_risk,
            "require_location": c.require_location, "require_order_flow": c.require_order_flow,
            "require_confirmation": c.require_confirmation, "enable_fvg": c.enable_fvg,
            "enable_order_block": c.enable_order_block, "enable_vix": c.enable_vix,
            "block_on_high_vix": c.block_on_high_vix, "vix_high": c.vix_high,
            "allow_synthetic": c.allow_synthetic_target, "fallback_rr": c.fallback_rr,
            "stop_buffer_ticks": c.stop_buffer_ticks, "sweep_window": c.sweep_window,
            "confirm_window": c.confirm_window, "location_proximity_ticks": c.location_proximity_ticks,
            "commission": c.commission_rt, "slippage": c.slippage_ticks * c.tick * c.money_per_price,
            "money_per_price": c.money_per_price}


def main():
    rows = load_rows(DATA)
    all_bars = to_bars(rows)
    bars = all_bars[SLICE_START:SLICE_START + SLICE_LEN]
    opt = StructureOptions()

    snap = analyze_structure(bars, len(bars) - 1, opt, [])
    prof = build_profile(bars, len(bars) - 140, len(bars) - 1, 0.10, 70.0, 48)
    of = order_flow(bars, len(bars) - 1, 12)
    avg_vol = sum(b.v for b in bars[-20:]) / 20.0

    cfg = EngineConfig()
    cases, seen = [], {}

    def add(name, bar, sd, prof_s, of_s, cfg_s, vix_s, source):
        L = len(bars)
        # the decision is made on bar `bar`; bars[bar] must be the last closed bar
        sub = bars[:bar + 1]
        dec = evaluate_signal(cfg_s, sub, bar, ns(sd), prof_s, None, of_s,
                              targets_for(ns(sd), prof_s), vix_s)
        exp = {"state": dec["state"], "reject": dec.get("reject", "None"), "detail": dec.get("detail", ""),
               "dir": dec.get("dir", 0)}
        if dec["state"] in ("bull", "bear"):
            exp.update({"entry": dec["entry"], "stop": dec["stop"], "target": dec["target"],
                        "score": dec["score"], "risk_usd": dec["risk_usd"], "rr_net": dec["rr_net"],
                        "synthetic": dec["synthetic"], "setup": dec["setup"]})
        cases.append({"name": name, "source": source, "bar_index": bar, "snapshot": sd,
                      "profile": prof_s,
                      "order_flow": None if of_s is None else
                      {"n": of_s["n"], "ratio": of_s["ratio"], "div_bull": of_s["div_bull"],
                       "div_bear": of_s["div_bear"]},
                      "targets": [t[0] for t in targets_for(ns(sd), prof_s)],
                      "vix": vix_s, "config": cfg_json(cfg_s), "expected": exp})

    # ---- natural decisions across the slice -------------------------------------
    natural = []
    for i in range(200, len(bars)):
        si = analyze_structure(bars, i, opt, [])
        sdi = snap_fields(si)
        pi = build_profile(bars, max(0, i - 140), i, 0.10, 70.0, 48)
        oi = order_flow(bars, i, 12)
        dec = evaluate_signal(cfg, bars[:i + 1], i, ns(sdi), pi, None, oi,
                              targets_for(si, pi), None)
        natural.append((i, sdi, pi, oi, dec))

    def take(state, reject=None, limit=3):
        got = [x for x in natural
               if x[4]["state"] == state and (reject is None or x[4].get("reject") == reject)]
        step = max(1, len(got) // limit)
        return got[::step][:limit]

    picked = []
    for state in ("bull", "bear"):
        for i, sd, p, o, dec in take(state, limit=3):
            order = list(p)
            picked.append((i, sd, p, o, dec, cfg, "natural %s" % state))
    for reject in ("NoSetup", "ScoreBelowThreshold", "RewardTooLow", "RiskCapExceeded", "Conflict"):
        got = take("neutral", reject, limit=1)
        for i, sd, p, o, dec in got:
            picked.append((i, sd, p, o, dec, cfg, "natural %s" % reject))

    for i, sd, p, o, dec, c, src in picked:
        add("bar%d_%s_%s" % (i, dec["state"], dec.get("reject", "None")), i, sd, p, o, c, None, src)

    # ---- forced branches, starting from a real bullish snapshot -------------------
    bull = take("bull", limit=1)
    if bull:
        i, sd, p, o, dec = bull[0]
        add("forced_spread", i, sd, p, o, EngineConfig(spread_ticks=60, max_spread_ticks=40), None,
            "spread gate on a real bullish setup")
        add("forced_vix", i, sd, p, o, EngineConfig(), {"valid": True, "level": 35.0},
            "VIX veto on a real bullish setup")
        add("forced_low_score", i, sd, p, o, EngineConfig(min_score=9.5), None,
            "score gate on a real bullish setup")
        add("forced_tight_cap", i, sd, p, o, EngineConfig(max_risk=0.5), None,
            "risk cap on a real bullish setup")
        add("forced_no_of", i, sd, p,
            {"n": 12, "ratio": 0.0, "div_bull": False, "div_bear": False},
            EngineConfig(require_order_flow=True), None,
            "order-flow veto on a real bullish setup")

    risk_cases = []
    for entry, stop, target in ((3300.0, 3298.8, 3303.0), (3300.0, 3299.0, 3298.0),
                                (3300.0, 3301.2, 3297.0), (3300.0, 3304.0, 3310.0)):
        r = planned_risk(entry, stop, target, 1.0, 0.14, 0.05)
        risk_cases.append({"entry": entry, "stop": stop, "target": target, "money": 1.0,
                           "commission": 0.14, "slippage": 0.05,
                           "expected": {k: r[k] for k in ("dist", "risk", "net_reward", "rr_net", "rr_gross")}})

    g = DrawdownGuard(1000.0, 15.0, 30.0)
    events = []
    base = datetime(2026, 3, 2, 14, 0)
    for k, (pnl, eq) in enumerate(((-6.0, 994.0), (-5.0, 989.0), (-4.5, 984.5), (-6.0, 978.5))):
        g.on_close(pnl, base + timedelta(minutes=5 * k), eq)
        st = g.evaluate(eq, base + timedelta(minutes=5 * k))
        events.append({"pnl": pnl, "equity": eq, "allowed": st[0], "reason": st[1]})
    guard = {"reference": 1000.0, "daily_loss": 15.0, "equity_dd": 30.0, "events": events,
             "expected": {"latched": g.latched, "realized_today": g.realized_today}}

    fixture = {
        "meta": {
            "generator": "research/make_parity.py",
            "purpose": "Cross-language parity between the Python research engine and src/QuantLOL/Core.",
            "note": "Values are produced by the Python reference. A mismatch is a bug in one of the two implementations.",
            "source": "research/data/gc_5m.csv.gz rows %d..%d (COMEX GC=F proxy)" % (SLICE_START, SLICE_START + SLICE_LEN - 1),
            "structure_options": {k: getattr(opt, k) for k in
                                  ("swing_lookback", "scan_bars", "tick", "min_sweep_pierce_ticks",
                                   "min_fvg_ticks", "ob_lookback", "sweep_window", "dealing_swings", "fvg_scan")},
        },
        "bars": [bar_json(b) for b in bars],
        "structure": {"last_index": len(bars) - 1, "expected": {
            "bars_analysed": snap.n, "atr": snap.atr, "trend": snap.trend, "swings": len(snap.swings),
            "last_bos_i": snap.last_bos_i, "last_bos_dir": snap.last_bos_dir, "last_bos_level": snap.last_bos_level,
            "last_choch_i": snap.last_choch_i, "last_choch_dir": snap.last_choch_dir,
            "last_choch_level": snap.last_choch_level,
            "dealing_high": snap.dealing_high, "dealing_low": snap.dealing_low,
            "bull_sweep": snap.bull_sweep, "bear_sweep": snap.bear_sweep,
            "bull_fvg": snap.bull_fvg, "bear_fvg": snap.bear_fvg,
            "bull_ob": snap.bull_ob, "bear_ob": snap.bear_ob}},
        "profile": {"start": len(bars) - 140, "end": len(bars) - 1, "bin": 0.10, "va_percent": 70.0,
                    "ib_bars": 48,
                    "expected": {k: prof[k] for k in ("poc", "vah", "val", "high", "low",
                                                      "total_volume", "ib_high", "ib_low", "partial")}},
        "orderflow": {"last_index": len(bars) - 1, "window": 12, "avg_volume": avg_vol,
                      "expected": {k: of[k] for k in ("delta", "ratio", "buy", "sell", "total",
                                                      "div_bull", "div_bear")}},
        "risk": risk_cases,
        "guard": guard,
        "decisions": cases,
    }
    out = os.path.join(HERE, "..", "tests", "fixtures", "parity.json")
    with open(out, "w") as f:
        json.dump(fixture, f, indent=1, sort_keys=True, default=str)
    print("wrote", os.path.abspath(out))
    for c in cases:
        print("  %-28s %-8s %-18s score=%s" % (c["name"], c["expected"]["state"],
                                               c["expected"]["reject"], c["expected"].get("score")))


if __name__ == "__main__":
    main()
