"""
Data acquisition and validation for the signal application.

Sources (all free, no key, no subscription):
  * local  : the committed gzip CSV snapshots (research/data/*.csv.gz) - works offline
  * live   : Yahoo Finance chart API for GC=F (COMEX front month) - needs outbound network
  * csv    : any broker/platform export with time,open,high,low,close[,volume]

Every load is validated before it reaches the engine: monotonic timestamps, no duplicates,
finite OHLC, high >= low, and a freshness check against the newest bar. Data that fails is
reported, never silently repaired into something that looks tradable.
"""
from __future__ import annotations

import csv
import gzip
import io
import json
import os
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone

from .core_bridge import core

UA = "Mozilla/5.0 (X11; Linux x86_64) quantlol-signals/1.0"


# --------------------------------------------------------------------------- result
@dataclass
class FeedResult:
    ok: bool
    bars: list
    source: str
    quality: str            # broker | proxy | delayed | unknown
    note: str = ""
    error: str = ""
    fetched_utc: datetime = None
    rows_read: int = 0
    rows_rejected: int = 0
    first: datetime = None
    last: datetime = None

    def age_minutes(self, now=None):
        if self.last is None:
            return float("inf")
        now = now or datetime.now(timezone.utc)
        return (now - self.last).total_seconds() / 60.0

    def is_fresh(self, max_age_minutes, now=None):
        return self.ok and self.age_minutes(now) <= max_age_minutes

    def describe(self, now=None):
        if not self.ok:
            return "DATA:INVALID(%s)" % (self.error or "no data")
        return "DATA:%s rows=%d last=%s age=%.1fm src=%s" % (
            self.quality, len(self.bars), self.last.strftime("%Y-%m-%d %H:%M"),
            self.age_minutes(now), self.source)


# ---------------------------------------------------------------------------- parse
_TIME_FORMATS = ("%Y-%m-%d %H:%M:%S", "%Y-%m-%dT%H:%M:%S", "%Y-%m-%d %H:%M", "%Y-%m-%dT%H:%M",
                 "%Y-%m-%d", "%d/%m/%Y %H:%M:%S", "%m/%d/%Y %H:%M:%S")


def parse_time(text):
    s = (text or "").strip().replace("Z", "").replace("+00:00", "")
    for fmt in _TIME_FORMATS:
        try:
            return datetime.strptime(s, fmt).replace(tzinfo=timezone.utc)
        except ValueError:
            continue
    raise ValueError("unrecognised timestamp: " + repr(text))


def parse_rows(rows, has_header=True, source="csv"):
    """rows: list of lists (csv.reader). Returns (bars, rejected, first, last)."""
    if not rows:
        return [], 0, None, None
    start = 0
    cols = None
    head = [str(c).strip().lower() for c in rows[0]]
    if has_header and any(h in head for h in ("time_utc", "time", "timestamp", "date", "datetime")):
        cols = {name: head.index(name) for name in head if name}
        start = 1
    idx_t = None
    for candidate in ("time_utc", "time", "timestamp", "datetime", "date"):
        if cols and candidate in cols:
            idx_t = cols[candidate]
            break
    bars, rejected = [], 0
    for r in rows[start:]:
        if len(r) < 5:
            rejected += 1
            continue
        try:
            t = parse_time(r[idx_t if idx_t is not None else 0])
            if cols and "open" in cols:
                o, h, l, c = (float(r[cols["open"]]), float(r[cols["high"]]),
                              float(r[cols["low"]]), float(r[cols["close"]]))
                v = float(r[cols["volume"]]) if "volume" in cols and cols["volume"] < len(r) else 0.0
            else:
                # time,open,high,low,close[,volume]
                o, h, l, c = float(r[1]), float(r[2]), float(r[3]), float(r[4])
                v = float(r[5]) if len(r) > 5 and str(r[5]).strip() != "" else 0.0
        except (ValueError, IndexError):
            rejected += 1
            continue
        if not all(_finite(x) for x in (o, h, l, c, v)) or h < l:
            rejected += 1
            continue
        bars.append(core.Bar(t, o, h, l, c, v))
    bars.sort(key=lambda b: b.t)
    deduped = []
    for b in bars:
        if deduped and b.t == deduped[-1].t:
            rejected += 1
            continue
        deduped.append(b)
    return deduped, rejected, (deduped[0].t if deduped else None), (deduped[-1].t if deduped else None)


