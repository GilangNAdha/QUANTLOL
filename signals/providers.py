"""
Free, keyless market-data providers.

Every adapter below talks to a **public** endpoint that needs no account, no key and no payment,
and every one of them returns OHLCV with a real volume column (which the order-flow proxy needs).
They are all venue-specific: they publish that venue's own book, never a consolidated tape.

Probe evidence (which endpoints actually answered, when) lives in tools/probe/out/live_probe.md.

Design rules:
  * the **in-progress candle is always dropped** - a signal must be decided on a closed bar;
  * timestamps are normalised to UTC and sorted ascending;
  * a provider that changes shape fails loudly with a message, never silently with zero bars;
  * no provider is assumed to work: try one, fall back to the next (see datafeed.load_feed).
"""
from __future__ import annotations

import json
import time
from datetime import datetime, timezone

from .core_bridge import core

UA = "Mozilla/5.0 (X11; Linux x86_64) quantlol-signals/1.0"
DEFAULT_TIMEOUT = 20

# timeframe minutes -> per-provider parameter
_TF = {
    "binance": {1: "1m", 3: "3m", 5: "5m", 15: "15m", 30: "30m", 60: "1h", 240: "4h"},
    "bybit": {1: "1", 3: "3", 5: "5", 15: "15", 30: "30", 60: "60", 240: "240"},
    "okx": {1: "1m", 3: "3m", 5: "5m", 15: "15m", 30: "30m", 60: "1H", 240: "4H"},
    "coinbase": {1: 60, 5: 300, 15: 900, 30: 1800, 60: 3600, 240: 21600},
    "bitstamp": {1: 60, 3: 180, 5: 300, 15: 900, 30: 1800, 60: 3600, 240: 14400},
    "kraken": {1: 1, 5: 5, 15: 15, 30: 30, 60: 60, 240: 240},
    "deribit": {1: 1, 3: 3, 5: 5, 15: 15, 30: 30, 60: 60, 240: 240},
    "yahoo": {1: "1m", 5: "5m", 15: "15m", 30: "30m", 60: "60m", 240: "60m"},
}

PROVIDERS = {
    # name: (asset classes, needs_key, base host, note)
    "binance":  ("crypto", False, "https://data-api.binance.vision", "spot klines; US-friendly mirror, api.binance.com fallback"),
    "bybit":    ("crypto", False, "https://api.bybit.com", "spot kline, newest first"),
    "okx":      ("crypto", False, "https://www.okx.com", "spot candles, includes an unconfirmed tail row"),
    "coinbase": ("crypto", False, "https://api.exchange.coinbase.com", "max 300 candles per call, newest first"),
    "bitstamp": ("crypto", False, "https://www.bitstamp.net", "ohlc endpoint, 1000 rows max"),
    "kraken":   ("crypto", False, "https://api.kraken.com", "OHLC; the final row is always in progress"),
    "deribit":  ("crypto", False, "https://www.deribit.com", "perp/index chart feed"),
    "yahoo":    ("crypto,forex,metals", False, "https://query1.finance.yahoo.com", "delayed, no FX volume, ToS-restricted for redistribution"),
    "twelvedata": ("forex", True, "https://api.twelvedata.com", "free key, 800 req/day, 8 req/min"),
}

# Sensible defaults so a user can say provider=binance without knowing the pair format.
DEFAULT_SYMBOLS = {
    "binance": "BTCUSDT", "bybit": "BTCUSDT", "okx": "BTC-USDT", "coinbase": "BTC-USD",
    "bitstamp": "btcusd", "kraken": "XBTUSD", "deribit": "BTC-PERPETUAL",
    "yahoo_btc": "BTC-USD", "yahoo_eurusd": "EURUSD=X", "yahoo_xauusd": "GC=F",
    "twelvedata": "EUR/USD",
}


class ProviderError(RuntimeError):
    pass


def _get(url, timeout=DEFAULT_TIMEOUT, retries=2):
    import urllib.error
    import urllib.request

    last = None
    for attempt in range(retries + 1):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": UA, "Accept": "application/json"})
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return r.read()
        except urllib.error.HTTPError as e:
            last = "HTTP %s" % e.code
            if e.code in (429, 418, 500, 502, 503) and attempt < retries:
                time.sleep(1.5 ** attempt)
                continue
            break
        except Exception as e:
            last = "%s: %s" % (type(e).__name__, str(e)[:160])
            if attempt < retries:
                time.sleep(1.5 ** attempt)
    raise ProviderError(last or "request failed")


