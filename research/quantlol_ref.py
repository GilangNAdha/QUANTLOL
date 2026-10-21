"""
QuantLOL reference engine (pure Python, stdlib only).

This is a line-by-line port of src/QuantLOL/Core/*.cs so that historical research
uses the SAME rules as the shipped cBot:

  * fractal swings confirmed k bars later (no repaint, no look-ahead)
  * BOS / CHoCH from closed bars only
  * liquidity sweeps against swings and context levels (>= N ticks pierce + reclaim)
  * fair value gaps and order blocks
  * session volume profiles (POC / VAH / VAL, initial balance)
  * CLV order-flow proxy from broker tick volume
  * deterministic confluence scoring + the hard per-trade USD risk cap
  * capital-protection state machine (daily loss, equity drawdown, cool-downs)

The C# unit test suite asserts the same invariants; tests/fixtures/parity.json
(run by tests/QuantLOL.Tests/ParityTests.cs) pins the two implementations together
on a deterministic synthetic series.
"""
from __future__ import annotations
import math
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone

# ----------------------------------------------------------------------------- data
@dataclass(slots=True)
class Bar:
    t: datetime
    o: float
    h: float
    l: float
    c: float
    v: float

    @property
    def range(self): return self.h - self.l


class SessionSettings:
    def __init__(self, asia=(0, 7), london=(7, 12), ny=(12, 21), friday_close=21, sunday_open=22):
        self.asia, self.london, self.ny = asia, london, ny
        self.friday_close, self.sunday_open = friday_close, sunday_open


def classify_session(t: datetime, s: SessionSettings) -> str:
    h = t.hour
    if s.asia[0] <= h < s.asia[1]: return "Asia"
    if s.london[0] <= h < s.london[1]: return "London"
    if s.ny[0] <= h < s.ny[1]: return "NewYork"
    return "OffHours"


def window_start(t: datetime, s: SessionSettings) -> datetime:
    day = t.replace(hour=0, minute=0, second=0, microsecond=0)
    k = classify_session(t, s)
    if k == "Asia": return day + timedelta(hours=s.asia[0])
    if k == "London": return day + timedelta(hours=s.london[0])
    if k == "NewYork": return day + timedelta(hours=s.ny[0])
    return t.replace(minute=0, second=0, microsecond=0)


def previous_session(t: datetime, s: SessionSettings):
    day = t.replace(hour=0, minute=0, second=0, microsecond=0)
    k = classify_session(t, s)
    if k == "Asia":
        d = day - timedelta(days=1)
        return d + timedelta(hours=s.ny[0]), d + timedelta(hours=s.ny[1])
    if k == "London":
        return day + timedelta(hours=s.asia[0]), day + timedelta(hours=s.asia[1])
    if k == "NewYork":
        return day + timedelta(hours=s.london[0]), day + timedelta(hours=s.london[1])
    if t.hour < s.asia[0]:
        d = day - timedelta(days=1)
        return d + timedelta(hours=s.ny[0]), d + timedelta(hours=s.ny[1])
    return day + timedelta(hours=s.ny[0]), day + timedelta(hours=s.ny[1])


def market_closed(t: datetime, s: SessionSettings) -> bool:
    wd = t.weekday()  # Mon=0 .. Sun=6
    if wd == 5: return True
    if wd == 6 and t.hour < s.sunday_open: return True
    if wd == 4 and t.hour >= s.friday_close: return True
    return False


# ------------------------------------------------------------------------- structure
@dataclass(slots=True)
class StructureOptions:
    swing_lookback: int = 2
    scan_bars: int = 320          # MUST match StructureOptions.ScanBars in C#
    tick: float = 0.01
    min_sweep_pierce_ticks: int = 2
    min_fvg_ticks: int = 10
    ob_lookback: int = 20
    sweep_window: int = 12
    dealing_swings: int = 4
    fvg_scan: int = 60


SUPPORT_KINDS = {"SwingLow", "SessionLow", "PrevSessionLow", "ValueAreaLow", "InitialBalanceLow", "PutWall", "GammaFlip"}
RESISTANCE_KINDS = {"SwingHigh", "SessionHigh", "PrevSessionHigh", "ValueAreaHigh", "InitialBalanceHigh", "CallWall"}