def _finite(x):
    return x == x and x not in (float("inf"), float("-inf"))


# ---------------------------------------------------------------------------- loads
def load_local(path, has_header=True):
    if not os.path.exists(path):
        return FeedResult(False, [], source="local", quality="unknown", error="file not found: " + path)
    try:
        if path.endswith(".gz"):
            with gzip.open(path, "rt", newline="") as f:
                rows = list(csv.reader(f))
        else:
            with open(path, newline="") as f:
                rows = list(csv.reader(f))
    except Exception as e:
        return FeedResult(False, [], source="local", quality="unknown", error="%s: %s" % (type(e).__name__, e))
    bars, rejected, first, last = parse_rows(rows, has_header=has_header, source=path)
    if not bars:
        return FeedResult(False, [], source="local", quality="unknown",
                          error="no usable bars in %s" % path, rows_rejected=rejected)
    quality = "proxy" if "gc_" in os.path.basename(path) else "broker"
    note = ("COMEX GC=F continuous front month from Yahoo - a PROXY for spot XAUUSD, not the traded instrument"
            if quality == "proxy" else "local export")
    return FeedResult(True, bars, source="file:" + os.path.basename(path), quality=quality, note=note,
                      fetched_utc=datetime.now(timezone.utc), rows_read=len(rows), rows_rejected=rejected,
                      first=first, last=last)


