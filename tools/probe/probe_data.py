import lzma, struct, sys, urllib.request, statistics

def fetch(url):
    req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0 (research)"})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return r.status, r.read()
    except Exception as e:
        return getattr(e, "code", "ERR"), str(e).encode()

def decompress_bi5(data):
    # Dukascopy .bi5 = LZMA (alone or raw) of fixed-size records
    try:
        return lzma.decompress(data, format=lzma.FORMAT_ALONE)
    except Exception:
        pass
    p = data[0]; lc = p % 9; p //= 9; lp = p % 5; pb = p // 5
    dict_size = int.from_bytes(data[1:5], "little")
    return lzma.decompress(data[13:], format=lzma.FORMAT_RAW,
                           filters=[{"id": lzma.FILTER_LZMA1, "dict_size": dict_size, "lc": lc, "lp": lp, "pb": pb}])

for (y, m) in [(2024, 0), (2024, 5), (2025, 0)]:
    for kind in ["BID", "ASK"]:
        url = f"https://datafeed.dukascopy.com/datafeed/XAUUSD/{y}/{m:02d}/{kind}_candles_min_1.bi5"
        st, body = fetch(url)
        print(f"URL {url} status={st} bytes={len(body) if isinstance(body, bytes) else body}")
        if st != 200: continue
        try:
            raw = decompress_bi5(body)
        except Exception as e:
            print("  DECOMPRESS FAILED", type(e).__name__, e); continue
        n = len(raw) // 24
        print(f"  records={n} mod={len(raw)%24}")
        if n:
            rows = [struct.unpack(">IIIIf", raw[i*24:(i+1)*24]) for i in range(n)]
            print("  first", rows[0], "last", rows[-1])
            mids = [(r[1] + r[2]) / 2 for r in rows[:5000]]
            print("  median_raw_price", statistics.median(mids), " -> /1000 =", statistics.median(mids) / 1000)

# probe alternate paths
for path in ["/XAUUSD/2024/00/01/BID_candles_min_1.bi5", "/XAUUSD/2024/00/01/00h_ticks.bi5",
             "/XAUUSD/2024/00/BID_candles_hour_1.bi5", "/XAUUSD/2024/00/BID_candles_day_1.bi5"]:
    st, body = fetch("https://datafeed.dukascopy.com/datafeed" + path)
    print(f"ALT {path} status={st} bytes={len(body) if isinstance(body, bytes) else body}")
