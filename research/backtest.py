"""
Event-driven, cost-aware backtest harness for QuantLOL.

Execution model (deliberately conservative):
  * a decision made on the close of bar i is filled at the OPEN of bar i+1;
  * the spread is paid in full (half on entry, half on exit) and a slippage
    allowance is applied adversely on entry and on stop exits;
  * when a bar touches both the stop and the target, the STOP is assumed first;
  * gaps through the stop fill at the gap price;
  * break-even and trailing stops are only adjusted on closed bars, so they can
    never use intrabar information unavailable to the bot;
  * commission is charged round-turn per trade.

The same rules are implemented in C# (src/QuantLOL/Core) and pinned by tests.
"""
from __future__ import annotations
import gzip, json, math, os, random
from dataclasses import dataclass, asdict, replace
from datetime import datetime, timedelta, timezone

from quantlol_ref import (
    Bar, EngineConfig, SessionSettings, StructureOptions, DrawdownGuard, RiskCaps,
    analyze_structure, build_profile, order_flow, evaluate_signal, planned_risk,
    classify_session, window_start, previous_session, market_closed,
)


# ----------------------------------------------------------------------------- io
def load_bars(path):
    bars = []
    with gzip.open(path, "rt") as f:
        header = f.readline().strip().split(",")
        idx = {k: i for i, k in enumerate(header)}
        for line in f:
            p = line.strip().split(",")
            if len(p) < 6: continue
            t = datetime.strptime(p[idx["time_utc"]], "%Y-%m-%d %H:%M:%S").replace(tzinfo=timezone.utc)
            try:
                o, h, l, c, v = (float(p[idx["open"]]), float(p[idx["high"]]), float(p[idx["low"]]),
                                 float(p[idx["close"]]), float(p[idx["volume"]]))
            except ValueError:
                continue
            if not all(math.isfinite(x) for x in (o, h, l, c, v)): continue
            if h < l: continue
            bars.append(Bar(t, o, h, l, c, v))
    bars.sort(key=lambda b: b.t)
    # drop duplicate timestamps
    out = []
    for b in bars:
        if out and b.t == out[-1].t: continue
        out.append(b)
    return out


def load_vix(path):
    rows = {}
    with gzip.open(path, "rt") as f:
        f.readline()
        for line in f:
            p = line.strip().split(",")
            if len(p) < 5: continue
            try:
                d = datetime.strptime(p[0], "%Y-%m-%d %H:%M:%S").date()
                rows[d] = float(p[4])
            except (ValueError, IndexError):
                continue
    return rows


# ------------------------------------------------------------------------- engine
@dataclass
class Trade:
    id: int
    direction: str
    setup: str
    session: str
    regime: str
    signal_time: str
    entry_time: str
    exit_time: str
    entry: float
    stop: float
    target: float
    exit_price: float
    exit_reason: str
    gross_pnl: float
    commission: float
    net_pnl: float
    risk_usd: float
    r_multiple: float
    mae_usd: float
    mfe_usd: float
    bars_held: int
    score: float
    spread_ticks: float
    slippage_usd: float
    synthetic_target: bool
    evidence: str


@dataclass
class Reject:
    time: str
    reason: str
    detail: str
    price: float
    session: str


def _day_key(t): return t.date()