def _json(url, **kw):
    raw = _get(url, **kw)
    try:
        return json.loads(raw)
    except Exception as e:
        raise ProviderError("invalid JSON (%s)" % e)


def _bar(t, o, h, l, c, v):
    return core.Bar(t, float(o), float(h), float(l), float(c), float(v or 0.0))


def _ms(t):
    return datetime.fromtimestamp(int(t) / 1000.0, timezone.utc)


def _s(t):
    return datetime.fromtimestamp(int(t), timezone.utc)


def drop_incomplete(bars, tf_minutes, now=None, tolerance_s=5):
    """Removes the still-forming candle. A decision on an open bar would repaint."""
    if not bars:
        return bars, False
    now = now or datetime.now(timezone.utc)
    last = bars[-1]
    closes_at = last.t.timestamp() + tf_minutes * 60
    if closes_at > now.timestamp() + tolerance_s:
        return bars[:-1], True
    return bars, False


def _finish(rows, source, quality, note, tf_minutes, now=None):
    rows.sort(key=lambda b: b.t)
    dedup = []
    for b in rows:
        if dedup and b.t == dedup[-1].t:
            continue
        dedup.append(b)
    closed, dropped = drop_incomplete(dedup, tf_minutes, now)
    if not closed:
        raise ProviderError("no closed bars returned")
    kept = len(closed)
    if dropped:
        note = (note + " | dropped 1 in-progress candle").strip(" |")
    return closed, {"source": source, "quality": quality, "note": note,
                    "bars": kept, "dropped_incomplete": dropped}


# ------------------------------------------------------------------ crypto venues
def _binance(symbol, tf, limit, host="data-api.binance.vision"):
    url = "https://%s/api/v3/klines?symbol=%s&interval=%s&limit=%d" % (host, symbol.upper(), _TF["binance"][tf], limit)
    d = _json(url)
    if not isinstance(d, list):
        raise ProviderError("unexpected payload: %s" % str(d)[:120])
    rows = [_bar(_ms(k[0]), k[1], k[2], k[3], k[4], k[5]) for k in d]
    return rows, "binance:" + symbol.upper(), "live", "venue spot klines (Binance)"


def _bybit(symbol, tf, limit, **_):
    url = ("https://api.bybit.com/v5/market/kline?category=spot&symbol=%s&interval=%s&limit=%d"
           % (symbol.upper(), _TF["bybit"][tf], min(limit, 1000)))
    d = _json(url)
    if d.get("retCode") not in (0, None):
        raise ProviderError("bybit retCode %s: %s" % (d.get("retCode"), d.get("retMsg")))
    rows = [_bar(_ms(k[0]), k[1], k[2], k[3], k[4], k[5]) for k in d["result"]["list"]]
    return rows, "bybit:" + symbol.upper(), "live", "venue spot klines (Bybit), newest first"


def _okx(symbol, tf, limit, **_):
    url = ("https://www.okx.com/api/v5/market/candles?instId=%s&bar=%s&limit=%d"
           % (symbol.upper(), _TF["okx"][tf], min(limit, 300)))
    d = _json(url)
    if d.get("code") not in ("0", 0, None):
        raise ProviderError("okx code %s: %s" % (d.get("code"), d.get("msg")))
    rows = []
    for k in d["data"]:
        if len(k) > 8 and str(k[8]) == "0":
            continue                      # unconfirmed candle
        rows.append(_bar(_ms(k[0]), k[1], k[2], k[3], k[4], k[5]))
    return rows, "okx:" + symbol.upper(), "live", "venue spot candles (OKX)"


def _coinbase(symbol, tf, limit, **_):
    g = _TF["coinbase"][tf]
    url = "https://api.exchange.coinbase.com/products/%s/candles?granularity=%d" % (symbol.upper(), g)
    d = _json(url)
    if not isinstance(d, list):
        raise ProviderError("unexpected payload: %s" % str(d)[:120])
    rows = [_bar(_s(k[0]), k[3], k[2], k[1], k[4], k[5]) for k in d]   # [t, low, high, open, close, vol]
    return rows, "coinbase:" + symbol.upper(), "live", "venue candles (Coinbase Exchange), max 300"


def _bitstamp(symbol, tf, limit, **_):
    url = "https://www.bitstamp.net/api/v2/ohlc/%s/?step=%d&limit=%d" % (
        symbol.lower(), _TF["bitstamp"][tf], min(limit, 1000))
    d = _json(url)
    rows = [_bar(_s(k["timestamp"]), k["open"], k["high"], k["low"], k["close"], k["volume"])
            for k in d["data"]["ohlc"]]
    return rows, "bitstamp:" + symbol.lower(), "live", "venue OHLC (Bitstamp)"


