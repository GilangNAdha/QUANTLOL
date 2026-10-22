"""Configuration for the signal application. Validated at start-up, like the cBot parameters."""
from __future__ import annotations

import json
import os
from dataclasses import dataclass, field, asdict


class ConfigError(ValueError):
    pass


@dataclass
class SignalConfig:
    # ---- identity -------------------------------------------------------------
    symbol: str = "XAUUSD"
    timeframe_minutes: int = 5
    label: str = "XAUUSD (proxy: COMEX GC=F)"

    # ---- data -----------------------------------------------------------------
    data_mode: str = "local"                 # local | live | csv
    data_path: str = "research/data/gc_5m.csv.gz"
    live_symbol: str = "GC=F"
    live_range: str = "60d"
    csv_path: str = ""
    csv_has_header: bool = True
    max_bar_age_minutes: int = 30            # a bar older than this is reported stale

    # ---- contract / money -----------------------------------------------------
    lots: float = 0.01
    ounces_per_lot: float = 100.0            # XAUUSD: 1 lot = 100 oz  -> 0.01 lot = 1 oz
    tick: float = 0.01
    commission_round_trip: float = 0.14      # USD for 1 oz round turn (assumption; verify)
    slippage_ticks: int = 5
    assumed_spread_ticks: int = 30           # NO quote feed: this is an assumption, labelled as such
    max_spread_ticks: int = 40

    # ---- risk mandate ---------------------------------------------------------
    max_risk_usd: float = 5.0
    min_rr: float = 1.5
    daily_loss_usd: float = 15.0
    equity_dd_usd: float = 30.0
    reference_balance: float = 1000.0

    # ---- signal rules ---------------------------------------------------------
    min_score: float = 4.0
    swing_lookback: int = 2
    scan_bars: int = 320
    sweep_window: int = 12
    confirm_window: int = 12
    location_proximity_ticks: int = 60
    stop_buffer_ticks: int = 5
    require_location: bool = True
    require_confirmation: bool = True
    require_order_flow: bool = True
    enable_fvg: bool = True
    enable_order_block: bool = True
    allow_synthetic_target: bool = True
    fallback_rr: float = 2.0

    # ---- optional context -----------------------------------------------------
    enable_vix: bool = True
    vix_path: str = "research/samples/vix.json"
    vix_max_age_minutes: int = 1440
    block_on_high_vix: bool = True
    vix_high: float = 30.0

    enable_gex: bool = False
    gex_path: str = "research/samples/gex_example.json"
    gex_max_age_minutes: int = 240
    gex_min_price: float = 2000.0
    gex_max_price: float = 8000.0

    # ---- service --------------------------------------------------------------
    poll_seconds: int = 60
    output_dir: str = "signals/out"
    host: str = "0.0.0.0"
    port: int = 8080
    max_recent_signals: int = 200

    extra: dict = field(default_factory=dict)

    # ------------------------------------------------------------------ validate
    def validate(self):
        if self.data_mode not in ("local", "live", "csv"):
            raise ConfigError("data_mode must be local|live|csv")
        if self.timeframe_minutes not in (1, 3, 5, 15, 30, 60, 240):
            raise ConfigError("timeframe_minutes must be one of 1,3,5,15,30,60,240")
        if self.lots <= 0:
            raise ConfigError("lots must be > 0")
        if not (0 < self.ounces_per_lot <= 10000):
            raise ConfigError("ounces_per_lot must be in (0, 10000]")
        money_per_price = self.money_per_price
        if money_per_price <= 0:
            raise ConfigError("lots * ounces_per_lot must be > 0 (money per 1.0 price unit)")
        if self.max_risk_usd <= 0:
            raise ConfigError("max_risk_usd must be > 0")
        if self.assumed_spread_ticks < 0:
            raise ConfigError("assumed_spread_ticks must be >= 0")
        if self.min_rr <= 0:
            raise ConfigError("min_rr must be > 0")
        if self.scan_bars < 50:
            raise ConfigError("scan_bars must be >= 50")
        if self.swing_lookback < 1:
            raise ConfigError("swing_lookback must be >= 1")
        if self.data_mode == "local" and not os.path.exists(self.data_path):
            raise ConfigError("local data file not found: " + self.data_path)
        if self.data_mode == "csv" and not os.path.exists(self.csv_path):
            raise ConfigError("csv file not found: " + self.csv_path)
        if self.poll_seconds < 5:
            raise ConfigError("poll_seconds must be >= 5")
        if not (1 <= self.port <= 65535):
            raise ConfigError("port out of range")
        return self

    @property
    def money_per_price(self):
        """USD P&L per 1.00 move in price for the configured volume."""
        return self.lots * self.ounces_per_lot

    @property
    def max_stop_distance(self):
        """Largest structural stop (price units) that still fits the USD cap after costs."""
        budget = self.max_risk_usd - self.commission_round_trip - self.slippage_ticks * self.tick * self.money_per_price
        return max(0.0, budget / self.money_per_price)

    # ------------------------------------------------------------------- io
    @classmethod
    def load(cls, path=None):
        cfg = cls()
        if path:
            if not os.path.exists(path):
                raise ConfigError("config file not found: " + path)
            with open(path) as f:
                raw = json.load(f)
            known = {k: v for k, v in raw.items() if k in cfg.__dataclass_fields__}
            unknown = sorted(set(raw) - set(known))
            for k, v in known.items():
                setattr(cfg, k, v)
            cfg.extra = {"unknown_keys": unknown} if unknown else {}
        return cfg.validate()

    def save(self, path):
        os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
        with open(path, "w") as f:
            json.dump(asdict(self), f, indent=2, sort_keys=True)

    def summary(self):
        return {
            "symbol": self.symbol, "label": self.label, "timeframe_minutes": self.timeframe_minutes,
            "data_mode": self.data_mode, "data_path": self.data_path if self.data_mode != "csv" else self.csv_path,
            "lots": self.lots, "ounces_per_lot": self.ounces_per_lot,
            "money_per_price": self.money_per_price,
            "max_risk_usd": self.max_risk_usd, "max_stop_distance": round(self.max_stop_distance, 4),
            "min_rr": self.min_rr,
            "assumed_spread_ticks": self.assumed_spread_ticks,
            "commission_round_trip": self.commission_round_trip,
            "slippage_ticks": self.slippage_ticks,
            "enable_vix": self.enable_vix, "enable_gex": self.enable_gex,
            "execution": "none (advisory signals only)",
        }
