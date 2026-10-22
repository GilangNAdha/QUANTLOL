"""Command line entry point:  python3 -m signals <command> [options]"""
from __future__ import annotations

import argparse
import json
import os
import sys

from . import datafeed, engine
from .config import SignalConfig, ConfigError
from .core_bridge import describe_engine
from .server import serve
from .service import APP_VERSION, SignalService


def _json(obj):
    print(json.dumps(obj, indent=2, default=str))


def cmd_doctor(args):
    cfg = _cfg(args)
    print("QuantLOL Signals %s - signal-only application (no broker, no orders)" % APP_VERSION)
    print("engine :", describe_engine()["module"], "|", describe_engine()["rules"])
    print("config : %s lots, %.2f oz/lot -> $%.2f per 1.00 price move" % (
        cfg.lots, cfg.ounces_per_lot, cfg.money_per_price))
    print("risk   : cap $%.2f -> max structural stop %.3f price units (%.0f ticks)" % (
        cfg.max_risk_usd, cfg.max_stop_distance, cfg.max_stop_distance / cfg.tick))
    print("costs  : commission $%.2f round turn, slippage %d ticks, assumed spread %d ticks (ASSUMPTION)" % (
        cfg.commission_round_trip, cfg.slippage_ticks, cfg.assumed_spread_ticks))
    feed = datafeed.load_feed(cfg)
    print("data   :", feed.describe())
    vix = datafeed.load_vix(cfg)
    gex = datafeed.load_gex(cfg, spot=feed.bars[-1].c if feed.ok and feed.bars else None)
    print("vix    :", vix.status)
    print("gex    :", gex.status)
    if not feed.ok:
        print("\nNOT READY:", feed.error)
        return 2
    if not feed.is_fresh(cfg.max_bar_age_minutes):
        print("\nWARNING: last bar is %.0f minutes old (market closed, or the data is stale)." % feed.age_minutes())
    print("\nready. `python3 -m signals once` for a single read, `serve` for the dashboard.")
    return 0


def cmd_once(args):
    cfg = _cfg(args)
    svc = SignalService(cfg)
    st = svc.poll_once(now=None)
    if args.json:
        _json(st)
        return 0
    rec = st["latest"]
    _print_record(rec, st)
    return 0 if rec.get("state") != "unavailable" else 2


def cmd_replay(args):
    cfg = _cfg(args)
    feed = datafeed.load_feed(cfg)
    if not feed.ok:
        print("data unavailable:", feed.error, file=sys.stderr)
        return 2
    vix = datafeed.load_vix(cfg)
    gex = datafeed.load_gex(cfg, spot=feed.bars[-1].c)
    res = engine.replay(feed.bars, cfg, vix_status=vix, gex_status=gex,
                        start=args.start, limit=args.limit)
    if args.json:
        _json(res)
        return 0
    print("replayed %d bars, %d signals" % (res["bars_evaluated"], len(res["signals"])))
    for r in res["signals"]:
        print("  %s  %-5s %-22s entry %8.2f stop %8.2f target %8.2f risk $%.2f rr %.2f score %.1f" % (
            r["bar_time_utc"][:16].replace("T", " "), r["direction"], (r.get("setup") or "")[:22],
            r["entry"], r["stop"], r["target"], r["risk_usd"], r["rr_net"], r["score"]))
    print("rejections:", dict(sorted(res["rejections"].items(), key=lambda kv: -kv[1])[:8]))
    return 0


def cmd_serve(args):
    cfg = _cfg(args)
    if args.port:
        cfg.port = args.port
    if args.host:
        cfg.host = args.host
    svc = SignalService(cfg)
    if args.preload:
        st = svc.poll_once()
        rec = st["latest"]
        print("preloaded: %s / %s (%s ms)" % (rec.get("state"), rec.get("reason"), rec.get("engine_ms")))
    serve(svc, host=cfg.host, port=cfg.port, background_loop=True)
    return 0


