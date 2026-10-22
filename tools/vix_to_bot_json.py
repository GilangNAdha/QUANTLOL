#!/usr/bin/env python3
"""
Converts the committed Cboe VIX daily history into the JSON file the cBot reads.

    python3 tools/vix_to_bot_json.py --out research/samples/vix.json
    python3 tools/vix_to_bot_json.py --date 2026-10-20 --out /path/for/cbot/vix.json

The timestamp follows the Cboe convention: the daily close is stamped 20:15Z
(16:15 New York in summer). It is a DAILY value - the bot is told that through
"quality": "delayed" so nobody mistakes it for a live quote.
"""
import argparse, gzip, json, os
from datetime import datetime, timezone, timedelta

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_CSV = os.path.join(HERE, "..", "research", "data", "vix_1d.csv.gz")


def read_last(path, want=None):
    """Returns ((date, level), previous_row) for `want`, or the final row when want is None."""
    last = None
    prev = None
    with gzip.open(path, "rt") as f:
        f.readline()
        for line in f:
            p = line.strip().split(",")
            if len(p) < 5:
                continue
            d = datetime.strptime(p[0][:10], "%Y-%m-%d").date()
            row = (d, float(p[4]))
            if want is not None:
                if d == want:
                    return row, prev
                if d < want:
                    prev = row
                continue
            prev = last
            last = row
    return (None, None) if want is not None else (last, prev)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--csv", default=DEFAULT_CSV)
    ap.add_argument("--date", help="YYYY-MM-DD (default: latest row)")
    ap.add_argument("--out", default="research/samples/vix.json")
    a = ap.parse_args()

    want = datetime.strptime(a.date, "%Y-%m-%d").date() if a.date else None
    row, prev = read_last(a.csv, want)
    if row is None or row[0] is None:
        raise SystemExit("no VIX row found for %s" % (want or "latest"))

    d, level = row
    epoch = datetime(d.year, d.month, d.day, 20, 15, tzinfo=timezone.utc)
    doc = {
        "vix": round(level, 2),
        "as_of": epoch.strftime("%Y-%m-%dT%H:%M:%SZ"),
        "quality": "delayed",
        "source": "Cboe VIX_History.csv (daily close, T+1)",
        "note": "Daily close. Broad context only.",
    }
    if prev and prev[0] < d:
        doc["change_1d"] = round(level - prev[1], 2)

    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    with open(a.out, "w") as f:
        json.dump(doc, f, indent=2)
    print("wrote %s -> VIX %.2f as of %s" % (a.out, level, doc["as_of"]))


if __name__ == "__main__":
    main()
