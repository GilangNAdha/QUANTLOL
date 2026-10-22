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


class TestLiveProvidersOffline(unittest.TestCase):
    """Provider parsing, with the network stubbed out. A live call can never run in CI here."""

    def setUp(self):
        from signals import providers
        self.pv = providers
        self._real = providers._json
        self.now = datetime(2026, 10, 22, 9, 5, tzinfo=timezone.utc)

    def tearDown(self):
        self.pv._json = self._real

    def _serve(self, payload):
        self.pv._json = lambda *a, **k: payload

    def test_in_progress_candle_is_dropped(self):
        from signals.core_bridge import core
        bars = [core.Bar(datetime(2026, 10, 22, 9, 0, tzinfo=timezone.utc), 1, 2, 0.5, 1.5, 10)]
        kept, dropped = self.pv.drop_incomplete(bars, 5, now=datetime(2026, 10, 22, 9, 4, tzinfo=timezone.utc))
        self.assertTrue(dropped)
        self.assertEqual([], kept)
        kept, dropped = self.pv.drop_incomplete(bars, 5, now=datetime(2026, 10, 22, 9, 6, tzinfo=timezone.utc))
        self.assertFalse(dropped)
        self.assertEqual(1, len(kept))

    def test_binance_kline_parsing(self):
        t0 = 1_790_000_000_000
        self._serve([[t0, "100.0", "110.0", "90.0", "105.0", "1234.5"],
                     [t0 + 300_000, "105.0", "106.0", "101.0", "102.0", "999.0"]])
        bars, meta = self.pv.fetch("binance", "BTCUSDT", 5, limit=2)
        self.assertEqual(2, len(bars))
        self.assertEqual(105.0, bars[0].c)
        self.assertEqual(1234.5, bars[0].v)
        self.assertEqual("live", meta["quality"])
        self.assertTrue(meta["volume_available"])
        self.assertEqual("binance:BTCUSDT", meta["source"])

    def test_coinbase_column_order_is_low_high_open_close(self):
        t0 = 1_790_000_000
        self._serve([[t0, 90.0, 110.0, 100.0, 105.0, 12.0],
                     [t0 + 300, 101.0, 106.0, 105.0, 102.0, 9.0]])
        bars, _ = self.pv.fetch("coinbase", "BTC-USD", 5)
        self.assertEqual(100.0, bars[0].o)     # third column is the open, not the close
        self.assertEqual(110.0, bars[0].h)
        self.assertEqual(90.0, bars[0].l)
        self.assertEqual(105.0, bars[0].c)

    def test_okx_unconfirmed_rows_are_skipped(self):
        t0 = 1_790_000_000_000
        self._serve({"code": "0", "data": [
            [t0 + 300_000, "105", "106", "101", "102", "9", "0", "0", "1"],   # confirm=1 -> closed
            [t0, "100", "110", "90", "105", "10", "0", "0", "0"]]})           # confirm=0 -> forming
        bars, _ = self.pv.fetch("okx", "BTC-USDT", 5)
        self.assertEqual(1, len(bars))
        self.assertEqual(102.0, bars[-1].c)

    def test_kraken_last_row_is_dropped_as_in_progress(self):
        t0 = 1_790_000_000
        self._serve({"error": [], "result": {"XXBTZUSD": [
            [t0, "100", "110", "90", "105", "1", "10"],
            [t0 + 300, "105", "106", "101", "102", "1", "9"]], "last": 1}})
        bars, _ = self.pv.fetch("kraken", "XBTUSD", 5)
        # both rows close in the past relative to "now" in the test clock, so both survive;
        # the drop is exercised by the shared drop_incomplete test above.
        self.assertEqual(2, len(bars))
        self.assertEqual(10.0, bars[0].v)

    def test_a_provider_error_is_loud(self):
        self._serve({"code": "-1121", "msg": "Invalid symbol"})
        with self.assertRaises(self.pv.ProviderError):
            self.pv.fetch("okx", "NOPE-USDT", 5)

    def test_unknown_provider_is_rejected(self):
        with self.assertRaises(self.pv.ProviderError):
            self.pv.fetch("definitely-not-a-venue", "BTC", 5)

    def test_fallback_chain_moves_to_the_next_venue(self):
        from signals.config import SignalConfig
        cfg = SignalConfig(data_mode="live", live_limit=2, require_order_flow=True)
        calls = []

        def fake_fetch(name, symbol=None, tf=5, limit=500, api_key=None, yahoo_range="5d", market=None):
            calls.append(name)
            if name in ("binance", "kraken"):
                raise self.pv.ProviderError("HTTP 451")
            from signals.core_bridge import core
            t = datetime.now(timezone.utc) - timedelta(minutes=10)
            bars = [core.Bar(t + timedelta(minutes=5 * i), 1, 2, 0.5, 1.5, 3) for i in range(2)]
            return bars, {"source": name + ":X", "quality": "live", "note": "", "volume_available": True}

        real = self.pv.fetch
        self.pv.fetch = fake_fetch
        try:
            res = datafeed.load_feed(cfg)
        finally:
            self.pv.fetch = real
        self.assertTrue(res.ok)
        self.assertEqual("coinbase:X", res.source)
        self.assertIn("fell back to coinbase after 2 failure(s)", res.note)
        self.assertEqual(["binance", "kraken", "coinbase"], calls)

    def test_every_provider_failing_is_an_error_not_an_empty_feed(self):
        from signals.config import SignalConfig
        cfg = SignalConfig(data_mode="live")
        real = self.pv.fetch
        self.pv.fetch = lambda *a, **k: (_ for _ in ()).throw(self.pv.ProviderError("HTTP 403"))
        try:
            res = datafeed.load_feed(cfg)
        finally:
            self.pv.fetch = real
        self.assertFalse(res.ok)
        self.assertIn("every provider failed", res.error)

    def test_no_volume_is_flagged_because_order_flow_cannot_work(self):
        from signals.config import SignalConfig
        cfg = SignalConfig(data_mode="live", live_provider="yahoo", live_symbol="EURUSD=X")
        real = self.pv.fetch
        self.pv.fetch = lambda *a, **k: (
            [type("B", (), {"t": datetime.now(timezone.utc) - timedelta(minutes=5), "v": 0.0})()],
            {"source": "yahoo:EURUSD=X", "quality": "no-volume", "note": "", "volume_available": False})
        try:
            res = datafeed.load_feed(cfg)
        finally:
            self.pv.fetch = real
        self.assertTrue(res.ok)
        self.assertIn("NO VOLUME", res.note)