def _http_get(url, timeout=20, retries=2):
    last = None
    for i in range(retries + 1):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": UA, "Accept": "*/*"})
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return r.read()
        except urllib.error.HTTPError as e:
            last = "HTTP %s" % e.code
            if e.code in (429, 500, 502, 503) and i < retries:
                time.sleep(2.0 ** i)
                continue
            break
        except Exception as e:
            last = "%s: %s" % (type(e).__name__, e)
            if i < retries:
                time.sleep(2.0 ** i)
    raise RuntimeError("GET failed (%s)" % last)


def fetch_live(symbol="GC=F", interval="5m", rng="60d", timeout=20):
    url = ("https://query1.finance.yahoo.com/v8/finance/chart/%s?interval=%s&range=%s"
           "&includePrePost=false" % (symbol, interval, rng))
    try:
        raw = json.loads(_http_get(url, timeout=timeout))
        res = raw["chart"]["result"][0]
    except Exception as e:
        return FeedResult(False, [], source="yahoo", quality="unknown", error="%s: %s" % (type(e).__name__, e))
    ts = res.get("timestamp") or []
    q = (res.get("indicators", {}).get("quote") or [{}])[0]
    rows = [["time_utc", "open", "high", "low", "close", "volume"]]
    for i, t in enumerate(ts):
        try:
            o, h, l, c = q["open"][i], q["high"][i], q["low"][i], q["close"][i]
            v = q.get("volume", [0] * len(ts))[i] or 0
        except (KeyError, IndexError, TypeError):
            continue
        if None in (o, h, l, c):
            continue
        rows.append([datetime.fromtimestamp(int(t), timezone.utc).strftime("%Y-%m-%d %H:%M:%S"),
                     o, h, l, c, v])
    bars, rejected, first, last = parse_rows(rows, has_header=True)
    if not bars:
        return FeedResult(False, [], source="yahoo", quality="unknown", error="no bars returned")
    meta = res.get("meta", {})
    return FeedResult(
        True, bars, source="yahoo:" + symbol, quality="proxy",
        note="%s via Yahoo Finance (delayed, free). Exchange tz %s. Not spot XAUUSD." % (
            symbol, meta.get("exchangeTimezoneName")),
        fetched_utc=datetime.now(timezone.utc), rows_read=len(rows), rows_rejected=rejected,
        first=first, last=last)


def _from_provider(bars, meta, cfg):
    if not bars:
        return FeedResult(False, [], source=meta.get("source", "?"), quality="unknown", error="no bars returned")
    note = meta.get("note", "")
    if not meta.get("volume_available", True):
        note += (" | NO VOLUME: order-flow confirmations will be skipped; "
                 "set require_order_flow=false or the engine will reject every bar")
    return FeedResult(True, bars, source=meta.get("source", "?"), quality=meta.get("quality", "unknown"),
                      note=note, fetched_utc=datetime.now(timezone.utc), rows_read=len(bars),
                      rows_rejected=0, first=bars[0].t, last=bars[-1].t)


def load_feed(cfg):
    """local | csv | live. In live mode a named provider is used, or the tested fallback chain."""
    from . import providers

    if cfg.data_mode == "local":
        return load_local(cfg.data_path)
    if cfg.data_mode == "csv":
        return load_local(cfg.csv_path, has_header=cfg.csv_has_header)
    if cfg.data_mode != "live":
        return FeedResult(False, [], source=cfg.data_mode, quality="unknown",
                          error="unknown data_mode %r" % cfg.data_mode)
    key = os.environ.get(cfg.apikey_env or "", "").strip() or None
    order = [cfg.live_provider] if cfg.live_provider else list(providers.FALLBACK_ORDER)
    errors = []
    for name in order:
        try:
            bars, meta = providers.fetch(name, cfg.live_symbol or None, cfg.timeframe_minutes,
                                         limit=cfg.live_limit, api_key=key, yahoo_range=cfg.live_range,
                                         market=cfg.market)
            res = _from_provider(bars, meta, cfg)
            if errors:
                res.note = ("fell back to %s after %d failure(s): %s | %s"
                            % (name, len(errors), "; ".join(errors), res.note))
            return res
        except Exception as e:
            errors.append("%s:%s" % (name, str(e)[:90]))
    return FeedResult(False, [], source="live-chain", quality="unknown",
                      error="every provider failed -> " + "; ".join(errors))


# ----------------------------------------------------------------------- context IO
@dataclass
class ExternalStatus:
    name: str
    available: bool
    quality: str
    age_minutes: float
    fresh: bool
    status: str
    data: object = None


def load_vix(cfg, now=None):
    now = now or datetime.now(timezone.utc)
    if not cfg.enable_vix:
        return ExternalStatus("VIX", False, "disabled", float("inf"), False, "VIX:disabled")
    if not os.path.exists(cfg.vix_path):
        return ExternalStatus("VIX", False, "unavailable", float("inf"), False,
                              "VIX:missing(file %s)" % cfg.vix_path)
    try:
        with open(cfg.vix_path) as f:
            raw = json.load(f)
        level = float(raw.get("vix", raw.get("level", raw.get("close"))))
        as_of = _parse_iso(raw.get("as_of"))
        if not (0 < level < 300):
            raise ValueError("implausible level %s" % level)
        if as_of is None:
            raise ValueError("missing as_of")
        if as_of > now + timedelta(minutes=5):
            raise ValueError("as_of in the future")
        age = (now - as_of).total_seconds() / 60.0
        fresh = age <= cfg.vix_max_age_minutes
        quality = "live" if str(raw.get("quality", "")).lower() == "live" else "delayed"
        snap = {"level": round(level, 2), "as_of": as_of.isoformat(), "quality": quality,
                "source": raw.get("source", "file"), "change_1d": raw.get("change_1d")}
        status = "VIX:%s(%s,age=%.0fm)" % ("FRESH" if fresh else "STALE", quality, age)
        return ExternalStatus("VIX", True, quality, age, fresh, status, snap)
    except Exception as e:
        return ExternalStatus("VIX", False, "invalid", float("inf"), False, "VIX:INVALID(%s)" % e)


def load_gex(cfg, spot=None, now=None):
    now = now or datetime.now(timezone.utc)
    if cfg.enable_gex and getattr(cfg, "gex_source", "file") == "deribit":
        return load_gex_deribit(cfg, spot=spot, now=now)
    if not cfg.enable_gex:
        return ExternalStatus("GEX", False, "disabled", float("inf"), False, "GEX:disabled")
    if not os.path.exists(cfg.gex_path):
        return ExternalStatus("GEX", False, "unavailable", float("inf"), False,
                              "GEX:missing(file %s)" % cfg.gex_path)
    try:
        with open(cfg.gex_path) as f:
            raw = json.load(f)
        inst = str(raw.get("instrument", "")).strip().upper()
        if not inst:
            raise ValueError("missing instrument")
        proxy = inst in ("GLD", "IAU", "GLD_PROXY")
        if not proxy and inst != cfg.symbol.upper().replace("/", ""):
            raise ValueError("instrument mismatch: %s vs %s" % (inst, cfg.symbol))
        as_of = _parse_iso(raw.get("as_of"))
        if as_of is None:
            raise ValueError("missing as_of")
        if as_of > now + timedelta(minutes=5):
            raise ValueError("as_of in the future")
        levels = []
        for lv in raw.get("levels", []) or []:
            p = float(lv.get("price"))
            if not _finite(p):
                continue
            if p < cfg.gex_min_price or p > cfg.gex_max_price:
                raise ValueError("level %.2f outside sane range [%s,%s]" % (p, cfg.gex_min_price, cfg.gex_max_price))
            if spot and abs(p - spot) / spot > 0.5:
                raise ValueError("level %.2f implausibly far from spot %.2f" % (p, spot))
            levels.append({"price": round(p, 2), "kind": str(lv.get("kind", "")).lower()})
        for key, kind in (("gamma_flip", "gamma_flip"), ("call_wall", "call_wall"), ("put_wall", "put_wall")):
            v = raw.get(key)
            if v is None:
                continue
            p = float(v)
            if not _finite(p):
                continue
            if p < cfg.gex_min_price or p > cfg.gex_max_price:
                raise ValueError("%s %.2f outside sane range" % (key, p))
            levels.append({"price": round(p, 2), "kind": kind})
        if not levels:
            raise ValueError("no usable price levels")
        age = (now - as_of).total_seconds() / 60.0
        fresh = age <= cfg.gex_max_age_minutes
        quality = "proxy" if proxy else "manual"
        snap = {"as_of": as_of.isoformat(), "quality": quality, "instrument": inst,
                "levels": levels, "spot": raw.get("spot"), "net_gex": raw.get("net_gex"),
                "expiry": raw.get("expiry"), "note": raw.get("greeks_note", "")}
        status = "GEX:%s(%s,levels=%d,age=%.0fm)" % ("FRESH" if fresh else "STALE", quality, len(levels), age)
        return ExternalStatus("GEX", True, quality, age, fresh, status, snap)
    except Exception as e:
        return ExternalStatus("GEX", False, "invalid", float("inf"), False, "GEX:INVALID(%s)" % e)


def load_gex_deribit(cfg, spot=None, now=None, chain=None):
    """Real positioning data: Deribit's public BTC options chain, one keyless request.

    Derived levels are open-interest concentrations. They are NOT dealer gamma - Deribit's
    summary endpoint has no gamma field - and the status string says so.
    """
    from . import providers

    now = now or datetime.now(timezone.utc)
    if spot is None:
        return ExternalStatus("GEX", False, "unavailable", float("inf"), False,
                              "GEX:missing(spot price is required to centre the chain)")
    try:
        chain = chain if chain is not None else providers.btc_option_chain(cfg.gex_currency)
        levels, info = providers.oi_levels(chain, spot)
    except Exception as e:
        return ExternalStatus("GEX", False, "unavailable", float("inf"), False,
                              "GEX:UNAVAILABLE(%s)" % str(e)[:120])
    if not levels:
        return ExternalStatus("GEX", False, "unavailable", float("inf"), False, "GEX:empty")
    snap = {"as_of": now.isoformat(), "quality": "oi-derived", "instrument": cfg.gex_currency.upper(),
            "levels": levels, "spot": round(spot, 2), "expiry": "all",
            "note": ("open interest from Deribit's keyless chain summary; no gamma field is "
                     "published there, so no dealer-gamma sign is claimed | strikes=%d contracts=%d"
                     % (info["strikes"], info["contracts"]))}
    return ExternalStatus("GEX", True, "oi-derived", 0.0, True,
                          "GEX:FRESH(oi-derived,levels=%d,src=deribit)" % len(levels), snap)


def _parse_iso(text):
    if text is None:
        return None
    s = str(text).strip().replace("Z", "+00:00")
    try:
        d = datetime.fromisoformat(s)
    except ValueError:
        return None
    if d.tzinfo is None:
        d = d.replace(tzinfo=timezone.utc)
    return d.astimezone(timezone.utc)