class Snapshot:
    __slots__ = ("swings", "trend", "last_bos_i", "last_bos_dir", "last_bos_level", "last_choch_i",
                 "last_choch_dir", "last_choch_level", "dealing_high", "dealing_low",
                 "bull_sweep", "bear_sweep", "bull_fvg", "bear_fvg", "bull_ob", "bear_ob", "atr", "n")

    def __init__(self):
        self.swings = []
        self.trend = 0
        self.last_bos_i = -1; self.last_bos_dir = 0; self.last_bos_level = 0.0
        self.last_choch_i = -1; self.last_choch_dir = 0; self.last_choch_level = 0.0
        self.dealing_high = 0.0; self.dealing_low = 0.0
        self.bull_sweep = None; self.bear_sweep = None
        self.bull_fvg = None; self.bear_fvg = None
        self.bull_ob = None; self.bear_ob = None
        self.atr = 0.0; self.n = 0


def atr(bars, start, end, period):
    start = max(1, start); end = min(end, len(bars) - 1)
    if end - start < 1: return 0.0
    a = 0.0; used = 0
    for i in range(start, end + 1):
        pc = bars[i - 1].c
        tr = max(bars[i].h - bars[i].l, abs(bars[i].h - pc), abs(bars[i].l - pc))
        if used < period:
            a += tr; used += 1
            if used == period: a /= period
        else:
            a = (a * (period - 1) + tr) / period
    return a