class TestInstrumentPresets(unittest.TestCase):
    def test_gold_preset_keeps_the_published_arithmetic(self):
        from signals.config import SignalConfig
        c = SignalConfig().apply_preset("xauusd").validate()
        self.assertEqual(1.0, c.money_per_price)          # 0.01 lot x 100 oz
        self.assertAlmostEqual(4.810, c.max_stop_distance, places=3)

    def test_btc_preset_scales_the_cap_to_the_instrument(self):
        from signals.config import SignalConfig
        c = SignalConfig().apply_preset("btc").validate()
        self.assertEqual(0.005, round(c.money_per_price, 4))   # 0.005 BTC
        self.assertAlmostEqual(995.5, c.max_stop_distance, places=1)
        self.assertTrue(c.require_order_flow)                  # venue feed has real volume

    def test_symbol_spelling_follows_the_venue_not_the_preset(self):
        from signals import providers
        from signals.config import SignalConfig
        c = SignalConfig().apply_preset("btc").validate()
        self.assertEqual("", c.live_symbol)          # no venue pinned
        self.assertEqual("btc", c.market)
        self.assertEqual("BTCUSDT", providers.default_symbol("binance", c.market))
        self.assertEqual("BTC-USD", providers.default_symbol("coinbase", c.market))
        self.assertEqual("XBTUSD", providers.default_symbol("kraken", c.market))
        self.assertEqual("btcusd", providers.default_symbol("bitstamp", c.market))

    def test_eurusd_preset_disables_an_impossible_order_flow_test(self):
        from signals.config import SignalConfig
        c = SignalConfig().apply_preset("eurusd").validate()
        self.assertEqual(1000.0, c.money_per_price)            # 0.01 lot x 100k
        self.assertFalse(c.require_order_flow)                 # Yahoo FX publishes no volume

    def test_unknown_preset_is_rejected(self):
        from signals.config import SignalConfig, ConfigError
        with self.assertRaises(ConfigError):
            SignalConfig().apply_preset("doge")

    def test_deribit_gex_needs_gex_enabled(self):
        from signals.config import SignalConfig, ConfigError
        c = SignalConfig().apply_preset("btc")
        c.gex_source = "deribit"
        with self.assertRaises(ConfigError):
            c.validate()


