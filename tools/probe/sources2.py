"""Second data-source probe: isolate Dukascopy path/availability with a EURUSD control."""
import gzip, json, lzma, struct, time, urllib.request, urllib.error

UA = "Mozilla/5.0 (X11; Linux x86_64) research-quantlol/1.0"
def get(url, headers=None, timeout=40):
    h = {"User-Agent": UA, "Accept": "*/*"}
    if headers: h.update(headers)
    try:
        req = urllib.request.Request(url, headers=h)
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, r.read()
    except urllib.error.HTTPError as e:
        return e.code, b""
    except Exception as e:
        return "ERR", f"{type(e).__name__}: {e}".encode()

def bi5_info(body, kind):
    if not body: return "empty"
    for name, fn in (("alone", lambda b: lzma.decompress(b, format=lzma.FORMAT_ALONE)),):
        try:
            raw = fn(body); return f"{name}: {len(raw)} bytes, {len(raw)//24} recs, first={struct.unpack('>IIIIf', raw[:24])}"
        except Exception as e: pass
    try:
        p = body[0]; lc = p % 9; p //= 9; lp = p % 5; pb = p // 5
        raw = lzma.decompress(body[13:], format=lzma.FORMAT_RAW,
              filters=[{"id": lzma.FILTER_LZMA1, "dict_size": int.from_bytes(body[1:5],"little"), "lc": lc, "lp": lp, "pb": pb}])
        return f"raw_lzma1: {len(raw)} bytes, {len(raw)//24} recs, first={struct.unpack('>IIIIf', raw[:24])}"
    except Exception as e:
        return f"decode_fail({type(e).__name__}) len={len(body)} head={body[:24].hex()}"

print("=== DUKASCOPY CONTROL: EURUSD ===")
for u in ["https://datafeed.dukascopy.com/datafeed/EURUSD/2024/00/BID_candles_min_1.bi5",
          "https://datafeed.dukascopy.com/datafeed/EURUSD/2024/00/01/BID_candles_min_1.bi5",
          "https://datafeed.dukascopy.com/datafeed/EURUSD/2024/00/BID_candles_day_1.bi5"]:
    st, b = get(u); print(f"  {u.split('datafeed/')[1]} status={st} bytes={len(b)} {bi5_info(b,'BID')[:120]}")

print("=== DUKASCOPY XAUUSD VARIANTS ===")
cands = [
 "https://datafeed.dukascopy.com/datafeed/XAUUSD/2024/00/BID_candles_min_1.bi5",
 "https://datafeed.dukascopy.com/datafeed/XAUUSD/2024/00/15/BID_candles_min_1.bi5",
 "https://datafeed.dukascopy.com/datafeed/XAUUSD/2024/00/BID_candles_day_1.bi5",
 "https://datafeed.dukascopy.com/datafeed/XAUUSD/2024/00/BID_candles_hour_1.bi5",
 "https://datafeed.dukascopy.com/datafeed/XAUUSD/2024/00/15/10h_ticks.bi5",
 "https://datafeed.dukascopy.com/datafeed/XAGUSD/2024/00/BID_candles_min_1.bi5",
]
for u in cands:
    st, b = get(u, headers={"Referer": "https://www.dukascopy.com/swiss/english/marketwatch/historical/"})
    print(f"  {u.split('datafeed/')[1]} status={st} bytes={len(b)} {bi5_info(b,'BID')[:130]}")

print("=== DUKASCOPY freeserv chart JSON ===")
for inst in ("XAU/USD", "EUR/USD"):
    u = ("https://freeserv.dukascopy.com/2.0/?path=chart/json3&instrument=" + inst +
         "&offer_side=B&interval=1MIN&splits=true&stocks=true&time_direction=P&timestamp=" + str(int(time.time()*1000)) + "&jsonp=")
    st, b = get(u); print(f"  {inst} status={st} bytes={len(b)} head={b[:220]!r}")

print("=== YAHOO ranges ===")
for rng, iv in (("60d","5m"),("7d","1m"),("730d","1h"),("10y","1d")):
    u = f"https://query1.finance.yahoo.com/v8/finance/chart/GC%3DF?interval={iv}&range={rng}"
    st, b = get(u)
    n = "?"
    if st == 200:
        try:
            j = json.loads(b); res = j["chart"]["result"][0]; n = len(res["timestamp"])
        except Exception: pass
    print(f"  GC=F {iv}/{rng} status={st} bytes={len(b)} bars={n}")
