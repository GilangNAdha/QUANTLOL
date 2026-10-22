"""
The signal service: fetch -> validate -> evaluate on closed bars -> persist -> expose state.

It never places, modifies or closes an order. There is no broker connection anywhere in this
package; the only outputs are files and an HTTP read view.
"""
from __future__ import annotations

import threading
import time
from datetime import datetime, timezone

from . import datafeed, engine
from .config import SignalConfig
from .core_bridge import core, describe_engine
from .store import Store

APP_VERSION = "1.0.0"


class SignalService:
    def __init__(self, cfg: SignalConfig):
        self.cfg = cfg
        self.store = Store(cfg.output_dir)
        self.started = time.time()
        self._lock = threading.Lock()
        self._wake = threading.Event()
        self._stop = threading.Event()
        self._thread = None
        self.feed = None
        self.vix = None
        self.gex = None
        self.latest = None
        self.recent = []
        self.stats = {"evaluations": 0, "signals": 0, "by_reason": {}, "by_state": {},
                      "by_session": {}, "last_eval_utc": None, "last_eval_ms": None,
                      "data_refreshes": 0, "errors": 0}
        prev = self.store.load_state()
        self.last_key = prev.get("last_dedupe_key") or ""

    # ------------------------------------------------------------------ data
    def refresh_data(self, now=None):
        now = now or datetime.now(timezone.utc)
        feed = datafeed.load_feed(self.cfg)
        spot = feed.bars[-1].c if feed.ok and feed.bars else None
        vix = datafeed.load_vix(self.cfg, now=now)
        gex = datafeed.load_gex(self.cfg, spot=spot, now=now)
        with self._lock:
            self.feed, self.vix, self.gex = feed, vix, gex
            self.stats["data_refreshes"] += 1
        return feed, vix, gex

    # -------------------------------------------------------------- evaluation
    def poll_once(self, force=False, now=None):
        """Evaluates the newest CLOSED bar. Emits a signal only once per bar/direction."""
        now = now or datetime.now(timezone.utc)
        feed, vix, gex = self.refresh_data(now=now)
        if not feed.ok:
            self.stats["errors"] += 1
            return self._state(error=feed.error)
        if len(feed.bars) < 50:
            return self._state(error="insufficient history (%d bars)" % len(feed.bars))

        rec = engine.evaluate_bar(feed.bars, len(feed.bars) - 1, self.cfg, feed=feed,
                                  vix_status=vix, gex_status=gex, now=now)
        rec["actionable"] = rec["state"] in ("long", "short")
        rec["signal_id"] = _signal_id(rec)
        rec["is_new"] = False

        with self._lock:
            self.stats["evaluations"] += 1
            self.stats["by_state"][rec["state"]] = self.stats["by_state"].get(rec["state"], 0) + 1
            key = rec.get("reason") or "None"
            if not rec["actionable"]:
                self.stats["by_reason"][key] = self.stats["by_reason"].get(key, 0) + 1
            self.stats["by_session"][rec["session"]] = self.stats["by_session"].get(rec["session"], 0) + 1
            self.stats["last_eval_utc"] = now.isoformat()
            self.stats["last_eval_ms"] = rec["engine_ms"]

            new_bar = rec["dedupe_key"] != self.last_key
            if rec["actionable"] and (force or new_bar):
                rec["is_new"] = True
                self.stats["signals"] += 1
                self.recent.append(rec)
                self.recent = self.recent[-self.cfg.max_recent_signals:]
                self.last_key = rec["dedupe_key"]
                self.store.append_signal(rec)
            self.store.append_evaluation(rec)
            self.latest = rec

        return self._state()

    # ------------------------------------------------------------------ state
    def _state(self, error=None):
        feed, vix, gex = self.feed, self.vix, self.gex
        now = datetime.now(timezone.utc)
        issues = []
        if feed is None or not feed.ok:
            issues.append({"severity": "error", "text": "market data: " + (error or "unavailable")})
        else:
            if not feed.is_fresh(self.cfg.max_bar_age_minutes, now):
                closed = _is_market_closed(now)
                if self.cfg.data_mode == "local":
                    # The committed snapshot is deliberately static: say so instead of pretending
                    # it is a live feed, and never upgrade its quality label.
                    issues.append({"severity": "info",
                                   "text": "offline snapshot: last bar is %.0f min old (committed research data). "
                                           "Use data_mode=live or a CSV export for fresh bars." % feed.age_minutes(now)})
                else:
                    issues.append({"severity": "warn",
                                   "text": "last bar is %.0f min old%s" % (
                                       feed.age_minutes(now), " (market closed)" if closed else "")})
        if vix and vix.available and not vix.fresh:
            issues.append({"severity": "info", "text": "VIX is stale (%s); it will not score new signals" % vix.status})
        if vix and not vix.available and self.cfg.enable_vix:
            issues.append({"severity": "warn", "text": "VIX enabled but " + vix.status})
        if gex and gex.available and not gex.fresh:
            issues.append({"severity": "warn", "text": "GEX stale: new entries would be blocked by policy"})
        if gex and not gex.available and self.cfg.enable_gex:
            issues.append({"severity": "warn", "text": "GEX enabled but " + gex.status})

        latest = self.latest or {}
        return {
            "schema": 1,
            "app": {"name": "QuantLOL Signals", "version": APP_VERSION,
                    "execution": "none - advisory signals only, no broker connection",
                    "engine": describe_engine(),
                    "uptime_seconds": round(time.time() - self.started, 1)},
            "generated_utc": now.isoformat(),
            "config": self.cfg.summary(),
            "data": _data_block(feed, self.cfg, now),
            "external": {"vix": _ext_block(vix), "gex": _ext_block(gex)},
            "latest": latest,
            "recent_signals": list(reversed(self.recent[-25:])),
            "stats": dict(self.stats),
            "health": {"ok": not any(i["severity"] == "error" for i in issues), "issues": issues,
                       "market_closed": _is_market_closed(now)},
            "stored": self.store.counts(),
        }

    def state(self):
        return self._state()

    # ------------------------------------------------------------------- loop
    def run_forever(self):
        while not self._stop.is_set():
            try:
                self.poll_once()
            except Exception as e:                      # never let the loop die
                self.stats["errors"] += 1
                self.last_error = "%s: %s" % (type(e).__name__, e)
            self._wake.wait(self.cfg.poll_seconds)
            self._wake.clear()

    def start_background(self):
        self._thread = threading.Thread(target=self.run_forever, name="signal-loop", daemon=True)
        self._thread.start()
        return self._thread

    def stop(self):
        self._stop.set()
        self._wake.set()
        if self._thread:
            self._thread.join(timeout=5)

    def wake(self):
        self._wake.set()


