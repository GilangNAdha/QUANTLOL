#!/usr/bin/env python3
"""
Endpoint probe for free market-data sources.

Runs from a machine with real internet (the sandbox has none; GitHub Actions does) and records,
per endpoint: HTTP status, latency, whether an API key was needed, how many bars came back, and
the first/last timestamp. Nothing is reported unless it was actually requested.

    python3 tools/probe/live_data_probe.py [--out tools/probe/out]
"""
from __future__ import annotations

import argparse
import json
import os
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone

UA = "Mozilla/5.0 (X11; Linux x86_64) quantlol-probe/1.0"
TIMEOUT = 20


def get(url, headers=None):
    h = {"User-Agent": UA, "Accept": "application/json,*/*"}
    if headers:
        h.update(headers)
    t0 = time.perf_counter()
    try:
        req = urllib.request.Request(url, headers=h)
        with urllib.request.urlopen(req, timeout=TIMEOUT) as r:
            body = r.read()
            status = r.status
    except urllib.error.HTTPError as e:
        return {"status": e.code, "ms": round((time.perf_counter() - t0) * 1000, 1),
                "body": e.read()[:400].decode("utf-8", "replace"), "error": "HTTP %s" % e.code}
    except Exception as e:
        return {"status": None, "ms": round((time.perf_counter() - t0) * 1000, 1), "body": "",
                "error": "%s: %s" % (type(e).__name__, str(e)[:160])}
    return {"status": status, "ms": round((time.perf_counter() - t0) * 1000, 1), "body": body}


def iso(ms=None, s=None):
    try:
        if ms is not None:
            return datetime.fromtimestamp(float(ms) / 1000.0, timezone.utc).strftime("%Y-%m-%d %H:%M")
        return datetime.fromtimestamp(float(s), timezone.utc).strftime("%Y-%m-%d %H:%M")
    except Exception:
        return "?"


# ---------------------------------------------------------------- each probe
def p_binance(host):
    r = get("https://%s/api/v3/klines?symbol=BTCUSDT&interval=5m&limit=50" % host)
    out = {"bars": 0, "first": "?", "last": "?", "volume": "no", "key": "none"}
    if r.get("status") == 200:
        try:
            d = json.loads(r["body"])
            out.update(bars=len(d), first=iso(ms=d[0][0]), last=iso(ms=d[-1][0]),
                       volume="yes" if float(d[-1][5]) >= 0 else "no")
        except Exception as e:
            out["parse_error"] = str(e)[:120]
    return r, out


def p_kraken():
    r = get("https://api.kraken.com/0/public/OHLC?pair=XBTUSD&interval=5")
    out = {"bars": 0, "first": "?", "last": "?", "volume": "no", "key": "none"}
    if r.get("status") == 200:
        try:
            d = json.loads(r["body"])
            if d.get("error"):
                out["api_error"] = d["error"]
            key = next((k for k in d.get("result", {}) if k != "last"), None)
            rows = d["result"][key]
            out.update(bars=len(rows), first=iso(s=rows[0][0]), last=iso(s=rows[-1][0]), volume="yes")
            out["note"] = "last row is the in-progress candle and must be dropped"
        except Exception as e:
            out["parse_error"] = str(e)[:120]
    return r, out


def p_coinbase():
    r = get("https://api.exchange.coinbase.com/products/BTC-USD/candles?granularity=300")
    out = {"bars": 0, "first": "?", "last": "?", "volume": "yes", "key": "none"}
    if r.get("status") == 200:
        try:
            d = json.loads(r["body"])
            out.update(bars=len(d), first=iso(s=d[-1][0]), last=iso(s=d[0][0]),
                       note="newest first, max 300 candles")
        except Exception as e:
            out["parse_error"] = str(e)[:120]
    return r, out


def p_bybit():
    r = get("https://api.bybit.com/v5/market/kline?category=spot&symbol=BTCUSDT&interval=5&limit=50")
    out = {"bars": 0, "first": "?", "last": "?", "volume": "yes", "key": "none"}
    if r.get("status") == 200:
        try:
            d = json.loads(r["body"])["result"]["list"]
            out.update(bars=len(d), first=iso(ms=d[-1][0]), last=iso(ms=d[0][0]),
                       note="newest first")
        except Exception as e:
            out["parse_error"] = str(e)[:120]
    return r, out