def analyze_structure(bars, last_index, opt: StructureOptions, context_levels=None):
    """Mirror of StructureAnalyzer.Analyze. context_levels: list[(price, kind)]."""
    snap = Snapshot()
    if last_index < 3: return snap
    k = max(1, opt.swing_lookback)
    start = max(0, last_index - max(20, opt.scan_bars) + 1)
    n = last_index - start + 1
    if n < 3 * k + 3: return snap
    snap.n = n
    snap.atr = atr(bars, start, last_index, min(14, n - 1))

    swings = []
    for i in range(start + k, last_index - k + 1):
        is_high = is_low = True
        for j in range(1, k + 1):
            if is_high and (bars[i].h <= bars[i - j].h or bars[i].h < bars[i + j].h): is_high = False
            if is_low and (bars[i].l >= bars[i - j].l or bars[i].l > bars[i + j].l): is_low = False
            if not is_high and not is_low: break
        if is_high and not is_low: swings.append((i, i + k, bars[i].h, True))
        elif is_low and not is_high: swings.append((i, i + k, bars[i].l, False))
    snap.swings = swings[-20:]

    ref_high = ref_low = None
    sw = 0
    for i in range(start + k, last_index + 1):
        while sw < len(swings) and swings[sw][1] <= i:
            if swings[sw][3]: ref_high = swings[sw]
            else: ref_low = swings[sw]
            sw += 1
        c = bars[i].c
        if ref_high is not None and c > ref_high[2]:
            snap.last_bos_i = i; snap.last_bos_dir = 1; snap.last_bos_level = ref_high[2]
            if snap.trend <= 0:
                snap.last_choch_i = i; snap.last_choch_dir = 1; snap.last_choch_level = ref_high[2]
            snap.trend = 1
            ob = find_order_block(bars, i, opt.ob_lookback, True)
            if ob: snap.bull_ob = ob
            ref_high = None
        elif ref_low is not None and c < ref_low[2]:
            snap.last_bos_i = i; snap.last_bos_dir = -1; snap.last_bos_level = ref_low[2]
            if snap.trend >= 0:
                snap.last_choch_i = i; snap.last_choch_dir = -1; snap.last_choch_level = ref_low[2]
            snap.trend = -1
            ob = find_order_block(bars, i, opt.ob_lookback, False)
            if ob: snap.bear_ob = ob
            ref_low = None

    highs = [s[2] for s in swings if s[3]][-opt.dealing_swings:]
    lows = [s[2] for s in swings if not s[3]][-opt.dealing_swings:]
    if highs and lows:
        snap.dealing_high = max(highs); snap.dealing_low = min(lows)
    else:
        rng = bars[start:last_index + 1]
        snap.dealing_high = max(b.h for b in rng); snap.dealing_low = min(b.l for b in rng)

    pierce = max(1, opt.min_sweep_pierce_ticks) * opt.tick
    sweep_start = max(start + k + 1, last_index - max(1, opt.sweep_window) + 1)
    recent_swings = swings[-24:]
    for i in range(sweep_start, last_index + 1):
        b = bars[i]
        for (sidx, conf, price, is_high) in recent_swings:
            if conf >= i: continue
            if not is_high:
                if b.l < price - pierce and b.c > price:
                    ev = {"i": i, "t": b.t, "level": price, "extreme": b.l, "kind": "SwingLow",
                          "dir": 1, "pierce_ticks": (price - b.l) / opt.tick}
                    if snap.bull_sweep is None or ev["i"] > snap.bull_sweep["i"] or (
                            ev["i"] == snap.bull_sweep["i"] and ev["level"] < snap.bull_sweep["level"]):
                        snap.bull_sweep = ev
            else:
                if b.h > price + pierce and b.c < price:
                    ev = {"i": i, "t": b.t, "level": price, "extreme": b.h, "kind": "SwingHigh",
                          "dir": -1, "pierce_ticks": (b.h - price) / opt.tick}
                    if snap.bear_sweep is None or ev["i"] > snap.bear_sweep["i"] or (
                            ev["i"] == snap.bear_sweep["i"] and ev["level"] > snap.bear_sweep["level"]):
                        snap.bear_sweep = ev
        for (lv, kind) in (context_levels or ()):
            if lv <= 0: continue
            if kind in SUPPORT_KINDS:
                if b.l < lv - pierce and b.c > lv:
                    ev = {"i": i, "t": b.t, "level": lv, "extreme": b.l, "kind": kind, "dir": 1,
                          "pierce_ticks": (lv - b.l) / opt.tick}
                    if snap.bull_sweep is None or ev["i"] > snap.bull_sweep["i"] or (
                            ev["i"] == snap.bull_sweep["i"] and ev["level"] < snap.bull_sweep["level"]):
                        snap.bull_sweep = ev
            elif kind in RESISTANCE_KINDS:
                if b.h > lv + pierce and b.c < lv:
                    ev = {"i": i, "t": b.t, "level": lv, "extreme": b.h, "kind": kind, "dir": -1,
                          "pierce_ticks": (b.h - lv) / opt.tick}
                    if snap.bear_sweep is None or ev["i"] > snap.bear_sweep["i"] or (
                            ev["i"] == snap.bear_sweep["i"] and ev["level"] > snap.bear_sweep["level"]):
                        snap.bear_sweep = ev

    fstart = max(start + 2, last_index - max(5, opt.fvg_scan) + 1)
    min_gap = max(1, opt.min_fvg_ticks) * opt.tick
    for i in range(fstart, last_index + 1):
        if bars[i].l > bars[i - 2].h + min_gap - opt.tick * 0.5:
            z = {"i": i, "bottom": bars[i - 2].h, "top": bars[i].l, "dir": 1, "filled": False}
            for j in range(i + 1, last_index + 1):
                if bars[j].c < z["bottom"]: z["filled"] = True; z["filled_i"] = j; break
            snap.bull_fvg = z
        if bars[i].h < bars[i - 2].l - min_gap + opt.tick * 0.5:
            z = {"i": i, "top": bars[i - 2].l, "bottom": bars[i].h, "dir": -1, "filled": False}
            for j in range(i + 1, last_index + 1):
                if bars[j].c > z["top"]: z["filled"] = True; z["filled_i"] = j; break
            snap.bear_fvg = z
    return snap


def find_order_block(bars, bos_i, lookback, bullish):
    for i in range(bos_i - 1, max(0, bos_i - max(1, lookback)) - 1, -1):
        opposing = bars[i].c < bars[i].o if bullish else bars[i].c > bars[i].o
        if opposing:
            return {"top": bars[i].h, "bottom": bars[i].l, "i": i}
    return None


