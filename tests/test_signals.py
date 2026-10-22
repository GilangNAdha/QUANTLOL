"""
Tests for the standalone signal application (signals/).

Run:  python3 -m unittest tests.test_signals -v

These tests are the acceptance evidence for the signal app:
  * it loads and validates free data, and rejects malformed data instead of repairing it silently;
  * it emits the same decision as the shared rule engine and respects the $5 cap;
  * it writes each signal once (no duplicates), with a rejection log;
  * the HTTP layer is read-only: there is no endpoint that can place, modify or close an order;
  * the package has no broker dependency at all (static import check).
"""
from __future__ import annotations

import ast
import csv
import gzip
import json
import os
import shutil
import sys
import tempfile
import unittest
from datetime import datetime, timedelta, timezone

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if REPO not in sys.path:
    sys.path.insert(0, REPO)

from signals import datafeed, engine, store as store_mod
from signals.config import SignalConfig, ConfigError
from signals.core_bridge import core
from signals.service import SignalService
from signals.server import serve_in_thread

DATA_5M = os.path.join(REPO, "research", "data", "gc_5m.csv.gz")
ACTION_BAR = datetime(2026, 8, 13, 8, 55, tzinfo=timezone.utc)   # known actionable bar in the snapshot
ACTION_INDEX = 335