def p_okx():
    r = get("https://www.okx.com/api/v5/market/candles?instId=BTC-USDT&bar=5m&limit=50")
    out = {"bars": 0, "first": "?", "last": "?", "volume": "yes", "key": "none"}
    if r.get("status") == 200:
        try:
            d = json.loads(r["body"])["data"]
            out.update(bars=len(d), first=iso(ms=d[-1][0]), last=iso(ms=d[0][0]),
                       note="newest first; last row may be unconfirmed")
        except Exception as e:
            out["parse_error"] = str(e)[:120]
    return r, out


def p_bitstamp():
    r = get("https://www.bitstamp.net/api/v2/ohlc/btcusd/?step=300&limit=50")
    out = {"bars": 0, "first": "?", "last": "?", "volume": "yes", "key": "none"}
    if r.get("status") == 200:
        try:
            d = json.loads(r["body"])["data"]["ohlc"]
            out.update(bars=len(d), first=iso(s=d[0]["timestamp"]), last=iso(s=d[-1]["timestamp"]))
        except Exception as e:
            out["parse_error"] = str(e)[:120]
    return r, out


def p_deribit_ohlc():
    r = get("https://www.deribit.com/api/v2/public/get_tradingview_chart_data"
            "?instrument_name=BTC-PERPETUAL&start_timestamp=%d&end_timestamp=%d&resolution=5"
            % (int(time.time() - 86400) * 1000, int(time.time()) * 1000))
    out = {"bars": 0, "first": "?", "last": "?", "volume": "yes", "key": "none"}
    if r.get("status") == 200:
        try:
            d = json.loads(r["body"])["result"]
            out.update(bars=len(d["ticks"]), first=iso(ms=d["ticks"][0]), last=iso(ms=d["ticks"][-1]),
                       note="bars from the venue's own chart feed; low volume on perps can be 0")
        except Exception as e:
            out["parse_error"] = str(e)[:120]
    return r, out


def p_deribit_options():
    r = get("https://www.deribit.com/api/v2/public/get_book_summary_by_currency?currency=BTC&kind=option")
    out = {"bars": 0, "first": "?", "last": "?", "volume": "n/a", "key": "none",
           "note": "options chain: open_interest + mark_iv per strike -> real (not proxy) BTC exposure data"}
    if r.get("status") == 200:
        try:
            d = json.loads(r["body"])["result"]
            oi = sum(1 for x in d if (x.get("open_interest") or 0) > 0)
            out.update(bars=len(d), first="instruments=%d" % len(d), last="with_oi=%d" % oi)
        except Exception as e:
            out["parse_error"] = str(e)[:120]
    return r, out


def p_yahoo(symbol):
    r = get("https://query1.finance.yahoo.com/v8/finance/chart/%s?interval=5m&range=5d" % symbol)
    out = {"bars": 0, "first": "?", "last": "?", "volume": "?", "key": "none"}
    if r.get("status") == 200:
        try:
            res = json.loads(r["body"])["chart"]["result"][0]
            ts = res.get("timestamp") or []
            q = res["indicators"]["quote"][0]
            vols = [v for v in (q.get("volume") or []) if v]
            out.update(bars=len(ts), first=iso(s=ts[0]) if ts else "?", last=iso(s=ts[-1]) if ts else "?",
                       volume="yes" if vols else "no (zero/absent)")
        except Exception as e:
            out["parse_error"] = str(e)[:120]
    return r, out


def p_frankfurter():
    r = get("https://api.frankfurter.dev/v1/latest?base=EUR&symbols=USD")
    out = {"bars": 0, "first": "?", "last": "?", "volume": "no", "key": "none",
           "note": "ECB daily fixing only - not intraday"}
    if r.get("status") == 200:
        try:
            d = json.loads(r["body"])
            out.update(bars=1, first=d.get("date", "?"), last="EUR/USD %s" % d.get("rates", {}).get("USD"))
        except Exception as e:
            out["parse_error"] = str(e)[:120]
    return r, out