# --------------------------------------------------------------------------- profile
def build_profile(bars, start_i, end_i, bin_size, value_area_pct=70.0, ib_bars=12):
    """Mirror of MarketProfileBuilder.Build: uniform volume distribution over each bar range."""
    if start_i < 0 or end_i < start_i or bin_size <= 0: return None
    hi = max(b.h for b in bars[start_i:end_i + 1]); lo = min(b.l for b in bars[start_i:end_i + 1])
    if hi <= lo: return None
    rng = hi - lo
    if rng / bin_size > 4000: bin_size = rng / 4000
    floor = math.floor(lo / bin_size) * bin_size
    bins = int(math.ceil((hi - floor) / bin_size)) + 1
    vols = [0.0] * bins
    for b in bars[start_i:end_i + 1]:
        if b.v <= 0: continue
        if b.h <= b.l:
            k = int((b.c - floor) / bin_size)
            if 0 <= k < bins: vols[k] += b.v
            continue
        span = b.h - b.l
        first = max(0, int(math.floor((b.l - floor) / bin_size)))
        last = min(bins - 1, int(math.floor((b.h - floor) / bin_size)))
        for k in range(first, last + 1):
            blo = floor + k * bin_size; bhi = blo + bin_size
            ov = min(bhi, b.h) - max(blo, b.l)
            if ov > 0: vols[k] += b.v * (ov / span)
    poc = max(range(bins), key=lambda k: (vols[k], -k))
    total = sum(vols)
    target = total * min(0.99, max(0.1, value_area_pct / 100.0))
    acc = vols[poc]; lo_i = hi_i = poc
    while acc < target and (lo_i > 0 or hi_i < bins - 1):
        v_up = vols[hi_i + 1] if hi_i < bins - 1 else -1
        v_dn = vols[lo_i - 1] if lo_i > 0 else -1
        if v_up >= v_dn: hi_i += 1; acc += vols[hi_i]
        else: lo_i -= 1; acc += vols[lo_i]
    ib_end = min(end_i, start_i + ib_bars - 1)
    return {
        "poc": floor + (poc + 0.5) * bin_size,
        "val": floor + lo_i * bin_size,
        "vah": floor + (hi_i + 1) * bin_size,
        "high": hi, "low": lo, "total_volume": total,
        "ib_high": max(b.h for b in bars[start_i:ib_end + 1]),
        "ib_low": min(b.l for b in bars[start_i:ib_end + 1]),
        "partial": start_i <= 0,
    }


def order_flow(bars, last_i, window):
    start = max(0, last_i - window + 1)
    delta = buy = sell = total = 0.0
    for b in bars[start:last_i + 1]:
        rng = b.h - b.l
        clv = 0.0 if rng <= 0 else ((b.c - b.l) - (b.h - b.c)) / rng
        delta += b.v * max(-1.0, min(1.0, clv))
        buy += b.v * (clv + 1) / 2.0
        sell += b.v - b.v * (clv + 1) / 2.0
        total += b.v
    ratio = delta / total if total > 0 else 0.0
    mid = start + (last_i - start) // 2
    lows = [b.l for b in bars[start:mid + 1]]; lows2 = [b.l for b in bars[mid + 1:last_i + 1]]
    highs = [b.h for b in bars[start:mid + 1]]; highs2 = [b.h for b in bars[mid + 1:last_i + 1]]
    div_bull = div_bear = False
    if lows and lows2 and highs and highs2:
        d1 = sum(_clv_delta(b) for b in bars[start:mid + 1])
        d2 = sum(_clv_delta(b) for b in bars[mid + 1:last_i + 1])
        div_bull = min(lows2) < min(lows) and d2 > d1
        div_bear = max(highs2) > max(highs) and d2 < d1
    return {"delta": delta, "ratio": ratio, "buy": buy, "sell": sell, "total": total,
            "div_bull": div_bull, "div_bear": div_bear, "n": last_i - start + 1}


def _clv_delta(b):
    rng = b.h - b.l
    clv = 0.0 if rng <= 0 else ((b.c - b.l) - (b.h - b.c)) / rng
    return b.v * max(-1.0, min(1.0, clv))


# ----------------------------------------------------------------------------- risk
@dataclass
class RiskCaps:
    max_risk_usd: float = 5.0
    daily_loss_usd: float = 15.0
    equity_dd_usd: float = 30.0
    max_positions: int = 1
    min_rr: float = 1.5
    enforce_cap: bool = True