# --------------------------------------------------------------------------- helpers
def write_slice(path, end_index, start_index=0):
    """Writes a plain CSV of the committed snapshot up to end_index (deterministic last bar)."""
    feed = datafeed.load_local(DATA_5M)
    with open(path, "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["time_utc", "open", "high", "low", "close", "volume"])
        for b in feed.bars[start_index:end_index + 1]:
            w.writerow([b.t.strftime("%Y-%m-%d %H:%M:%S"), b.o, b.h, b.l, b.c, b.v])
    return end_index - start_index + 1


class Tmp(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="qlsignals-")

    def tearDown(self):
        shutil.rmtree(self.dir, ignore_errors=True)

    def cfg(self, **kw):
        c = SignalConfig(data_mode="local", data_path=DATA_5M, output_dir=os.path.join(self.dir, "out"),
                         enable_vix=False, enable_gex=False)
        for k, v in kw.items():
            setattr(c, k, v)
        return c.validate()


# ---------------------------------------------------------------------------- config
class ConfigTests(Tmp):
    def test_money_and_cap_arithmetic(self):
        c = self.cfg()
        self.assertAlmostEqual(1.0, c.money_per_price, places=9)          # 0.01 lot x 100 oz
        # 5 USD cap - 0.14 commission - 5 ticks * 0.01 * 1.0 = 4.81 price units
        self.assertAlmostEqual(4.81, c.max_stop_distance, places=6)

    def test_invalid_configs_are_refused(self):
        base = dict(data_mode="local", data_path=DATA_5M, output_dir=self.dir)
        for bad in (dict(port=0), dict(lots=0), dict(min_rr=0), dict(scan_bars=10),
                    dict(data_mode="telepathy"), dict(timeframe_minutes=7)):
            opts = dict(base); opts.update(bad)
            with self.assertRaises(ConfigError, msg=str(bad)):
                SignalConfig(**opts).validate()

    def test_missing_data_file_is_a_config_error(self):
        with self.assertRaises(ConfigError):
            SignalConfig(data_mode="local", data_path=os.path.join(self.dir, "nope.csv")).validate()

    def test_load_ignores_unknown_keys_but_records_them(self):
        p = os.path.join(self.dir, "c.json")
        with open(p, "w") as f:
            json.dump({"lots": 0.01, "data_mode": "local", "data_path": DATA_5M,
                       "output_dir": self.dir, "nonsense": 5}, f)
        c = SignalConfig.load(p)
        self.assertEqual(0.01, c.lots)
        self.assertIn("nonsense", c.extra["unknown_keys"])


# ------------------------------------------------------------------------------ data
class DataFeedTests(Tmp):
    def test_local_gzip_snapshot_loads_and_is_validated(self):
        f = datafeed.load_local(DATA_5M)
        self.assertTrue(f.ok, f.error)
        self.assertEqual("proxy", f.quality)
        self.assertGreater(len(f.bars), 1000)
        self.assertEqual(0, f.rows_rejected)
        times = [b.t for b in f.bars]
        self.assertEqual(sorted(times), times, "bars must be chronological")

    def test_bad_rows_are_rejected_not_repaired(self):
        rows = [["time_utc", "open", "high", "low", "close", "volume"],
                ["2026-01-01 00:00:00", 100, 101, 99, 100.5, 10],
                ["2026-01-01 00:05:00", 100, 99, 101, 100.5, 10],     # high < low
                ["not-a-time", 100, 101, 99, 100.5, 10],              # bad timestamp
                ["2026-01-01 00:10:00", 100, 101, 99, 100.5, 10],
                ["2026-01-01 00:10:00", 100, 101, 99, 100.5, 10],     # duplicate timestamp
                ["2026-01-01 00:15:00", 100, 101, 99]]                # short row
        bars, rejected, first, last = datafeed.parse_rows(rows)
        self.assertEqual(2, len(bars))
        # rejects: high<low, bad timestamp, duplicate timestamp, short row
        self.assertEqual(4, rejected)

    def test_headerless_export_is_accepted(self):
        rows = [["2026-01-01 00:00:00", "100", "101", "99", "100.5", "10"],
                ["2026-01-01 00:05:00", "100.5", "101", "99", "100.2", ""]]
        bars, rejected, _, _ = datafeed.parse_rows(rows, has_header=False)
        self.assertEqual(2, len(bars))
        self.assertEqual(0, rejected)
        self.assertEqual(0.0, bars[1].v)

    def test_alternative_timestamp_formats(self):
        for text in ("2026-01-02 03:04:05", "2026-01-02T03:04:05", "2026-01-02T03:04:05Z",
                     "2026-01-02", "02/01/2026 03:04:05"):
            self.assertIsNotNone(datafeed.parse_time(text), text)

    def test_freshness_and_age(self):
        f = datafeed.load_local(DATA_5M)
        now = f.last + timedelta(minutes=5)
        self.assertTrue(f.is_fresh(30, now))
        self.assertFalse(f.is_fresh(30, f.last + timedelta(minutes=31)))
        self.assertIn("PROXY", f.describe(now).upper())

    def test_missing_file_reports_invalid_without_raising(self):
        f = datafeed.load_local(os.path.join(self.dir, "missing.csv"))
        self.assertFalse(f.ok)
        self.assertIn("file not found", f.error)
        self.assertEqual("DATA:INVALID", f.describe()[:12])


# -------------------------------------------------------------------------- external
class ExternalTests(Tmp):
    def _vix(self, obj, **kw):
        p = os.path.join(self.dir, "vix.json")
        with open(p, "w") as f:
            json.dump(obj, f)
        c = self.cfg(enable_vix=True, vix_path=p, **kw)
        return datafeed.load_vix(c, now=datetime(2026, 10, 20, 18, 0, tzinfo=timezone.utc))

    def test_fresh_vix(self):
        st = self._vix({"vix": 17.3, "as_of": "2026-10-20T17:15:00Z", "quality": "delayed"})
        self.assertTrue(st.available)
        self.assertTrue(st.fresh)
        self.assertEqual(17.3, st.data["level"])
        self.assertEqual("delayed", st.quality)

    def test_stale_and_future_vix(self):
        old = self._vix({"vix": 17.3, "as_of": "2026-10-01T17:15:00Z"})
        self.assertTrue(old.available)
        self.assertFalse(old.fresh)
        future = self._vix({"vix": 17.3, "as_of": "2026-10-25T17:15:00Z"})
        self.assertFalse(future.available)
        self.assertIn("future", future.status)

    def test_implausible_vix_is_rejected(self):
        st = self._vix({"vix": 0, "as_of": "2026-10-20T17:15:00Z"})
        self.assertFalse(st.available)
        self.assertIn("plausible", st.status)

    def _gex(self, obj, symbol="XAUUSD", spot=4321.5):
        p = os.path.join(self.dir, "gex.json")
        with open(p, "w") as f:
            json.dump(obj, f)
        c = self.cfg(enable_gex=True, gex_path=p, symbol=symbol)
        return datafeed.load_gex(c, spot=spot, now=datetime(2026, 10, 20, 18, 0, tzinfo=timezone.utc))

    def test_manual_gex_is_accepted_with_provenance(self):
        st = self._gex({"instrument": "XAUUSD", "as_of": "2026-10-20T17:30:00Z",
                        "levels": [{"price": 4350, "kind": "call_wall"}],
                        "gamma_flip": 4300, "put_wall": 4250})
        self.assertTrue(st.available)
        self.assertEqual("manual", st.quality)
        self.assertEqual(3, len(st.data["levels"]))

    def test_gex_from_gld_is_flagged_proxy(self):
        st = self._gex({"instrument": "GLD", "as_of": "2026-10-20T17:30:00Z",
                        "levels": [{"price": 4350, "kind": "call_wall"}]})
        self.assertTrue(st.available)
        self.assertEqual("proxy", st.quality)

    def test_gex_wrong_instrument_and_wrong_scale_are_rejected(self):
        bad_inst = self._gex({"instrument": "SPX", "as_of": "2026-10-20T17:30:00Z",
                              "levels": [{"price": 4350, "kind": "call_wall"}]})
        self.assertFalse(bad_inst.available)
        self.assertIn("instrument mismatch", bad_inst.status)
        bad_scale = self._gex({"instrument": "GLD", "as_of": "2026-10-20T17:30:00Z",
                               "levels": [{"price": 398.5, "kind": "call_wall"}]})
        self.assertFalse(bad_scale.available)
        self.assertIn("sane range", bad_scale.status)

    def test_disabled_external_inputs_are_not_loaded(self):
        c = self.cfg(enable_vix=False, enable_gex=False)
        self.assertIn("disabled", datafeed.load_vix(c).status)
        self.assertIn("disabled", datafeed.load_gex(c).status)


# ---------------------------------------------------------------------------- engine
class EngineTests(Tmp):
    def test_record_is_complete_and_json_serialisable(self):
        cfg = self.cfg()
        feed = datafeed.load_local(DATA_5M)
        rec = engine.evaluate_bar(feed.bars, len(feed.bars) - 1, cfg, feed=feed)
        json.dumps(rec)                      # must not raise
        for key in ("state", "reason", "price", "bar_time_utc", "session", "risk_ok",
                    "context", "data", "engine_ms", "dedupe_key", "max_risk_usd"):
            self.assertIn(key, rec)
        self.assertIn(rec["state"], ("long", "short", "neutral", "unavailable"))
        self.assertEqual(5.0, rec["max_risk_usd"])

    def test_actionable_signal_respects_cap_and_rr(self):
        cfg = self.cfg()
        feed = datafeed.load_local(DATA_5M)
        rec = engine.evaluate_bar(feed.bars, ACTION_INDEX, cfg, feed=feed)
        self.assertEqual("long", rec["state"], rec.get("detail"))
        self.assertLessEqual(rec["risk_usd"], cfg.max_risk_usd + 1e-9)
        self.assertGreaterEqual(rec["rr_net"], cfg.min_rr - 1e-9)
        self.assertLess(rec["stop"], rec["entry"])
        self.assertGreater(rec["target"], rec["entry"])
        self.assertTrue(rec["setup"])
        self.assertTrue(rec["evidence"])

    def test_engine_matches_the_shared_rule_function(self):
        """The app must not re-implement a single decision rule."""
        cfg = self.cfg()
        feed = datafeed.load_local(DATA_5M)
        i = ACTION_INDEX
        rec = engine.evaluate_bar(feed.bars, i, cfg, feed=feed)
        ctx = engine.build_context(feed.bars, i, cfg)
        dec = core.evaluate_signal(engine.engine_config(cfg), feed.bars, i, ctx.snap,
                                   ctx.session_profile, ctx.prev_profile, ctx.order_flow,
                                   ctx.targets, None)
        mapped = {"bull": "long", "bear": "short"}.get(dec["state"], dec["state"])
        self.assertEqual(mapped, rec["state"])
        self.assertEqual(dec.get("reject") or "None", rec["reason"])
        if rec["state"] in ("long", "short"):
            # the record rounds to 4 decimals for display; compare at that precision
            self.assertAlmostEqual(dec["entry"], rec["entry"], places=4)
            self.assertAlmostEqual(dec["stop"], rec["stop"], places=4)
            self.assertAlmostEqual(dec["target"], rec["target"], places=4)

    def test_high_vix_blocks_entries(self):
        cfg = self.cfg(enable_vix=True, vix_high=30.0)
        vix = datafeed.ExternalStatus("VIX", True, "delayed", 30.0, True, "VIX:FRESH",
                                      {"level": 35.0, "as_of": "2026-08-13T00:00:00+00:00"})
        feed = datafeed.load_local(DATA_5M)
        rec = engine.evaluate_bar(feed.bars, ACTION_INDEX, cfg, feed=feed, vix_status=vix)
        self.assertEqual("neutral", rec["state"])
        self.assertEqual("VixFilter", rec["reason"])

    def test_stale_vix_does_not_veto_and_is_reported(self):
        cfg = self.cfg(enable_vix=True, vix_high=30.0)
        stale = datafeed.ExternalStatus("VIX", True, "delayed", 5000.0, False,
                                        "VIX:STALE(delayed,age=5000m)", {"level": 90.0,
                                                                          "as_of": "2026-01-01T00:00:00+00:00"})
        feed = datafeed.load_local(DATA_5M)
        rec = engine.evaluate_bar(feed.bars, ACTION_INDEX, cfg, feed=feed, vix_status=stale)
        self.assertEqual("long", rec["state"])
        self.assertFalse(rec["context"]["vix"]["fresh"])

    def test_replay_finds_the_known_signal_and_counts_rejections(self):
        cfg = self.cfg()
        feed = datafeed.load_local(DATA_5M)
        res = engine.replay(feed.bars, cfg, start=300, limit=5)
        self.assertGreaterEqual(len(res["signals"]), 1)
        self.assertGreater(sum(res["rejections"].values()), 0)
        self.assertEqual(ACTION_BAR, datetime.fromisoformat(res["signals"][0]["bar_time_utc"]))
        self.assertEqual("long", res["signals"][0]["state"])

    def test_unavailable_when_there_is_no_data(self):
        cfg = self.cfg()
        empty = datafeed.FeedResult(False, [], source="x", quality="unknown", error="boom")
        rec = engine.evaluate_latest(empty, cfg)
        self.assertEqual("unavailable", rec["state"])
        self.assertEqual("DataStale", rec["reason"])


# --------------------------------------------------------------------------- service
class ServiceTests(Tmp):
    def _service(self, end_index=ACTION_INDEX):
        csv_path = os.path.join(self.dir, "slice.csv")
        write_slice(csv_path, end_index)
        cfg = self.cfg(data_mode="csv", csv_path=csv_path,
                       output_dir=os.path.join(self.dir, "out"))
        return SignalService(cfg)

    def test_signal_is_written_once_per_bar(self):
        svc = self._service()
        svc.poll_once()
        svc.poll_once()
        svc.poll_once()
        counts = svc.store.counts()
        self.assertEqual(1, counts["signals_written"], "duplicate suppression failed")
        # one audit row per (bar, outcome); repeated polls of the same bar add nothing
        self.assertEqual(1, counts["evaluations_written"])
        self.assertEqual(3, svc.stats["evaluations"])
        rows = svc.store.tail_signals(10)
        self.assertEqual(1, len(rows))
        self.assertEqual("long", rows[0]["state"])

    def test_repeated_polls_do_not_duplicate_evaluation_rows(self):
        """A static feed polled forever must not grow the audit log without bound."""
        svc = self._service()
        for _ in range(5):
            svc.poll_once()
        counts = svc.store.counts()
        self.assertEqual(1, counts["signals_written"], "signal duplicated")
        self.assertEqual(1, counts["evaluations_written"], "evaluation duplicated")
        self.assertEqual(4, svc.stats["evaluations_skipped"])
        # a restart must not re-write the row it already wrote
        svc2 = SignalService(svc.cfg)
        svc2.poll_once()
        self.assertEqual(1, svc2.store.counts()["evaluations_written"])

    def test_a_new_bar_adds_exactly_one_evaluation_row(self):
        svc = self._service()
        svc.poll_once()
        csv_path = os.path.join(self.dir, "later.csv")
        write_slice(csv_path, ACTION_INDEX + 1)
        svc.cfg.csv_path = csv_path
        svc.poll_once()
        self.assertEqual(2, svc.store.counts()["evaluations_written"])

    def test_state_exposes_health_data_and_stats(self):
        st = self._service().poll_once()
        self.assertIn("config", st)
        self.assertIn("health", st)
        self.assertIn("latest", st)
        self.assertEqual("none (advisory signals only)", st["config"]["execution"])
        self.assertIn("by_reason", st["stats"])
        self.assertTrue(st["data"]["ok"])

    def test_missing_data_keeps_the_service_alive(self):
        svc = self._service()
        svc.cfg.csv_path = os.path.join(self.dir, "gone.csv")
        st = svc.poll_once()
        self.assertFalse(st["health"]["ok"])
        self.assertEqual("error", st["health"]["issues"][0]["severity"])
        self.assertIn("market data", st["health"]["issues"][0]["text"].lower())

    def test_store_csv_and_state_round_trip(self):
        st_dir = os.path.join(self.dir, "store")
        s = store_mod.Store(st_dir)
        rec = {"bar_time_utc": "2026-08-13T08:55:00+00:00", "state": "long", "direction": "LONG",
               "setup": "SwingLow_sweep+bull_choch", "session": "London", "price": 4432.4,
               "entry": 4432.4, "stop": 4428.05, "target": 4465.2, "risk_usd": 4.54,
               "rr_net": 7.18, "score": 7.0, "stop_ticks": 435, "target_ticks": 3280,
               "synthetic_target": False, "evidence": ["bull_sweep", "choch_bull"],
               "context": {"vix": {"status": "VIX:disabled"}, "gex": {"status": "GEX:disabled"}},
               "data": {"quality": "proxy"}, "engine_ms": 2.0, "generated_utc": "2026-08-13T09:00:00+00:00"}
        s.append_signal(rec)
        s.save_state({"last_dedupe_key": "k"})
        self.assertEqual("k", s.load_state()["last_dedupe_key"])
        self.assertEqual(1, len(s.tail_signals(5)))
        with open(s.csv_path) as f:
            rows = list(csv.DictReader(f))
        self.assertEqual(1, len(rows))
        self.assertEqual("bull_sweep;choch_bull", rows[0]["evidence"])
        self.assertEqual("proxy", rows[0]["data_quality"])


# ---------------------------------------------------------------------------- server
class ServerTests(Tmp):
    def setUp(self):
        super().setUp()
        csv_path = os.path.join(self.dir, "slice.csv")
        write_slice(csv_path, ACTION_INDEX)
        cfg = SignalConfig(data_mode="csv", csv_path=csv_path, output_dir=os.path.join(self.dir, "out"),
                           enable_vix=False, enable_gex=False).validate()
        self.svc = SignalService(cfg)
        self.httpd, self.thread, self.port = serve_in_thread(self.svc, port=0)

    def tearDown(self):
        self.httpd.shutdown()
        self.httpd.server_close()
        super().tearDown()

    def _get(self, path, method="GET"):
        import urllib.request
        req = urllib.request.Request("http://127.0.0.1:%d%s" % (self.port, path), method=method)
        try:
            with urllib.request.urlopen(req, timeout=5) as r:
                return r.status, r.read()
        except urllib.error.HTTPError as e:
            return e.code, e.read()

    def test_dashboard_and_json_endpoints(self):
        code, body = self._get("/")
        self.assertEqual(200, code)
        self.assertIn(b"QuantLOL", body)
        for path in ("/api/state", "/api/health", "/api/config", "/api/signals?limit=3"):
            code, body = self._get(path)
            self.assertEqual(200, code, path)
            json.loads(body)

    def test_evaluate_endpoint_produces_a_read_and_writes_a_signal(self):
        code, body = self._get("/api/evaluate", method="POST")
        self.assertEqual(200, code)
        payload = json.loads(body)
        self.assertTrue(payload["ok"])
        self.assertEqual("long", payload["state"]["latest"]["state"])
        self.assertEqual(1, self.svc.store.counts()["signals_written"])

    def test_unknown_path_is_404(self):
        code, _ = self._get("/api/orders", method="POST")
        self.assertEqual(404, code)


# ------------------------------------------------------------- separation guarantees
class SeparationTests(unittest.TestCase):
    """The user asked for a product that is separate from the cBot: prove it structurally."""

    FORBIDDEN_IMPORTS = {"cAlgo", "cAlgo.API", "MetaTrader5", "mt5", "ccxt", "ib_insync", "alpaca"}

    def test_no_broker_sdk_is_imported_anywhere_in_the_package(self):
        pkg = os.path.join(REPO, "signals")
        offenders = []
        for name in sorted(os.listdir(pkg)):
            if not name.endswith(".py"):
                continue
            tree = ast.parse(open(os.path.join(pkg, name)).read())
            for node in ast.walk(tree):
                if isinstance(node, ast.Import):
                    for a in node.names:
                        if a.name.split(".")[0] in self.FORBIDDEN_IMPORTS:
                            offenders.append((name, a.name))
                elif isinstance(node, ast.ImportFrom) and node.module:
                    if node.module.split(".")[0] in self.FORBIDDEN_IMPORTS:
                        offenders.append((name, node.module))
        self.assertEqual([], offenders)

    def test_no_order_entry_point_exists(self):
        """No function in the app may look like an order submission."""
        pkg = os.path.join(REPO, "signals")
        banned = ("place_order", "submit_order", "execute_market", "send_order", "open_position",
                  "close_position", "ExecuteMarketOrder", "PlaceLimitOrder")
        hits = []
        for name in os.listdir(pkg):
            if not name.endswith(".py"):
                continue
            text = open(os.path.join(pkg, name)).read()
            for token in banned:
                if token in text:
                    hits.append((name, token))
        self.assertEqual([], hits)

    def test_engine_is_imported_from_the_shared_module_not_copied(self):
        from signals import core_bridge
        self.assertTrue(hasattr(core_bridge.core, "evaluate_signal"))
        self.assertTrue(core_bridge.describe_engine()["module"].endswith("quantlol_ref.py"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
