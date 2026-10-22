"""Probe free/legitimate XAUUSD data sources from CI (full internet). No secrets needed."""
import gzip, io, json, lzma, struct, sys, time, urllib.request, urllib.error

UA = "Mozilla/5.0 (X11; Linux x86_64) research-quantlol/1.0"

def get(url, headers=None, timeout=45, retries=3, backoff=2.0):
    h = {"User-Agent": UA, "Accept": "*/*", "Accept-Encoding": "gzip"}
    if headers: h.update(headers)
    last = None
    for i in range(retries):
        try:
            req = urllib.request.Request(url, headers=h)
            with urllib.request.urlopen(req, timeout=timeout) as r:
                body = r.read()
                if r.headers.get("Content-Encoding") == "gzip":
                    try: body = gzip.decompress(body)
                    except Exception: pass
                return r.status, body
        except urllib.error.HTTPError as e:
            last = f"HTTP {e.code}"
            if e.code in (429, 503, 502, 500) and i < retries - 1:
                time.sleep(backoff ** i); continue
            return e.code, b""
        except Exception as e:
            last = f"{type(e).__name__}: {e}"
            if i < retries - 1: time.sleep(backoff ** i)
    return "ERR", str(last).encode()

def head(url, n=200):
    st, body = get(url)
    sample = body[:n].decode("utf-8", "replace").replace("\n", " | ")[:n]
    return f"  status={st} bytes={len(body) if isinstance(body,bytes) else body} sample={sample!r}"

def fxcmpaths():
    for y in (2014, 2018, 2020, 2022, 2024, 2025):
        for tf in ("m1", "m15", "H1", "D1"):
            yield f"https://candledata.fxcorporate.com/{tf}/XAUUSD/{y}/1.csv.gz"

def main():
    print("=== FXCM candledata (public, free) ===")
    for u in fxcmpaths():
        st, body = get(u, retries=2)
        extra = ""
        if st == 200 and body[:2] == b"\x1f\x8b":
            try:
                txt = gzip.decompress(body).decode("utf-8", "replace")
                lines = txt.splitlines()
                extra = f" rows={len(lines)} first={lines[0][:80]!r} last={lines[-1][:80]!r}"
            except Exception as e:
                extra = f" gunzip_fail={e}"
        print(f"FXCM {u.split('/')[-3]}/{u.split('/')[-2]}/{u.split('/')[-1]} status={st} bytes={len(body) if isinstance(body,bytes) else body}{extra}")

    print("\n=== Dukascopy (retry+backoff) ===")
    for y, m in ((2024, 0), (2023, 5)):
        for kind in ("BID", "ASK"):
            u = f"https://datafeed.dukascopy.com/datafeed/XAUUSD/{y}/{m:02d}/{kind}_candles_min_1.bi5"
            st, body = get(u, headers={"Referer": "https://www.dukascopy.com/"}, retries=4)
            extra = ""
            if st == 200:
                try:
                    raw = lzma.decompress(body, format=lzma.FORMAT_ALONE)
                    extra = f" decompressed={len(raw)} records={len(raw)//24} first={struct.unpack('>IIIIf', raw[:24])}"
                except Exception as e:
                    extra = f" lzma_alone_fail={type(e).__name__}"
            print(f"DUKA {y}/{m:02d}/{kind} status={st} bytes={len(body) if isinstance(body,bytes) else body}{extra}")

    print("\n=== Yahoo (COMEX GC=F continuous futures, free delayed) ===")
    u = "https://query1.finance.yahoo.com/v8/finance/chart/GC=F?interval=1h&range=730d"
    st, body = get(u, retries=2)
    print(f"  status={st} bytes={len(body) if isinstance(body,bytes) else body}")
    if st == 200:
        try:
            j = json.loads(body); res = j["chart"]["result"][0]
            ts = res["timestamp"]; q = res["indicators"]["quote"][0]
            print(f"  bars={len(ts)} start={time.strftime('%Y-%m-%d', time.gmtime(ts[0]))} end={time.strftime('%Y-%m-%d', time.gmtime(ts[-1]))} keylen={len(q['close'])}")
        except Exception as e:
            print("  parse_fail", e, body[:300])

    print("\n=== Stooq daily XAUUSD ===")
    st, body = get("https://stooq.com/q/d/l/?s=xauusd&i=d")
    print(f"  status={st} bytes={len(body) if isinstance(body,bytes) else body} head={body[:120]!r}")

    print("\n=== FRED (no key endpoints for gold: LBMA) ===")
    st, body = get("https://fred.stlouisfed.org/graph/fredgraph.csv?id=GOLDPMGBD228NLBM")
    print(f"  status={st} bytes={len(body) if isinstance(body,bytes) else body} tail={body[-160:]!r}")

    print("\n=== VIX history (CBOE public csv) ===")
    st, body = get("https://cdn.cboe.com/api/global/us_indices/daily_prices/VIX_History.csv")
    print(f"  status={st} bytes={len(body) if isinstance(body,bytes) else body} head={body[:120]!r} tail={body[-120:]!r}")

if __name__ == "__main__":
    main()