def planned_risk(entry, stop, target, money_per_price, commission_rt, slippage_rt):
    dist = abs(entry - stop)
    dirn = 1 if entry > stop else (-1 if entry < stop else 0)
    reward = max(0.0, (target - entry) * dirn) if (dirn and target > 0) else 0.0
    risk = dist * money_per_price + commission_rt + slippage_rt
    net_reward = reward * money_per_price - commission_rt - slippage_rt
    return {"dist": dist, "risk": risk, "reward": reward, "net_reward": net_reward,
            "rr_net": (net_reward / risk) if risk > 0 else 0.0,
            "rr_gross": (reward * money_per_price / (dist * money_per_price)) if dist > 0 else 0.0}


class DrawdownGuard:
    def __init__(self, reference, daily_loss, equity_dd, latch=True):
        self.reference = reference; self.daily_loss = daily_loss; self.equity_dd = equity_dd
        self.latch = latch; self.day = None; self.day_start_equity = reference
        self.realized_today = 0.0; self.latched = False; self.peak = reference

    def roll(self, t, equity):
        d = t.date()
        if self.day != d:
            self.day = d; self.day_start_equity = equity; self.realized_today = 0.0

    def on_equity(self, equity, t):
        self.roll(t, equity)
        self.peak = max(self.peak, equity)
        if self.latch and self.reference - equity >= self.equity_dd: self.latched = True

    def on_close(self, pnl, t, equity):
        self.roll(t, equity); self.realized_today += pnl

    def evaluate(self, equity, t):
        self.roll(t, equity)
        if self.latched: return False, "kill switch latched (lifetime drawdown)", self.realized_today
        if self.realized_today <= -abs(self.daily_loss):
            return False, "daily loss limit (%.2f)" % self.realized_today, self.realized_today
        dd = self.day_start_equity - equity
        if dd >= abs(self.equity_dd): return False, "equity drawdown %.2f" % dd, self.realized_today
        return True, "", self.realized_today


# --------------------------------------------------------------------------- signal
@dataclass
class SignalWeights:
    sweep: float = 2.0
    confirmation: float = 2.0
    location: float = 1.0
    order_flow: float = 1.0
    fvg: float = 0.5
    order_block: float = 0.5
    gex: float = 1.0
    vix: float = 0.5
    amt: float = 0.5


@dataclass
class EngineConfig:
    tick: float = 0.01
    money_per_price: float = 1.0            # 1 oz
    commission_rt: float = 0.14
    slippage_ticks: int = 5
    spread_ticks: int = 30
    max_spread_ticks: int = 40
    stop_buffer_ticks: int = 5
    min_score: float = 4.0
    require_location: bool = True
    require_confirmation: bool = True
    require_order_flow: bool = True
    enable_fvg: bool = True
    enable_order_block: bool = True
    enable_amt: bool = True
    enable_gex: bool = False
    enable_vix: bool = True
    block_on_high_vix: bool = True
    vix_high: float = 30.0
    fallback_rr: float = 2.0
    allow_synthetic_target: bool = True
    sweep_window: int = 12
    confirm_window: int = 12
    location_proximity_ticks: int = 60
    weights: SignalWeights = field(default_factory=SignalWeights)
    structure: StructureOptions = field(default_factory=StructureOptions)
    sessions: SessionSettings = field(default_factory=SessionSettings)
    enable_asia: bool = True
    enable_london: bool = True
    enable_ny: bool = True
    cooldown_trade_min: int = 5
    cooldown_loss_min: int = 15
    break_even_r: float = 1.0
    trail_start_r: float = 1.5
    trail_atr_mult: float = 1.0
    time_stop_min: int = 240
    max_entry_drift_ticks: int = 8
    daily_loss: float = 15.0
    equity_dd: float = 30.0
    max_risk: float = 5.0
    min_rr: float = 1.5
    lots: float = 0.01
    units: float = 1.0
    skip_weekend: bool = True
    reject_calendar_closed: bool = True
    # Research switch: when False the account-level daily/lifetime limits are
    # tracked but NOT enforced. Only the backtest pipeline sets this False, and
    # only for full-sample module comparison; the cBot always enforces them.
    enforce_account_limits: bool = True


