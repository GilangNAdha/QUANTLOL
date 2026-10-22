"""Append-only persistence: signals, evaluations, state snapshot. Stdlib only, no database."""
from __future__ import annotations

import csv
import json
import os
import threading
from datetime import datetime, timezone

SIGNAL_FIELDS = ["generated_utc", "bar_time_utc", "state", "direction", "setup", "session",
                 "price", "entry", "stop", "target", "risk_usd", "rr_net", "score",
                 "stop_ticks", "target_ticks", "synthetic_target", "evidence",
                 "vix_status", "gex_status", "data_quality", "engine_ms"]


class Store:
    def __init__(self, out_dir):
        self.dir = out_dir
        os.makedirs(out_dir, exist_ok=True)
        self.signals_path = os.path.join(out_dir, "signals.jsonl")
        self.evals_path = os.path.join(out_dir, "evaluations.jsonl")
        self.csv_path = os.path.join(out_dir, "signals.csv")
        self.state_path = os.path.join(out_dir, "state.json")
        self._lock = threading.Lock()

    # ------------------------------------------------------------------ append
    def append_signal(self, rec):
        with self._lock:
            self._append_jsonl(self.signals_path, rec)
            self._append_csv(rec)

    def append_evaluation(self, rec):
        with self._lock:
            self._append_jsonl(self.evals_path, rec)

    def _append_jsonl(self, path, rec):
        with open(path, "a", encoding="utf-8") as f:
            f.write(json.dumps(rec, separators=(",", ":"), default=str) + "\n")

    def _append_csv(self, rec):
        new = not os.path.exists(self.csv_path) or os.path.getsize(self.csv_path) == 0
        row = {
            **{k: rec.get(k) for k in SIGNAL_FIELDS},
            "evidence": ";".join(rec.get("evidence") or []),
            "vix_status": (rec.get("context", {}).get("vix") or {}).get("status", ""),
            "gex_status": (rec.get("context", {}).get("gex") or {}).get("status", ""),
            "data_quality": (rec.get("data") or {}).get("quality", ""),
        }
        with open(self.csv_path, "a", newline="", encoding="utf-8") as f:
            w = csv.DictWriter(f, fieldnames=SIGNAL_FIELDS)
            if new:
                w.writeheader()
            w.writerow(row)

    # -------------------------------------------------------------------- read
    def tail_signals(self, limit=50):
        return self._tail_jsonl(self.signals_path, limit)

    @staticmethod
    def _tail_jsonl(path, limit):
        if not os.path.exists(path):
            return []
        with open(path, encoding="utf-8") as f:
            lines = f.readlines()
        out = []
        for line in lines[-max(1, limit):]:
            line = line.strip()
            if not line:
                continue
            try:
                out.append(json.loads(line))
            except json.JSONDecodeError:
                continue
        return out

    # ------------------------------------------------------------------- state
    def save_state(self, state):
        tmp = self.state_path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(state, f, indent=1, default=str)
        os.replace(tmp, self.state_path)

    def load_state(self):
        if not os.path.exists(self.state_path):
            return {}
        try:
            with open(self.state_path, encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            return {}

    def counts(self):
        n_sig = self._count_lines(self.signals_path)
        n_eval = self._count_lines(self.evals_path)
        return {"signals_written": n_sig, "evaluations_written": n_eval,
                "files": {"signals": self.signals_path, "evaluations": self.evals_path,
                          "csv": self.csv_path, "state": self.state_path}}

    @staticmethod
    def _count_lines(path):
        if not os.path.exists(path):
            return 0
        with open(path, "rb") as f:
            return sum(1 for _ in f)


def utcnow():
    return datetime.now(timezone.utc)