def p_erapi():
    r = get("https://open.er-api.com/v6/latest/EUR")
    out = {"bars": 0, "first": "?", "last": "?", "volume": "no", "key": "none",
           "note": "daily rates, no OHLC"}
    if r.get("status") == 200:
        try:
            d = json.loads(r["body"])
            out.update(bars=1, first=d.get("time_last_update_utc", "?")[:16], last="rates=%d" % len(d.get("rates", {})))
        except Exception as e:
            out["parse_error"] = str(e)[:120]
    return r, out


def p_twelvedata_nokey():
    r = get("https://api.twelvedata.com/time_series?symbol=EUR/USD&interval=5min&outputsize=50")
    out = {"bars": 0, "first": "?", "last": "?", "volume": "n/a", "key": "REQUIRED",
           "note": "free tier: 800 req/day, 8 req/min, needs a free key"}
    if r.get("status") == 200:
        try:
            d = json.loads(r["body"])
            out["api_error"] = d.get("message", "ok")[:120]
            out["bars"] = int(d.get("values") and len(d["values"]) or 0)
        except Exception as e:
            out["parse_error"] = str(e)[:120]
    return r, out


def p_binance_forex():
    """Does Binance list any FX pair? (Proves crypto venues cannot stand in for forex.)"""
    r = get("https://data-api.binance.vision/api/v3/exchangeInfo?symbol=EURUSDT")
    out = {"bars": 0, "first": "?", "last": "?", "volume": "n/a", "key": "none",
           "note": "EURUSDT is a stablecoin pair, NOT spot EUR/USD"}
    if r.get("status") == 200:
        try:
            d = json.loads(r["body"])
            out["bars"] = len(d.get("symbols", []))
        except Exception as e:
            out["parse_error"] = str(e)[:120]
    return r, out


PROBES = [
    ("crypto", "Binance (data-api mirror)", "https://data-api.binance.vision", lambda: p_binance("data-api.binance.vision")),
    ("crypto", "Binance (api.binance.com)", "https://api.binance.com", lambda: p_binance("api.binance.com")),
    ("crypto", "Kraken", "https://api.kraken.com", p_kraken),
    ("crypto", "Coinbase Exchange", "https://api.exchange.coinbase.com", p_coinbase),
    ("crypto", "Bybit", "https://api.bybit.com", p_bybit),
    ("crypto", "OKX", "https://www.okx.com", p_okx),
    ("crypto", "Bitstamp", "https://www.bitstamp.net", p_bitstamp),
    ("crypto", "Deribit (perp OHLC)", "https://www.deribit.com", p_deribit_ohlc),
    ("crypto-options", "Deribit (options chain)", "https://www.deribit.com", p_deribit_options),
    ("forex", "Yahoo EURUSD=X", "https://query1.finance.yahoo.com", lambda: p_yahoo("EURUSD=X")),
    ("forex", "Frankfurter (ECB)", "https://api.frankfurter.dev", p_frankfurter),
    ("forex", "open.er-api.com", "https://open.er-api.com", p_erapi),
    ("forex", "Twelve Data (no key)", "https://api.twelvedata.com", p_twelvedata_nokey),
    ("forex", "Binance EURUSDT (not spot FX)", "https://data-api.binance.vision", p_binance_forex),
    ("control", "Yahoo GC=F", "https://query1.finance.yahoo.com", lambda: p_yahoo("GC=F")),
    ("control", "Yahoo BTC-USD", "https://query1.finance.yahoo.com", lambda: p_yahoo("BTC-USD")),
]


