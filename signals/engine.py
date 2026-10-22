"""
Context assembly + decision, using the shared rule engine.

This module contains NO trading rules of its own: it builds the inputs the rules need
(session profile, prior session, structural levels, order flow, VIX/GEX context) and calls
quantlol_ref.evaluate_signal. Whatever the cBot decides on the same closed bar, this decides
too - that is what the parity tests protect.
"""
from __future__ import annotations

import time
from dataclasses import dataclass, field
from datetime import datetime, timezone

from .core_bridge import core
from .config import SignalConfig

SCHEMA_VERSION = 1


@dataclass
class Context:
    bars: list
    index: int
    session: str
    session_profile: dict | None
    prev_profile: dict | None
    levels: list                      # [(price, kind)]
    snap: object
    order_flow: dict | None
    targets: list
    atr: float
    price: float
    time: datetime


def engine_config(cfg: SignalConfig):
    st = core.StructureOptions(
        swing_lookback=cfg.swing_lookback, scan_bars=cfg.scan_bars, tick=cfg.tick,
        min_sweep_pierce_ticks=2, min_fvg_ticks=10, ob_lookback=20,
        sweep_window=cfg.sweep_window, dealing_swings=4, fvg_scan=60)
    return core.EngineConfig(
        tick=cfg.tick, money_per_price=cfg.money_per_price,
        commission_rt=cfg.commission_round_trip, slippage_ticks=cfg.slippage_ticks,
        spread_ticks=cfg.assumed_spread_ticks, max_spread_ticks=cfg.max_spread_ticks,
        stop_buffer_ticks=cfg.stop_buffer_ticks, min_score=cfg.min_score, min_rr=cfg.min_rr,
        require_location=cfg.require_location, require_confirmation=cfg.require_confirmation,
        require_order_flow=cfg.require_order_flow, enable_fvg=cfg.enable_fvg,
        enable_order_block=cfg.enable_order_block, enable_amt=True, enable_gex=cfg.enable_gex,
        enable_vix=cfg.enable_vix, block_on_high_vix=cfg.block_on_high_vix, vix_high=cfg.vix_high,
        fallback_rr=cfg.fallback_rr, allow_synthetic_target=cfg.allow_synthetic_target,
        sweep_window=cfg.sweep_window, confirm_window=cfg.confirm_window,
        location_proximity_ticks=cfg.location_proximity_ticks, structure=st,
        max_risk=cfg.max_risk_usd, lots=cfg.lots, units=cfg.money_per_price)