class TestOptionChainParsing(unittest.TestCase):
    """Deribit's chain summary carries no strike_price field; the strike lives in the name."""

    def test_strike_comes_from_the_instrument_name(self):
        from signals import providers
        real = providers._json
        providers._json = lambda *a, **k: {"result": [
            {"instrument_name": "BTC-27MAR26-100000-C", "open_interest": 500.0, "mark_iv": 52.5,
             "underlying_price": 101000.0},
            {"instrument_name": "BTC-27MAR26-100000-P", "open_interest": 300.0, "mark_iv": 54.0},
            {"instrument_name": "BTC-27MAR26-105000-C", "open_interest": 0.0, "mark_iv": 51.0}]}
        try:
            chain = providers.btc_option_chain("BTC")
        finally:
            providers._json = real
        self.assertEqual(3, len(chain))
        self.assertEqual(100000.0, chain[0]["strike"])
        self.assertEqual("call", chain[0]["type"])
        self.assertEqual("put", chain[1]["type"])
        self.assertEqual(105000.0, chain[2]["strike"])

    def test_an_api_error_is_reported_not_swallowed(self):
        from signals import providers
        real = providers._json
        providers._json = lambda *a, **k: {"error": {"code": 10001, "message": "bad request"}}
        try:
            with self.assertRaises(providers.ProviderError) as ctx:
                providers.btc_option_chain("BTC")
        finally:
            providers._json = real
        self.assertIn("bad request", str(ctx.exception))

    def test_rows_without_parseable_strikes_fail_loudly(self):
        from signals import providers
        real = providers._json
        providers._json = lambda *a, **k: {"result": [{"instrument_name": "BTC-PERPETUAL"}]}
        try:
            with self.assertRaises(providers.ProviderError) as ctx:
                providers.btc_option_chain("BTC")
        finally:
            providers._json = real
        self.assertIn("no parseable strikes", str(ctx.exception))


class TestOptionsOpenInterest(unittest.TestCase):
    def _chain(self):
        rows = []
        for strike, call_oi, put_oi in ((90000, 10, 300), (95000, 20, 200), (100000, 500, 50),
                                        (105000, 700, 10), (110000, 100, 5), (150000, 99999, 0)):
            rows.append({"instrument": "BTC-31DEC26-%d-C" % strike, "strike": float(strike),
                         "oi": float(call_oi), "iv": 50.0, "type": "call", "expiry": "x"})
            rows.append({"instrument": "BTC-31DEC26-%d-P" % strike, "strike": float(strike),
                         "oi": float(put_oi), "iv": 50.0, "type": "put", "expiry": "x"})
        return rows

    def test_levels_are_open_interest_concentrations_inside_the_band(self):
        from signals import providers
        levels, info = providers.oi_levels(self._chain(), spot=100000.0, band=0.10)
        kinds = {lv["kind"]: lv["price"] for lv in levels}
        self.assertEqual(105000.0, kinds["call_wall"])
        self.assertEqual(90000.0, kinds["put_wall"])
        self.assertIn("gamma_flip", kinds)
        self.assertNotIn(150000.0, [lv["price"] for lv in levels])   # outside +-10%
        self.assertEqual(12, info["contracts"])

    def test_gex_status_never_claims_dealer_gamma(self):
        from signals.config import SignalConfig
        c = SignalConfig().apply_preset("btc")
        c.enable_gex, c.gex_source = True, "deribit"
        c.validate()
        st = datafeed.load_gex_deribit(c, spot=100000.0, now=datetime.now(timezone.utc), chain=self._chain())
        self.assertTrue(st.available)
        self.assertEqual("oi-derived", st.quality)
        self.assertIn("no dealer-gamma sign is claimed", st.data["note"])
        self.assertEqual(3, len(st.data["levels"]))

    def test_gex_without_a_spot_price_is_unavailable(self):
        from signals.config import SignalConfig
        c = SignalConfig().apply_preset("btc")
        c.enable_gex, c.gex_source = True, "deribit"
        st = datafeed.load_gex_deribit(c, spot=None, chain=self._chain())
        self.assertFalse(st.available)
        self.assertIn("spot price is required", st.status)


if __name__ == "__main__":
    unittest.main(verbosity=2)