def _signal_id(rec):
    return "%s-%s-%s" % (rec["bar_time_utc"].replace(":", "").replace("-", "")[:13],
                         rec["direction"], rec.get("setup") or "na")


def _data_block(feed, cfg, now):
    if feed is None:
        return {"ok": False, "error": "not loaded"}
    return {
        "ok": feed.ok, "source": feed.source, "quality": feed.quality, "note": feed.note,
        "bars": len(feed.bars), "rows_rejected": feed.rows_rejected,
        "first_bar_utc": feed.first.isoformat() if feed.first else None,
        "last_bar_utc": feed.last.isoformat() if feed.last else None,
        "age_minutes": round(feed.age_minutes(now), 2) if feed.ok else None,
        "fresh": feed.is_fresh(cfg.max_bar_age_minutes, now) if feed.ok else False,
        "fetched_utc": feed.fetched_utc.isoformat() if feed.fetched_utc else None,
    }


def _ext_block(status):
    if status is None:
        return {"available": False, "status": "not loaded"}
    return {"available": status.available, "status": status.status, "quality": status.quality,
            "fresh": status.fresh, "age_minutes": round(status.age_minutes, 1) if status.available else None,
            "data": status.data if status.available else None}


def _is_market_closed(now):
    try:
        return core.market_closed(now, core.SessionSettings())
    except Exception:
        return False