def _candidate(cfg, snap, i, price, of, d, dirn, levels, vix):
    """Mirror of SignalEngine.BuildCandidate."""
    c = {"dir": dirn, "core": False, "has_sweep": False, "score": 0.0, "evidence": [], "reject": None}
    sweep = snap.bull_sweep if dirn > 0 else snap.bear_sweep
    if sweep is None or i - sweep["i"] > cfg.sweep_window:
        c["reject"] = ("no %s sweep within %d bars" % ("bullish" if dirn > 0 else "bearish", cfg.sweep_window))
        return c
    confirm = (snap.last_choch_dir == dirn and snap.last_choch_i >= max(0, sweep["i"]) - 1
               and (i - snap.last_choch_i) <= cfg.confirm_window)
    if cfg.require_confirmation and not confirm:
        c["reject"] = "no confirmed %s shift after sweep" % ("bullish" if dirn > 0 else "bearish")
        return c
    c["core"] = True; c["has_sweep"] = True
    c["sweep_extreme"] = sweep["extreme"]
    c["sweep_kind"] = sweep["kind"]
    c["score"] += cfg.weights.sweep + cfg.weights.confirmation
    c["evidence"].append("%s_sweep:%s@pierce%.0ft" % ("bull" if dirn > 0 else "bear", sweep["kind"], sweep["pierce_ticks"]))

    mid = (snap.dealing_high + snap.dealing_low) / 2.0
    in_discount = snap.dealing_high > snap.dealing_low and price <= mid
    in_premium = snap.dealing_high > snap.dealing_low and price >= mid
    loc_ok = in_discount if dirn > 0 else in_premium
    prox = abs(price - sweep["level"]) / cfg.tick
    near = prox <= cfg.location_proximity_ticks
    if loc_ok or near:
        c["score"] += cfg.weights.location
        c["evidence"].append("discount" if dirn > 0 else "premium")
    elif cfg.require_location:
        c["core"] = False
        c["reject"] = "price not in %s" % ("discount" if dirn > 0 else "premium")
        return c

    if d.get("session_profile"):
        p = d["session_profile"]
        if dirn > 0 and price <= p["val"]: c["score"] += cfg.weights.amt; c["evidence"].append("below_val")
        elif dirn < 0 and price >= p["vah"]: c["score"] += cfg.weights.amt; c["evidence"].append("above_vah")
        elif p["val"] <= price <= p["vah"]: c["evidence"].append("inside_value")

    if of is not None and of["n"] > 1:
        of_ok = ((of["ratio"] > 0 or of["div_bull"]) if dirn > 0
                 else (of["ratio"] < 0 or of["div_bear"]))
        if of_ok:
            c["score"] += cfg.weights.order_flow
            c["evidence"].append("of:%+.2f" % of["ratio"])
        elif cfg.require_order_flow:
            c["core"] = False
            c["reject"] = "order-flow proxy does not confirm (%.2f)" % of["ratio"]
            return c

    if cfg.enable_fvg:
        fvg = snap.bull_fvg if dirn > 0 else snap.bear_fvg
        if fvg and not fvg["filled"] and (i - fvg["i"]) <= 40:
            c["score"] += cfg.weights.fvg
            c["evidence"].append("fvg")
    if cfg.enable_order_block:
        ob = snap.bull_ob if dirn > 0 else snap.bear_ob
        if ob: c["score"] += cfg.weights.order_block; c["evidence"].append("ob")
    if cfg.enable_vix and vix is not None and vix["valid"] and vix["level"] < cfg.vix_high:
        c["score"] += cfg.weights.vix; c["evidence"].append("vix_ok")
    return c


def select_target(levels, dirn, entry, risk_dist, min_rr, fallback_rr, allow_synthetic):
    min_dist = risk_dist * min_rr
    best = None; best_dist = float("inf")
    for (price, kind) in levels:
        if price <= 0: continue
        ahead = price > entry if dirn > 0 else price < entry
        if not ahead: continue
        dist = abs(price - entry)
        if dist < min_dist: continue
        if dist < best_dist: best_dist = dist; best = price
    if best is not None: return best, False
    if not allow_synthetic: return None, False
    return (entry + risk_dist * fallback_rr if dirn > 0 else entry - risk_dist * fallback_rr), True