def cmd_config(args):
    cfg = _cfg(args)
    if args.init:
        cfg.save(args.init)
        print("wrote example config to", args.init)
        return 0
    cfg.validate()
    _json(cfg.summary())
    return 0


def _print_record(rec, st):
    print("=" * 78)
    print("QuantLOL Signals - %s  %s" % (rec.get("symbol", ""), rec.get("label", "")))
    print("bar   : %sZ  (%.0f min old, %s, bar close confirmed)" % (
        (rec.get("bar_time_utc") or "?")[:16].replace("T", " "), rec.get("bar_age_minutes") or 0,
        rec.get("session", "?")))
    print("price : %.2f" % (rec.get("price") or 0))
    print("state : %s   reason: %s %s" % (rec.get("state"), rec.get("reason"), rec.get("detail") or ""))
    if rec.get("state") in ("long", "short"):
        print("plan  : entry %.2f  stop %.2f  target %.2f" % (rec["entry"], rec["stop"], rec["target"]))
        print("risk  : $%.2f of $%.2f cap (headroom $%.2f)  net R:R %.2f  score %.1f" % (
            rec["risk_usd"], rec["max_risk_usd"], rec["risk_headroom"], rec["rr_net"], rec["score"]))
        print("ticks : stop %.0f  target %.0f  %s" % (
            rec["stop_ticks"], rec["target_ticks"],
            "SYNTHETIC target" if rec.get("synthetic_target") else "structural target"))
        print("setup : %s" % rec.get("setup"))
        print("why   : %s" % ", ".join(rec.get("evidence") or []))
    print("data  : %s (%s, %s)" % ((rec.get("data") or {}).get("source"),
                                   (rec.get("data") or {}).get("quality"),
                                   "fresh" if (rec.get("data") or {}).get("fresh") else "stale"))
    print("ctx   : VIX %s | GEX %s" % (
        ((rec.get("context") or {}).get("vix") or {}).get("status"),
        ((rec.get("context") or {}).get("gex") or {}).get("status")))
    print("engine: %s (%.2f ms)" % (rec.get("engine"), rec.get("engine_ms") or 0))
    for issue in st["health"]["issues"]:
        print("[%s] %s" % (issue["severity"], issue["text"]))
    print("=" * 78)


def _cfg(args):
    try:
        return SignalConfig.load(getattr(args, "config", None))
    except ConfigError as e:
        print("configuration error:", e, file=sys.stderr)
        raise SystemExit(3)


def main(argv=None):
    ap = argparse.ArgumentParser(prog="python3 -m signals",
                                 description="QuantLOL Signals - advisory signals, no broker, no orders")
    ap.add_argument("--config", help="path to a JSON config (see config/signals.example.json)")
    sub = ap.add_subparsers(dest="cmd", required=True)

    d = sub.add_parser("doctor", help="validate config, data, external inputs (like the cBot banner)")
    d.add_argument("--json", action="store_true")
    d.set_defaults(func=cmd_doctor)

    o = sub.add_parser("once", help="evaluate the newest closed bar and print the record")
    o.add_argument("--json", action="store_true")
    o.set_defaults(func=cmd_once)

    r = sub.add_parser("replay", help="replay historical bars and list the signals the engine would emit")
    r.add_argument("--start", type=int, default=None)
    r.add_argument("--limit", type=int, default=25)
    r.add_argument("--json", action="store_true")
    r.set_defaults(func=cmd_replay)

    s = sub.add_parser("serve", help="run the polling loop + local dashboard")
    s.add_argument("--port", type=int)
    s.add_argument("--host")
    s.add_argument("--preload", action="store_true", help="evaluate once before serving")
    s.set_defaults(func=cmd_serve)

    c = sub.add_parser("config", help="show the effective config, or write an example")
    c.add_argument("--init", help="write the effective config to this path")
    c.set_defaults(func=cmd_config)

    args = ap.parse_args(argv)
    return args.func(args)