def _kraken(symbol, tf, limit, **_):
    url = "https://api.kraken.com/0/public/OHLC?pair=%s&interval=%d" % (symbol, _TF["kraken"][tf])
    d = _json(url)
    if d.get("error"):
        raise ProviderError("kraken: %s" % d["error"])
    result = d.get("result", {})
    key = next((k for k in result if k != "last"), None)
    if not key:
        raise ProviderError("kraken: empty result")
    rows = [_bar(_s(k[0]), k[1], k[2], k[3], k[4], k[6]) for k in result[key]]
    return rows, "kraken:%s" % key, "live", "venue OHLC (Kraken); last row is in progress"


def _deribit(symbol, tf, limit, **_):
    now_ms = int(time.time() * 1000)
    start = now_ms - int(limit * tf * 60 * 1000) - 3600_000
    url = ("https://www.deribit.com/api/v2/public/get_tradingview_chart_data"
           "?instrument_name=%s&start_timestamp=%d&end_timestamp=%d&resolution=%d"
           % (symbol.upper(), start, now_ms, _TF["deribit"][tf]))
    d = _json(url)
    r = d.get("result") or {}
    ticks, close, vol = r.get("ticks") or [], r.get("close") or [], r.get("volume") or []
    opens, highs, lows = r.get("open") or [], r.get("high") or [], r.get("low") or []
    rows = []
    for i, t in enumerate(ticks):
        try:
            rows.append(_bar(_ms(t), opens[i], highs[i], lows[i], close[i], vol[i]))
        except IndexError:
            continue
    return rows, "deribit:" + symbol.upper(), "live", "venue chart feed (Deribit perp)"


def _yahoo(symbol, tf, limit, range_="5d", **_):
    url = ("https://query1.finance.yahoo.com/v8/finance/chart/%s?interval=%s&range=%s&includePrePost=false"
           % (symbol, _TF["yahoo"][tf], range_))
    d = _json(url)
    res = d["chart"]["result"][0]
    ts = res.get("timestamp") or []
    q = res["indicators"]["quote"][0]
    rows = []
    has_volume = False
    for i, t in enumerate(ts):
        o, h, l, c = q["open"][i], q["high"][i], q["low"][i], q["close"][i]
        if None in (o, h, l, c):
            continue
        v = (q.get("volume") or [0] * len(ts))[i] or 0
        has_volume = has_volume or v > 0
        rows.append(_bar(_s(t), o, h, l, c, v))
    note = ("Yahoo Finance delayed chart data; exchange tz %s"
            % res.get("meta", {}).get("exchangeTimezoneName"))
    if not has_volume:
        note += " | NO VOLUME for this symbol: the order-flow proxy cannot work, disable require_order_flow"
    q_ = "delayed" if has_volume else "no-volume"
    return rows, "yahoo:" + symbol, q_, note


def _twelvedata(symbol, tf, limit, api_key=None, **_):
    if not api_key:
        raise ProviderError("Twelve Data needs a free API key in the TWELVEDATA_API_KEY environment variable")
    interval = {1: "1min", 5: "5min", 15: "15min", 30: "30min", 60: "1h", 240: "4h"}.get(tf, "5min")
    url = ("https://api.twelvedata.com/time_series?symbol=%s&interval=%s&outputsize=%d&apikey=%s"
           % (symbol, interval, min(limit, 5000), api_key))
    d = _json(url)
    if d.get("status") == "error" or "values" not in d:
        raise ProviderError("twelvedata: %s" % str(d.get("message"))[:140])
    rows = []
    for k in reversed(d["values"]):
        t = datetime.fromisoformat(k["datetime"]).replace(tzinfo=timezone.utc)
        rows.append(core.Bar(t, float(k["open"]), float(k["high"]), float(k["low"]),
                             float(k["close"]), float(k.get("volume") or 0)))
    return rows, "twelvedata:" + symbol, "vendor-agg", "Twelve Data free tier (aggregated FX feed)"


ADAPTERS = {
    "binance": _binance, "bybit": _bybit, "okx": _okx, "coinbase": _coinbase,
    "bitstamp": _bitstamp, "kraken": _kraken, "deribit": _deribit,
    "yahoo": _yahoo, "twelvedata": _twelvedata,
}

FALLBACK_ORDER = ["binance", "bybit", "okx", "coinbase", "kraken", "bitstamp", "deribit", "yahoo"]