def run_backtest(bars, cfg: EngineConfig, vix_by_day=None, label="run", max_bars=None,
                 collect_curve=True):
    """Runs one configuration over bars[0:end]. Returns dict with trades/rejects/diagnostics."""
    trades, rejects = [], []
    equity = 1000.0
    guard = DrawdownGuard(1000.0, cfg.daily_loss, cfg.equity_dd)
    peak_equity = equity
    max_dd = 0.0
    last_entry_t = None
    last_loss_t = None
    last_signal_key = None
    rejected_counts = {}
    setups_seen = 0
    ended_by_kill = None
    pos = None
    warmup = max(cfg.structure.scan_bars, 120)
    end = len(bars) - 2 if max_bars is None else min(len(bars) - 2, max_bars)
    curve = []
    half_spread = cfg.spread_ticks * cfg.tick / 2.0
    slip_price = cfg.slippage_ticks * cfg.tick

    for i in range(warmup, end + 1):
        b = bars[i]
        t = b.t

        # ---------------- manage an open position on this bar ----------------
        if pos is not None:
            exit_price = None
            reason = None
            if pos["dir"] > 0:
                if b.o <= pos["stop"]:            # gap through stop
                    exit_price, reason = b.o, "stop_gap"
                elif b.l <= pos["stop"]:
                    exit_price, reason = pos["stop"] - slip_price, "stop_loss"
                elif b.h >= pos["target"]:
                    exit_price, reason = pos["target"], "take_profit"
                elif cfg.time_stop_min > 0 and not pos["be"] and (t - pos["entry_t"]).total_seconds() / 60 >= cfg.time_stop_min:
                    exit_price, reason = b.c, "time_stop"
            else:
                if b.o >= pos["stop"]:
                    exit_price, reason = b.o, "stop_gap"
                elif b.h >= pos["stop"]:
                    exit_price, reason = pos["stop"] + slip_price, "stop_loss"
                elif b.l <= pos["target"]:
                    exit_price, reason = pos["target"], "take_profit"
                elif cfg.time_stop_min > 0 and not pos["be"] and (t - pos["entry_t"]).total_seconds() / 60 >= cfg.time_stop_min:
                    exit_price, reason = b.c, "time_stop"

            # excursions (uses bar extremes; conservative/approximate)
            if pos["dir"] > 0:
                pos["mae"] = min(pos["mae"], (b.l - pos["entry"]) * cfg.money_per_price)
                pos["mfe"] = max(pos["mfe"], (b.h - pos["entry"]) * cfg.money_per_price)
            else:
                pos["mae"] = min(pos["mae"], (pos["entry"] - b.h) * cfg.money_per_price)
                pos["mfe"] = max(pos["mfe"], (pos["entry"] - b.l) * cfg.money_per_price)

            if exit_price is None:
                # break-even / trailing adjusted on CLOSED bars only
                r_now = ((b.c - pos["entry"]) if pos["dir"] > 0 else (pos["entry"] - b.c)) / pos["risk_dist"]
                if cfg.break_even_r > 0 and not pos["be"] and r_now >= cfg.break_even_r:
                    pos["stop"] = pos["entry"] + (2 * cfg.tick if pos["dir"] > 0 else -2 * cfg.tick)
                    pos["be"] = True
                if cfg.trail_start_r > 0 and r_now >= cfg.trail_start_r and pos["atr"] > 0:
                    best = b.h if pos["dir"] > 0 else b.l
                    cand = best - cfg.trail_atr_mult * pos["atr"] if pos["dir"] > 0 else best + cfg.trail_atr_mult * pos["atr"]
                    if (pos["dir"] > 0 and cand > pos["stop"] + cfg.tick) or (pos["dir"] < 0 and cand < pos["stop"] - cfg.tick):
                        pos["stop"] = cand
                continue

            # exit accounting: half spread paid on the way out
            exit_eff = exit_price - half_spread if pos["dir"] > 0 else exit_price + half_spread
            gross = (exit_eff - pos["entry"]) * pos["dir"] * cfg.money_per_price
            net = gross - cfg.commission_rt
            trades.append(Trade(
                id=len(trades) + 1, direction="LONG" if pos["dir"] > 0 else "SHORT",
                setup=pos["setup"], session=pos["session"], regime=pos["regime"],
                signal_time=pos["signal_t"].strftime("%Y-%m-%d %H:%M"), entry_time=pos["entry_t"].strftime("%Y-%m-%d %H:%M"),
                exit_time=t.strftime("%Y-%m-%d %H:%M"), entry=round(pos["entry"], 4), stop=round(pos["stop0"], 4),
                target=round(pos["target"], 4), exit_price=round(exit_eff, 4), exit_reason=reason,
                gross_pnl=round(gross, 4), commission=cfg.commission_rt, net_pnl=round(net, 4),
                risk_usd=round(pos["risk_usd"], 4), r_multiple=round(net / pos["risk_usd"], 4) if pos["risk_usd"] > 0 else 0.0,
                mae_usd=round(pos["mae"], 4), mfe_usd=round(pos["mfe"], 4), bars_held=pos["bars"],
                score=pos["score"], spread_ticks=cfg.spread_ticks,
                slippage_usd=round(slip_price * cfg.money_per_price, 4),
                synthetic_target=pos["synthetic"], evidence=pos["evidence"]))
            equity += net
            guard.on_close(net, t, equity)
            if net < 0: last_loss_t = t
            peak_equity = max(peak_equity, equity)
            max_dd = max(max_dd, peak_equity - equity)
            pos = None
            if collect_curve: curve.append((t.strftime("%Y-%m-%d %H:%M"), round(equity, 2)))

        # ---------------- new signal at the close of bar i ----------------
        if pos is not None: continue
        if cfg.reject_calendar_closed and market_closed(t, cfg.sessions):
            continue
        sess = classify_session(t, cfg.sessions)
        if sess == "Asia" and not cfg.enable_asia: continue
        if sess == "London" and not cfg.enable_london: continue
        if sess == "NewYork" and not cfg.enable_ny: continue

        guard.on_equity(equity, t)
        ok, why, _ = guard.evaluate(equity, t)
        if not ok:
            if not cfg.enforce_account_limits:
                pass                      # research run: keep sampling, limits only recorded
            else:
                if guard.latched or why.startswith("equity drawdown"):
                    ended_by_kill = why
                    break                 # account-level stop; no further sampling
                key = why.split(" ")[0]
                rejected_counts[key] = rejected_counts.get(key, 0) + 1
                continue
        if last_entry_t is not None and (t - last_entry_t).total_seconds() / 60 < cfg.cooldown_trade_min: continue
        if last_loss_t is not None and (t - last_loss_t).total_seconds() / 60 < cfg.cooldown_loss_min: continue

        # context
        session_profile = None
        prev_profile = None
        levels = []
        if cfg.enable_amt:
            ws = window_start(t, cfg.sessions)
            s0 = _index_at_or_after(bars, ws, max(0, i - 1200), i)
            if s0 is not None and i - s0 >= 10:
                session_profile = build_profile(bars, s0, i, cfg.structure.tick * 10, 70.0,
                                                max(1, 60 // 5))
                if session_profile:
                    levels += [(session_profile["val"], "ValueAreaLow"), (session_profile["vah"], "ValueAreaHigh"),
                               (session_profile["high"], "SessionHigh"), (session_profile["low"], "SessionLow")]
            ps, pe = previous_session(t, cfg.sessions)
            p0 = _index_at_or_after(bars, ps, max(0, i - 1200), i)
            p1 = _index_before(bars, pe, max(0, i - 1200), i)
            if p0 is not None and p1 is not None and p1 > p0 + 5:
                prev_profile = build_profile(bars, p0, p1, cfg.structure.tick * 10, 70.0, 12)
                if prev_profile:
                    levels += [(prev_profile["high"], "PrevSessionHigh"), (prev_profile["low"], "PrevSessionLow")]

        snap = analyze_structure(bars, i, cfg.structure, levels)
        of = order_flow(bars, i, cfg.delta_window if hasattr(cfg, "delta_window") else 12) if cfg.require_order_flow or cfg.enable_amt else None

        # targets: swings from context, session/prev levels, dealing range
        targets = [(snap.dealing_high, "SwingHigh"), (snap.dealing_low, "SwingLow")]
        targets += [(s[2], "SwingHigh" if s[3] else "SwingLow") for s in snap.swings[-8:]]
        targets += levels

        vix = None
        if cfg.enable_vix and vix_by_day:
            lvl = vix_by_day.get(t.date())
            if lvl is not None:
                vix = {"valid": True, "level": lvl}
            else:
                # carry the most recent prior observation; staleness is reported later
                prior = [d for d in vix_by_day if d <= t.date()]
                if prior:
                    vix = {"valid": True, "level": vix_by_day[max(prior)]}

        dec = evaluate_signal(cfg, bars, i, snap, session_profile, prev_profile, of, targets, vix)
        if dec["state"] not in ("bull", "bear"):
            reason = dec.get("reject", "NoSetup")
            rejected_counts[reason] = rejected_counts.get(reason, 0) + 1
            if reason not in ("NoSetup", "ScoreBelowThreshold") and len(rejects) < 5000:
                rejects.append(Reject(t.strftime("%Y-%m-%d %H:%M"), reason, dec.get("detail", ""), round(b.c, 3), sess))
            continue

        setups_seen += 1
        # ---- fill at next bar open, re-validate risk with the actual fill ----
        nb = bars[i + 1]
        d = dec["dir"]
        entry_fill = nb.o + (half_spread + slip_price) * d
        risk_dist = abs(entry_fill - dec["stop"])
        slip_usd = cfg.slippage_ticks * cfg.tick * cfg.money_per_price
        r = planned_risk(entry_fill, dec["stop"], dec["target"], cfg.money_per_price, cfg.commission_rt, slip_usd)
        if r["risk"] > cfg.max_risk:
            rejected_counts["RiskCapAtFill"] = rejected_counts.get("RiskCapAtFill", 0) + 1
            rejects.append(Reject(nb.t.strftime("%Y-%m-%d %H:%M"), "RiskCapAtFill",
                                  "fill %.3f moved risk to %.2f" % (entry_fill, r["risk"]), round(entry_fill, 3), sess))
            continue
        drift = abs(nb.o - b.c) / cfg.tick
        if cfg.max_entry_drift_ticks > 0 and drift > cfg.max_entry_drift_ticks:
            rejected_counts["EntryDrift"] = rejected_counts.get("EntryDrift", 0) + 1
            rejects.append(Reject(nb.t.strftime("%Y-%m-%d %H:%M"), "EntryDrift", "drift %.0f ticks" % drift, round(nb.o, 3), sess))
            continue

        pos = {"dir": d, "entry": entry_fill, "stop": dec["stop"], "stop0": dec["stop"], "target": dec["target"],
               "entry_t": nb.t, "signal_t": t, "risk_dist": risk_dist, "risk_usd": r["risk"],
               "mae": 0.0, "mfe": 0.0, "bars": 0, "be": False, "atr": snap.atr,
               "score": dec["score"], "evidence": dec["evidence"], "session": sess,
               "regime": "Balance" if (session_profile and session_profile["val"] <= b.c <= session_profile["vah"]) else "Discovery",
               "setup": dec["setup"], "synthetic": dec["synthetic"]}
        last_entry_t = nb.t
        last_signal_key = (d, i)

    return {"label": label, "trades": trades, "rejects": rejects, "rejected_counts": rejected_counts,
            "equity_curve": curve, "final_equity": equity, "max_dd_closed": max_dd,
            "setups": setups_seen, "bars": max(0, end - warmup + 1), "ended_by_kill": ended_by_kill,
            "start": bars[warmup].t.isoformat() if bars else None,
            "end": bars[end].t.isoformat() if bars else None}


def _index_at_or_after(bars, t, lo, hi):
    for i in range(lo, hi + 1):
        if bars[i].t >= t: return i
    return None


def _index_before(bars, t, lo, hi):
    for i in range(hi, lo - 1, -1):
        if bars[i].t < t: return i
    return None


# ------------------------------------------------------------------------- metrics
def compute_metrics(run):
    trades = run["trades"]
    n = len(trades)
    m = {"trades": n, "net": 0.0, "gross_profit": 0.0, "gross_loss": 0.0, "wins": 0,
         "pf": None, "expectancy": None, "win_rate": None, "avg_win": None, "avg_loss": None,
         "max_dd": run["max_dd_closed"], "recovery": None, "sharpe": None, "sortino": None,
         "avg_hold": None, "median_hold": None, "avg_mae": None, "avg_mfe": None,
         "commission": 0.0, "slippage": 0.0, "by_session": {}, "by_setup": {}, "avg_r": None,
         "rejected": run["rejected_counts"], "setups": run["setups"], "equity": run["final_equity"]}
    if n == 0: return m
    for t in trades:
        m["net"] += t.net_pnl
        m["commission"] += t.commission
        m["slippage"] += t.slippage_usd
        if t.net_pnl > 0: m["gross_profit"] += t.net_pnl; m["wins"] += 1
        else: m["gross_loss"] += -t.net_pnl
        for key, bucket in (("by_session", t.session), ("by_setup", t.setup)):
            d = m[key].setdefault(bucket, {"n": 0, "net": 0.0, "wins": 0})
            d["n"] += 1; d["net"] += t.net_pnl; d["wins"] += 1 if t.net_pnl > 0 else 0
    m["net"] = round(m["net"], 4)
    m["pf"] = round(m["gross_profit"] / m["gross_loss"], 3) if m["gross_loss"] > 0 else None
    m["expectancy"] = round(m["net"] / n, 4)
    m["win_rate"] = round(100.0 * m["wins"] / n, 2)
    m["avg_win"] = round(m["gross_profit"] / m["wins"], 3) if m["wins"] else None
    losses = n - m["wins"]
    m["avg_loss"] = round(-m["gross_loss"] / losses, 3) if losses else None
    m["recovery"] = round(m["net"] / m["max_dd"], 3) if m["max_dd"] > 0 else None
    m["avg_hold"] = round(sum((datetime.fromisoformat(t.exit_time) - datetime.fromisoformat(t.entry_time)).total_seconds() / 60 for t in trades) / n, 1)
    holds = sorted((datetime.fromisoformat(t.exit_time) - datetime.fromisoformat(t.entry_time)).total_seconds() / 60 for t in trades)
    m["median_hold"] = round(holds[len(holds) // 2], 1)
    m["avg_mae"] = round(sum(t.mae_usd for t in trades) / n, 3)
    m["avg_mfe"] = round(sum(t.mfe_usd for t in trades) / n, 3)
    m["avg_r"] = round(sum(t.r_multiple for t in trades) / n, 3)
    # risk-adjusted ratios from daily PnL
    daily = {}
    for t in trades:
        d = t.exit_time[:10]
        daily[d] = daily.get(d, 0.0) + t.net_pnl
    if len(daily) >= 20:
        eq = 1000.0; rets = []
        for d in sorted(daily):
            rets.append(daily[d] / eq); eq += daily[d]
        mean = sum(rets) / len(rets)
        sd = math.sqrt(sum((r - mean) ** 2 for r in rets) / (len(rets) - 1)) if len(rets) > 1 else 0
        m["sharpe"] = round(mean / sd * math.sqrt(252), 3) if sd > 0 else None
        dn = [r * r for r in rets if r < 0]
        dsd = math.sqrt(sum(dn) / len(dn)) if dn else 0
        m["sortino"] = round(mean / dsd * math.sqrt(252), 3) if dsd > 0 else None
    return m


def bootstrap_ci(pnls, iterations=4000, seed=42):
    if len(pnls) < 5: return (None, None)
    rng = random.Random(seed)
    n = len(pnls)
    means = []
    for _ in range(iterations):
        s = 0.0
        for _ in range(n): s += pnls[rng.randrange(n)]
        means.append(s / n)
    means.sort()
    return (round(means[int(0.025 * iterations)], 4), round(means[int(0.975 * iterations) - 1], 4))