def build_context(bars, index, cfg: SignalConfig, sessions=None, gex_levels=None) -> Context:
    sessions = sessions or core.SessionSettings()
    i = index
    b = bars[i]
    levels = []
    session_profile = prev_profile = None

    ws = core.window_start(b.t, sessions)
    s0 = _index_at_or_after(bars, ws, max(0, i - 1200), i)
    if s0 is not None and i - s0 >= 10:
        session_profile = core.build_profile(bars, s0, i, cfg.tick * 10, 70.0, max(1, 60 // cfg.timeframe_minutes))
        if session_profile:
            levels += [(session_profile["val"], "ValueAreaLow"),
                       (session_profile["vah"], "ValueAreaHigh"),
                       (session_profile["high"], "SessionHigh"),
                       (session_profile["low"], "SessionLow")]

    ps, pe = core.previous_session(b.t, sessions)
    p0 = _index_at_or_after(bars, ps, max(0, i - 1200), i)
    p1 = _index_before(bars, pe, max(0, i - 1200), i)
    if p0 is not None and p1 is not None and p1 > p0 + 5:
        prev_profile = core.build_profile(bars, p0, p1, cfg.tick * 10, 70.0, 12)
        if prev_profile:
            levels += [(prev_profile["high"], "PrevSessionHigh"), (prev_profile["low"], "PrevSessionLow")]

    for lv in (gex_levels or []):
        levels.append((float(lv["price"]), _kind_name(lv.get("kind", ""))))

    snap = core.analyze_structure(bars, i, core.StructureOptions(
        swing_lookback=cfg.swing_lookback, scan_bars=cfg.scan_bars, tick=cfg.tick,
        sweep_window=cfg.sweep_window), levels)

    of = core.order_flow(bars, i, 12)

    targets = [(snap.dealing_high, "SwingHigh"), (snap.dealing_low, "SwingLow")]
    targets += [(s[2], "SwingHigh" if s[3] else "SwingLow") for s in snap.swings[-8:]]
    targets += levels

    return Context(bars=bars, index=i, session=core.classify_session(b.t, sessions),
                   session_profile=session_profile, prev_profile=prev_profile, levels=levels,
                   snap=snap, order_flow=of, targets=targets, atr=snap.atr, price=b.c, time=b.t)


def _kind_name(kind):
    k = (kind or "").lower().replace("_", " ")
    return {"call wall": "CallWall", "put wall": "PutWall", "gamma flip": "GammaFlip",
            "call_wall": "CallWall", "put_wall": "PutWall", "gamma_flip": "GammaFlip"}.get(k, "GexLevel")


def _index_at_or_after(bars, t, lo, hi):
    for i in range(lo, hi + 1):
        if bars[i].t >= t:
            return i
    return None


def _index_before(bars, t, lo, hi):
    for i in range(hi, lo - 1, -1):
        if bars[i].t < t:
            return i
    return None


# ------------------------------------------------------------------------- decision
def evaluate_bar(bars, index, cfg: SignalConfig, feed=None, vix_status=None, gex_status=None,
                 now=None) -> dict:
    """Evaluates ONE closed bar. Returns a JSON-serialisable signal record."""
    now = now or datetime.now(timezone.utc)
    t0 = time.perf_counter()
    bar = bars[index]

    gex_levels = None
    gex = {"status": "GEX:disabled", "available": False, "quality": "disabled"}
    if gex_status is not None:
        gex = {"status": gex_status.status, "available": gex_status.available,
               "quality": gex_status.quality, "fresh": gex_status.fresh,
               "age_minutes": round(gex_status.age_minutes, 1) if gex_status.available else None,
               "data": gex_status.data if gex_status.available else None}
        if gex_status.available and gex_status.fresh and gex_status.data:
            gex_levels = gex_status.data.get("levels") or None

    vix = {"status": "VIX:disabled", "available": False, "quality": "disabled"}
    vix_input = None
    if vix_status is not None:
        vix = {"status": vix_status.status, "available": vix_status.available,
               "quality": vix_status.quality, "fresh": vix_status.fresh,
               "age_minutes": round(vix_status.age_minutes, 1) if vix_status.available else None,
               "level": (vix_status.data or {}).get("level") if vix_status.available else None,
               "as_of": (vix_status.data or {}).get("as_of") if vix_status.available else None}
        if vix_status.available:
            # stale VIX is passed as an explicit invalid input, never as a silent zero
            vix_input = {"valid": vix_status.fresh, "level": vix_status.data["level"]}

    ctx = build_context(bars, index, cfg, gex_levels=gex_levels)
    dec = core.evaluate_signal(engine_config(cfg), bars, index, ctx.snap, ctx.session_profile,
                               ctx.prev_profile, ctx.order_flow, ctx.targets, vix_input)

    elapsed_ms = (time.perf_counter() - t0) * 1000.0
    state = {"bull": "long", "bear": "short"}.get(dec["state"], dec["state"])
    direction = dec.get("dir", 0) or 0

    rec = {
        "schema": SCHEMA_VERSION,
        "generated_utc": now.isoformat(),
        "engine": "quantlol_ref (shared with cBot C# core)",
        "symbol": cfg.symbol,
        "label": cfg.label,
        "timeframe_minutes": cfg.timeframe_minutes,
        "bar_time_utc": bar.t.isoformat(),
        "bar_age_minutes": round((now - bar.t).total_seconds() / 60.0, 2),
        "price": round(bar.c, 4),
        "state": state,
        "direction": "LONG" if direction > 0 else ("SHORT" if direction < 0 else "NONE"),
        "entry": None, "stop": None, "target": None,
        "risk_usd": None, "rr_net": None, "score": None, "setup": None,
        "stop_ticks": None, "target_ticks": None,
        "evidence": [],
        "reason": dec.get("reject") or "None",
        "detail": dec.get("detail", ""),
        "session": ctx.session,
        "atr": round(ctx.atr, 4),
        "dealing_high": round(ctx.snap.dealing_high, 4),
        "dealing_low": round(ctx.snap.dealing_low, 4),
        "volume": "0.01 lot fixed = %.2f oz, never resized" % (cfg.lots * cfg.ounces_per_lot),
        "money_per_price": cfg.money_per_price,
        "max_risk_usd": cfg.max_risk_usd,
        "max_stop_distance": round(cfg.max_stop_distance, 4),
        "spread": {"assumed_ticks": cfg.assumed_spread_ticks,
                   "note": "no quote feed: spread is an ASSUMPTION used for the risk check",
                   "cost_usd": round(cfg.assumed_spread_ticks * cfg.tick * cfg.money_per_price, 4)},
        "context": {"vix": vix, "gex": gex},
        "data": None,
        "engine_ms": round(elapsed_ms, 3),
    }
    if feed is not None:
        rec["data"] = {"source": feed.source, "quality": feed.quality, "note": feed.note,
                       "last_bar_utc": feed.last.isoformat() if feed.last else None,
                       "bars": len(feed.bars), "rows_rejected": feed.rows_rejected,
                       "fresh": feed.is_fresh(cfg.max_bar_age_minutes, now),
                       "age_minutes": round(feed.age_minutes(now), 2)}

    if state in ("long", "short"):
        risk = dec["risk_usd"]
        rec.update({
            "entry": round(dec["entry"], 4),
            "stop": round(dec["stop"], 4),
            "target": round(dec["target"], 4),
            "risk_usd": round(risk, 4),
            "rr_net": round(dec["rr_net"], 3),
            "score": round(dec["score"], 2),
            "setup": dec["setup"],
            "synthetic_target": dec["synthetic"],
            "stop_ticks": round(abs(dec["entry"] - dec["stop"]) / cfg.tick, 1),
            "target_ticks": round(abs(dec["target"] - dec["entry"]) / cfg.tick, 1),
            "evidence": [e for e in (dec.get("evidence") or "").split(";") if e],
            "risk_ok": risk <= cfg.max_risk_usd + 1e-9,
            "risk_headroom": round(cfg.max_risk_usd - risk, 4),
        })
    else:
        rec["risk_ok"] = False

    rec["dedupe_key"] = "%s|%s|%s" % (bar.t.isoformat(), state, rec["direction"])
    return rec


def evaluate_latest(feed, cfg: SignalConfig, vix_status=None, gex_status=None, now=None):
    if not feed.ok or len(feed.bars) < cfg.scan_bars // 2:
        return {"state": "unavailable", "reason": "DataStale",
                "detail": (feed.error or "not enough bars") if not feed.ok else "insufficient history",
                "bar_time_utc": None, "generated_utc": (now or datetime.now(timezone.utc)).isoformat()}
    return evaluate_bar(feed.bars, len(feed.bars) - 1, cfg, feed=feed,
                        vix_status=vix_status, gex_status=gex_status, now=now)


def replay(bars, cfg: SignalConfig, vix_status=None, gex_status=None, start=None, limit=None):
    """Evaluates a series of closed bars, yielding only the actionable ones plus counts."""
    start = start if start is not None else max(cfg.scan_bars, 1)
    end = len(bars) - 1
    hits, counts = [], {}
    for i in range(start, end + 1):
        # cheap context per bar keeps replay faithful; slow on purpose, it is a research tool
        rec = evaluate_bar(bars, i, cfg, vix_status=vix_status, gex_status=gex_status)
        if rec["state"] in ("long", "short"):
            hits.append(rec)
            if limit and len(hits) >= limit:
                break
        else:
            key = rec.get("reason") or "Unknown"
            counts[key] = counts.get(key, 0) + 1
    return {"signals": hits, "rejections": counts, "bars_evaluated": max(0, min(end, i) - start + 1)}
