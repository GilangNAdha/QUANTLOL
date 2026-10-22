#!/usr/bin/env python3
"""
Free, reproducible market-data acquisition for QuantLOL research.

Sources (no API key, no paid subscription):
  * Yahoo Finance chart API  : COMEX gold futures continuous front month (GC=F),
                               spot XAUUSD (XAUUSD=X) where available.
  * CBOE public CSV          : VIX daily history.
  * Optional: a broker export dropped into research/data/broker/ (cTrader "Export to CSV"),
              which the pipeline prefers when present.

Every file is normalised to:  time_utc,open,high,low,close,volume
and written gzip-compressed with a provenance sidecar JSON.

Usage:  python3 research/fetch_data.py [--out research/data]
"""
import argparse, gzip, json, os, sys, time, urllib.request, urllib.error
from datetime import datetime, timezone

UA = "Mozilla/5.0 (X11; Linux x86_64) quantlol-research/1.0"

def get(url, retries=3, timeout=60, backoff=2.0):
    last = None
    for i in range(retries):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": UA, "Accept": "*/*"})
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return r.read()
        except urllib.error.HTTPError as e:
            last = "HTTP %s" % e.code
            if e.code in (429, 500, 502, 503) and i < retries - 1:
                time.sleep(backoff ** i); continue
            break
        except Exception as e:
            last = "%s: %s" % (type(e).__name__, e)
            if i < retries - 1: time.sleep(backoff ** i)
    raise RuntimeError("GET failed for %s (%s)" % (url, last))

def yahoo(symbol, interval, rng):
    url = ("https://query1.finance.yahoo.com/v8/finance/chart/%s?interval=%s&range=%s"
           "&includePrePost=false&events=div%%2Csplits" % (symbol, interval, rng))
    raw = json.loads(get(url))
    res = raw["chart"]["result"][0]
    ts = res.get("timestamp") or []
    q = res["indicators"]["quote"][0]
    rows = []
    for i, t in enumerate(ts):
        o, h, l, c, v = q["open"][i], q["high"][i], q["low"][i], q["close"][i], q["volume"][i]
        if None in (o, h, l, c): continue
        rows.append((int(t), float(o), float(h), float(l), float(c), float(v or 0)))
    meta = res.get("meta", {})
    provenance = {
        "source": "Yahoo Finance chart API",
        "url": url,
        "symbol": symbol,
        "interval": interval,
        "requested_range": rng,
        "exchange_timezone": meta.get("exchangeTimezoneName"),
        "gmtoffset": meta.get("gmtoffset"),
        "regular_market_price": meta.get("regularMarketPrice"),
        "rows": len(rows),
        "first": fmt(rows[0][0]) if rows else None,
        "last": fmt(rows[-1][0]) if rows else None,
        "fetched_utc": datetime.now(timezone.utc).isoformat(),
        "note": "Delayed/free data. GC=F is the COMEX front-month continuous series, not spot XAUUSD."
    }
    return rows, provenance

def fmt(epoch): return datetime.fromtimestamp(int(epoch), timezone.utc).strftime("%Y-%m-%d %H:%M:%S")

def write_csv_gz(path, rows):
    tmp = path + ".tmp"
    with gzip.open(tmp, "wt", newline="") as f:
        f.write("time_utc,open,high,low,close,volume\n")
        for r in rows:
            f.write("%s,%.6f,%.6f,%.6f,%.6f,%.6f\n" % (fmt(r[0]), r[1], r[2], r[3], r[4], r[5]))
    os.replace(tmp, path)

def cboe_vix():
    raw = get("https://cdn.cboe.com/api/global/us_indices/daily_prices/VIX_History.csv")
    lines = raw.decode("utf-8", "replace").strip().splitlines()
    rows = []
    for line in lines[1:]:
        p = line.split(",")
        if len(p) < 5: continue
        try:
            d = datetime.strptime(p[0], "%m/%d/%Y").replace(tzinfo=timezone.utc)
        except ValueError:
            continue
        rows.append((int(d.timestamp()), float(p[1]), float(p[2]), float(p[3]), float(p[4]), 0.0))
    return rows, {"source": "Cboe VIX_History.csv (public)", "rows": len(rows),
                  "first": fmt(rows[0][0]), "last": fmt(rows[-1][0]), "fetched_utc": datetime.now(timezone.utc).isoformat()}

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="research/data")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)
    os.makedirs(os.path.join(args.out, "broker"), exist_ok=True)

    jobs = [
        ("gc_5m",        "GC%3DF",   "5m",  "60d"),
        ("gc_60m",       "GC%3DF",   "60m", "730d"),
        ("gc_1d",        "GC%3DF",   "1d",  "10y"),
        ("xauusd_5m",    "XAUUSD%3DX", "5m", "60d"),
        ("xauusd_60m",   "XAUUSD%3DX", "60m", "730d"),
        ("xauusd_1d",    "XAUUSD%3DX", "1d", "10y"),
    ]
    manifest = {"fetched_utc": datetime.now(timezone.utc).isoformat(), "datasets": {}}
    for name, sym, interval, rng in jobs:
        try:
            rows, prov = yahoo(sym, interval, rng)
            if len(rows) < 50:
                print("[skip] %s: only %d rows" % (name, len(rows))); continue
            write_csv_gz(os.path.join(args.out, name + ".csv.gz"), rows)
            prov["file"] = name + ".csv.gz"
            manifest["datasets"][name] = prov
            print("[ok] %-12s %6d rows  %s -> %s" % (name, len(rows), prov["first"], prov["last"]))
        except Exception as e:
            print("[fail] %s: %s" % (name, e))

    try:
        rows, prov = cboe_vix()
        write_csv_gz(os.path.join(args.out, "vix_1d.csv.gz"), rows)
        prov["file"] = "vix_1d.csv.gz"
        manifest["datasets"]["vix_1d"] = prov
        print("[ok] vix_1d       %6d rows  %s -> %s" % (len(rows), prov["first"], prov["last"]))
    except Exception as e:
        print("[fail] vix: %s" % e)

    with open(os.path.join(args.out, "manifest.json"), "w") as f:
        json.dump(manifest, f, indent=2, sort_keys=True)
    print("wrote", os.path.join(args.out, "manifest.json"))

if __name__ == "__main__":
    main()