def fetch(provider, symbol=None, timeframe_minutes=5, limit=500, api_key=None, yahoo_range="5d"):
    """Fetches and normalises closed bars from one provider. Returns (bars, meta)."""
    if provider not in ADAPTERS:
        raise ProviderError("unknown provider '%s' (choose from %s)" % (provider, ", ".join(sorted(ADAPTERS))))
    sym = symbol or DEFAULT_SYMBOLS.get(provider, "")
    if not sym:
        raise ProviderError("provider '%s' needs an explicit symbol" % provider)
    kw = {"api_key": api_key} if provider == "twelvedata" else {}
    if provider == "yahoo":
        kw["range_"] = yahoo_range
    rows, source, quality, note = ADAPTERS[provider](sym, timeframe_minutes, limit, **kw)
    bars, meta = _finish(rows, source, quality, note, timeframe_minutes)
    meta["provider"] = provider
    meta["symbol"] = sym
    meta["volume_available"] = any(b.v > 0 for b in bars[-50:])
    if not meta["volume_available"]:
        meta["note"] += " | no volume in the last 50 bars"
    return bars, meta


# --------------------------------------------------------------- options positioning
def btc_option_chain(currency="BTC", timeout=DEFAULT_TIMEOUT):
    """Whole options chain in one keyless call: strike, mark_iv, open_interest, expiry.

    Deribit publishes the summary (not the raw greeks) here; the greeks endpoint is per instrument,
    so anything derived below is OI concentration, not computed gamma. Label it as such.
    """
    url = ("https://www.deribit.com/api/v2/public/get_book_summary_by_currency"
           "?currency=%s&kind=option" % currency.upper())
    d = _json(url, timeout=timeout)
    rows = (d.get("result") or [])
    out = []
    for r in rows:
        try:
            strike = float(r["strike_price"])
            oi = float(r.get("open_interest") or 0.0)
            iv = float(r.get("mark_iv") or 0.0)
        except (KeyError, TypeError, ValueError):
            continue
        if strike <= 0 or oi < 0:
            continue
        out.append({"instrument": r.get("instrument_name"), "strike": strike, "oi": oi,
                    "iv": iv, "expiry": r.get("expiration_timestamp"),
                    "type": "call" if str(r.get("instrument_name", "")).endswith("-C") else "put",
                    "underlying": r.get("underlying_price")})
    if not out:
        raise ProviderError("empty options chain for %s" % currency)
    return out


def oi_levels(chain, spot, band=0.25, top=3):
    """Turns a chain into price levels the engine can consume.

    call wall  = strike with the most call open interest
    put wall   = strike with the most put open interest
    flip       = strike where cumulative (call OI - put OI) changes sign
    These are OPEN-INTEREST concentrations. No dealer-gamma sign is claimed.
    """
    lo, hi = spot * (1 - band), spot * (1 + band)
    calls, puts = {}, {}
    for r in chain:
        if not (lo <= r["strike"] <= hi) or r["oi"] <= 0:
            continue
        (calls if r["type"] == "call" else puts)[r["strike"]] = (
            (calls if r["type"] == "call" else puts).get(r["strike"], 0.0) + r["oi"])
    if not calls and not puts:
        raise ProviderError("no open interest inside %.0f%% of spot" % (band * 100))
    levels = []
    strikes = sorted(set(calls) | set(puts))
    if calls:
        k = max(calls, key=lambda s: calls[s])
        levels.append({"price": round(k, 2), "kind": "call_wall", "oi": calls[k]})
    if puts:
        k = max(puts, key=lambda s: puts[s])
        levels.append({"price": round(k, 2), "kind": "put_wall", "oi": puts[k]})
    cum, prev = 0.0, None
    for k in strikes:
        prev = cum
        cum += calls.get(k, 0.0) - puts.get(k, 0.0)
        if prev is not None and prev != 0 and cum != 0 and (prev > 0) != (cum > 0):
            levels.append({"price": round(k, 2), "kind": "gamma_flip", "oi": abs(cum)})
            break
    levels.sort(key=lambda x: abs(x["price"] - spot))
    keep, seen = [], set()
    for lv in levels:
        if lv["kind"] in seen:
            continue
        seen.add(lv["kind"])
        keep.append({k: lv[k] for k in ("price", "kind")})
    return keep[:top], {"strikes": len(strikes), "contracts": len(chain),
                        "total_call_oi": sum(calls.values()), "total_put_oi": sum(puts.values())}
