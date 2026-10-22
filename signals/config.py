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
    # live: a keyless public venue feed. provider="" tries the tested fallback chain.
    market: str = "xauusd"                   # xauusd | btc | eurusd - selects per-venue symbol spelling
    live_provider: str = ""                  # binance | kraken | coinbase | okx | bitstamp | bybit | deribit | yahoo | twelvedata
    live_symbol: str = ""                    # empty = resolved from market + provider
    live_range: str = "5d"                   # yahoo only
    live_limit: int = 500                    # bars requested per poll
    apikey_env: str = "TWELVEDATA_API_KEY"   # only for providers that need a key; never stored here
    csv_path: str = ""
    csv_has_header: bool = True
    max_bar_age_minutes: int = 30            # a bar older than this is reported stale

    # ---- contract / money -----------------------------------------------------
    lots: float = 0.01
    ounces_per_lot: float = 100.0            # XAUUSD: 1 lot = 100 oz -> 0.01 lot = 1 oz
    units_per_lot: float = 0.0               # overrides ounces_per_lot: BTC=1, EURUSD=100000, XAUUSD=100
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
    gex_source: str = "file"                 # file | deribit (keyless live BTC options chain)
    gex_currency: str = "BTC"
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
        if self.live_provider == "twelvedata" and not os.environ.get(self.apikey_env or "TWELVEDATA_API_KEY"):
            raise ConfigError("provider twelvedata needs the key in $%s (free plan: 800 req/day, 8/min)"
                              % (self.apikey_env or "TWELVEDATA_API_KEY"))
        if self.units_per_lot and not (0 < self.units_per_lot <= 10_000_000):
            raise ConfigError("units_per_lot must be in (0, 10000000]")
        if not (0 < self.ounces_per_lot <= 10000):
            raise ConfigError("ounces_per_lot must be in (0, 10000]")
        if self.market not in ("xauusd", "btc", "eurusd"):
            raise ConfigError("market must be xauusd|btc|eurusd")
        if self.live_provider and self.live_provider not in (
                "binance", "bybit", "okx", "coinbase", "kraken", "bitstamp", "deribit", "yahoo", "twelvedata"):
            raise ConfigError("unknown live_provider '%s'" % self.live_provider)
        money_per_price = self.money_per_price
        if money_per_price <= 0:
            raise ConfigError("lots * contract_units must be > 0 (money per 1.0 price unit)")
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
        self._validate_gex()
        if not (1 <= self.port <= 65535):
            raise ConfigError("port out of range")
        return self

    @property
    def contract_units(self):
        """Units of the underlying per 1.00 lot: 100 oz (XAUUSD), 1 BTC, 100000 EUR (EURUSD)."""
        return self.units_per_lot or self.ounces_per_lot

    @property
    def money_per_price(self):
        """USD P&L per 1.00 move in price for the configured volume."""
        return self.lots * self.contract_units

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

    def _validate_gex(self):
        if self.gex_source not in ("file", "deribit"):
            raise ConfigError("gex_source must be 'file' or 'deribit'")
        if self.gex_source == "deribit" and not self.enable_gex:
            raise ConfigError("gex_source=deribit needs enable_gex=true")
        return self

    def label_for_feed(self):
        """The label must describe the feed actually in use, not the default one."""
        m, p = self.market, (self.live_provider or "yahoo")
        if self.data_mode == "csv":
            return "%s (CSV export)" % self.symbol
        if self.data_mode != "live":
            return self.label
        if m == "xauusd":
            if p == "twelvedata":
                return "XAUUSD (spot, Twelve Data; no volume)"
            return "XAUUSD (proxy: COMEX GC=F)"
        if m == "eurusd":
            return "EURUSD (spot, %s; no volume)" % ("Twelve Data" if p == "twelvedata" else "Yahoo")
        return self.label

    def apply_preset(self, name):
        """Fills the instrument arithmetic for a well-known market. Rules and mandate are untouched."""
        p = (name or "").lower()
        if p == "xauusd":
            self.symbol = "XAUUSD"
            self.label, self.units_per_lot, self.tick = "XAUUSD (proxy: COMEX GC=F)", 100.0, 0.01
            self.market, self.live_provider, self.live_symbol = "xauusd", "yahoo", ""
            self.commission_round_trip, self.assumed_spread_ticks = 0.14, 30
            self.lots, self.require_order_flow = 0.01, True
        elif p == "btc":
            self.symbol = "BTCUSD"
            self.label = "BTCUSD (spot, venue feed)"
            self.units_per_lot, self.tick = 1.0, 0.1
            self.market, self.live_provider, self.live_symbol = "btc", "binance", ""
            self.data_mode = "live"          # there is no committed BTC snapshot to replay
            self.commission_round_trip, self.assumed_spread_ticks = 0.02, 2
            # 0.005 BTC ~ $500 notional at $100k: a 1% stop (~$1000) stays inside the $5 cap
            self.lots, self.require_order_flow = 0.005, True
            self.min_rr = 1.5
        elif p == "eurusd":
            self.symbol = "EURUSD"
            self.label = "EURUSD (spot, Yahoo; no volume)"
            self.units_per_lot, self.tick = 100000.0, 0.00001
            self.market, self.live_provider, self.live_symbol = "eurusd", "yahoo", ""
            self.data_mode = "live"         # there is no committed EURUSD snapshot to replay
            self.commission_round_trip, self.assumed_spread_ticks = 0.10, 10
            self.lots = 0.01
            # Yahoo FX has no volume: the order-flow proxy cannot work, so it is switched off
            # explicitly rather than silently producing zero-delta confirmations.
            self.require_order_flow = False
        else:
            raise ConfigError("unknown preset '%s' (xauusd | btc | eurusd)" % name)
        return self

    def save(self, path):
        os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
        with open(path, "w") as f:
            json.dump(asdict(self), f, indent=2, sort_keys=True)

    def summary(self):
        return {
            "symbol": self.symbol, "label": self.label, "timeframe_minutes": self.timeframe_minutes,
            "data_mode": self.data_mode,
            "data_source": (self.data_path if self.data_mode == "local" else
                            (self.csv_path if self.data_mode == "csv" else
                             "%s/%s" % (self.live_provider or "auto-chain",
                                        self.live_symbol or ("auto:" + self.market)))),
            "lots": self.lots, "ounces_per_lot": self.ounces_per_lot,
            "volume": "%.4f lot fixed = %.4f units (never resized)" % (self.lots, self.lots * self.contract_units),
            "contract_units": self.contract_units,
            "money_per_price": self.money_per_price,
            "max_risk_usd": self.max_risk_usd, "max_stop_distance": round(self.max_stop_distance, 4),
            "min_rr": self.min_rr,
            "assumed_spread_ticks": self.assumed_spread_ticks,
            "market": self.market, "live_provider": self.live_provider,
            "apikey_env": self.apikey_env,
            "key_present": bool(os.environ.get(self.apikey_env or "")),
            "commission_round_trip": self.commission_round_trip,
            "slippage_ticks": self.slippage_ticks,
            "enable_vix": self.enable_vix, "enable_gex": self.enable_gex,
            "gex_source": self.gex_source, "gex_currency": self.gex_currency,
            "execution": "none (advisory signals only)",
        }