def evaluate_signal(cfg, bars, i, snap, session_profile, prev_profile, of, targets, vix):
    """Mirror of SignalEngine.Evaluate. Returns a decision dict."""
    spread_ticks = cfg.spread_ticks
    if spread_ticks > cfg.max_spread_ticks:
        return {"state": "neutral", "reject": "SpreadTooWide", "detail": "spread %d > %d" % (spread_ticks, cfg.max_spread_ticks)}
    if cfg.enable_vix and vix and vix["valid"] and cfg.block_on_high_vix and vix["level"] >= cfg.vix_high:
        return {"state": "neutral", "reject": "VixFilter", "detail": "VIX %.1f >= %.1f" % (vix["level"], cfg.vix_high)}

    price = bars[i].c
    d = {"session_profile": session_profile}
    longs = _candidate(cfg, snap, i, price, of, d, +1, targets, vix)
    shorts = _candidate(cfg, snap, i, price, of, d, -1, targets, vix)
    if longs["core"] and shorts["core"]:
        return {"state": "neutral", "reject": "Conflict", "detail": "both directions valid"}
    if not longs["core"] and not shorts["core"]:
        rel = longs if longs["has_sweep"] else (shorts if shorts["has_sweep"] else None)
        return {"state": "neutral", "reject": "NoSetup", "detail": (rel or {}).get("reject") or "no sweep+confirmation"}
    c = longs if longs["core"] else shorts
    if c["score"] < cfg.min_score:
        return {"state": "neutral", "reject": "ScoreBelowThreshold", "dir": c["dir"],
                "detail": "score %.1f < %.1f" % (c["score"], cfg.min_score)}

    entry = price
    buffer = cfg.stop_buffer_ticks * cfg.tick
    if c["dir"] > 0:
        structural = c["sweep_extreme"]
        if cfg.enable_order_block and snap.bull_ob and snap.bull_ob["bottom"] < structural:
            structural = min(structural, snap.bull_ob["bottom"])
        stop = structural - buffer
    else:
        structural = c["sweep_extreme"]
        if cfg.enable_order_block and snap.bear_ob and snap.bear_ob["top"] > structural:
            structural = max(structural, snap.bear_ob["top"])
        stop = structural + buffer
    stop = round(stop / cfg.tick) * cfg.tick
    risk_dist = abs(entry - stop)
    if risk_dist <= 0:
        return {"state": "neutral", "reject": "NoSetup", "dir": c["dir"], "detail": "degenerate stop"}

    target, synthetic = select_target(targets, c["dir"], entry, risk_dist, cfg.min_rr, cfg.fallback_rr, cfg.allow_synthetic_target)
    if target is None:
        return {"state": "neutral", "reject": "RewardTooLow", "dir": c["dir"], "detail": "no structural target"}
    target = round(target / cfg.tick) * cfg.tick

    slip = cfg.slippage_ticks * cfg.tick * cfg.money_per_price
    r = planned_risk(entry, stop, target, cfg.money_per_price, cfg.commission_rt, slip)
    if r["risk"] > cfg.max_risk:
        return {"state": "neutral", "reject": "RiskCapExceeded", "dir": c["dir"],
                "detail": "planned risk %.2f > cap %.2f" % (r["risk"], cfg.max_risk),
                "risk_usd": r["risk"], "stop_distance": r["dist"], "entry": entry, "stop": stop, "target": target}
    if r["rr_net"] < cfg.min_rr:
        return {"state": "neutral", "reject": "RewardTooLow", "detail": "net R:R %.2f" % r["rr_net"],
                "risk_usd": r["risk"], "entry": entry, "stop": stop, "target": target}
    return {"state": "bull" if c["dir"] > 0 else "bear", "dir": c["dir"], "entry": entry, "stop": stop,
            "target": target, "risk_usd": r["risk"], "rr_net": r["rr_net"], "score": c["score"],
            "evidence": ";".join(c["evidence"]), "synthetic": synthetic,
            "setup": "%s_sweep+%s_choch" % (c.get("sweep_kind", "level"),
                                            "bull" if c["dir"] > 0 else "bear")}