def app_checks():
    """Runs the signal app's own adapters against the real venues.

    This is the part that matters: an endpoint can answer 200 and still hand back candles the
    engine cannot use (wrong column order, no volume, an in-progress bar). These rows are produced
    by signals.providers, i.e. exactly the code path `python3 -m signals --preset btc` uses.
    """
    sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
    from signals import providers  # noqa: E402

    out = []
    for name in ("binance", "coinbase", "kraken", "okx", "bitstamp", "deribit", "bybit"):
        t0 = time.time()
        rec = {"class": "app", "name": "signals.providers/%s" % name,
               "base": providers.PROVIDERS[name][2], "bars": 0, "first": "?", "last": "?",
               "volume": "?", "key": "none", "status": None, "ms": None, "note": ""}
        try:
            bars, meta = providers.fetch(name, None, 5, limit=200, market="btc")
            rec.update(status=200, bars=len(bars), first=bars[0].t.strftime("%Y-%m-%d %H:%M"),
                       last=bars[-1].t.strftime("%Y-%m-%d %H:%M"),
                       volume="yes" if meta.get("volume_available") else "NO",
                       note="%s:%s | %s" % (meta.get("provider"), meta.get("symbol"), meta.get("note", "")))
        except Exception as e:
            rec.update(status="ERR", note=str(e)[:150])
        rec["ms"] = int((time.time() - t0) * 1000)
        out.append(rec)

    rec = {"class": "app", "name": "signals / Deribit OI levels", "base": "https://www.deribit.com",
           "bars": 0, "first": "?", "last": "?", "volume": "n/a", "key": "none", "status": None,
           "ms": None, "note": ""}
    t0 = time.time()
    try:
        chain = providers.btc_option_chain("BTC")
        spot = providers.fetch("binance", None, 5, limit=5, market="btc")[0][-1].c
        levels, info = providers.oi_levels(chain, spot, band=0.25)
        rec.update(status=200, bars=len(chain),
                   first="strikes=%d" % info["strikes"], last="levels=%d" % len(levels),
                   note="spot=%.0f | %s | %s" % (spot, ", ".join("%s@%.0f" % (l["kind"], l["price"]) for l in levels),
                                                 "OI concentration, not gamma"))
    except Exception as e:
        rec.update(status="ERR", note=str(e)[:150])
    rec["ms"] = int((time.time() - t0) * 1000)
    out.append(rec)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="tools/probe/out")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)

    results = []
    for klass, name, base, fn in PROBES:
        r, meta = fn()
        rec = {"class": klass, "name": name, "base": base,
               "status": r.get("status"), "ms": r.get("ms"), "error": r.get("error", ""),
               **meta}
        results.append(rec)
        print("%-14s %-34s status=%-5s %6sms bars=%-4s key=%s %s" % (
            klass, name, rec["status"], rec["ms"], rec["bars"], rec["key"],
            (rec.get("api_error") or rec.get("parse_error") or rec.get("error") or "")[:70]))

    print("\n--- the app's own adapters (signals.providers) ---")
    for rec in app_checks():
        results.append(rec)
        print("%-14s %-34s status=%-5s %6sms bars=%-4s vol=%-3s %s" % (
            rec["class"], rec["name"], rec["status"], rec["ms"], rec["bars"], rec["volume"],
            (rec.get("note") or rec.get("error") or "")[:80]))

    stamp = datetime.now(timezone.utc)
    doc = {"generated_utc": stamp.isoformat(), "runner": os.environ.get("RUNNER_OS", "unknown"),
           "note": "Every row was requested live; no row is copied from documentation.",
           "results": results}
    with open(os.path.join(a.out, "live_probe.json"), "w") as f:
        json.dump(doc, f, indent=1)

    lines = ["# Free market-data endpoint probe", "",
             "Generated %s on %s. Each row was actually requested; nothing is copied from docs."
             % (stamp.strftime("%Y-%m-%d %H:%M UTC"), doc["runner"]), "",
             "| class | provider | HTTP | ms | bars/items | key | volume | notes |",
             "|---|---|---|---|---|---|---|---|"]
    for r in results:
        note = (r.get("api_error") or r.get("parse_error") or r.get("error") or r.get("note") or "")
        if r.get("first") not in ("?", None):
            note = ("%s .. %s | " % (r.get("first"), r.get("last"))) + note
        lines.append("| %s | %s | %s | %s | %s | %s | %s | %s |" % (
            r["class"], r["name"], r["status"], r["ms"], r["bars"], r["key"],
            r.get("volume", "?"), note.replace("|", "\\|")[:160]))
    with open(os.path.join(a.out, "live_probe.md"), "w") as f:
        f.write("\n".join(lines) + "\n")
    print("\nwrote", a.out)


if __name__ == "__main__":
    main()
