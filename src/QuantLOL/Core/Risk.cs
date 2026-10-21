using System;

namespace QuantLOL.Core
{
    public enum RejectReason
    {
        None = 0,
        SpecUnverified = 1,
        VolumeNotAccepted = 2,
        MarketClosed = 3,
        Disconnected = 4,
        QuoteStale = 5,
        SpreadTooWide = 6,
        DataStale = 7,
        NoSetup = 8,
        LocationFilter = 9,
        ConfirmationMissing = 10,
        OrderFlowFilter = 11,
        VixFilter = 12,
        Conflict = 13,
        ScoreBelowThreshold = 14,
        RiskCapExceeded = 15,
        RewardTooLow = 16,
        MaxPositionsReached = 17,
        DailyLossLimit = 18,
        EquityDrawdownLimit = 19,
        Cooldown = 20,
        DuplicateSuppressed = 21,
        EntryDrift = 22,
        ExternalDataRequired = 23,
        PositionStateUnknown = 24,
        OrderRejected = 25,
        Exception = 26,
        Disabled = 27
    }

    public sealed class RiskCaps
    {
        public double MaxRiskPerTradeUsd = 5.0;
        public double DailyLossLimitUsd = 15.0;
        public double EquityDrawdownLimitUsd = 30.0;
        public int MaxOpenPositions = 1;
        public double MinRewardRisk = 1.5;
        public bool EnforceRiskCap = true;
    }

    public readonly struct RiskNumbers
    {
        public readonly double StopDistance, StopTicks, RiskUsd, CommissionUsd, SlippageUsd;
        public readonly double RewardDistance, RewardUsd, NetRewardUsd, RrGross, RrNet;
        public RiskNumbers(double stopDistance, double stopTicks, double riskUsd, double commissionUsd,
                           double slippageUsd, double rewardDistance, double rewardUsd, double netRewardUsd,
                           double rrGross, double rrNet)
        {
            StopDistance = stopDistance; StopTicks = stopTicks; RiskUsd = riskUsd; CommissionUsd = commissionUsd;
            SlippageUsd = slippageUsd; RewardDistance = rewardDistance; RewardUsd = rewardUsd;
            NetRewardUsd = netRewardUsd; RrGross = rrGross; RrNet = rrNet;
        }
    }

    /// <summary>
    /// Money math for a FIXED volume. moneyPerPriceUnit is the account-currency P&amp;L of a
    /// 1.0 price move for that exact volume (for 0.01 lot XAUUSD = 1 oz it is 1.0 USD).
    /// It is derived from the broker's own AmountRisked() at startup, never assumed.
    /// </summary>
    public static class RiskMath
    {
        public static RiskNumbers Compute(double entry, double stop, double target, double moneyPerPriceUnit,
                                          double commissionRoundTripUsd, double slippageRoundTripUsd)
        {
            double stopDist = Math.Abs(entry - stop);
            double rewardDist = target > 0 ? (target - entry) : 0;
            if (moneyPerPriceUnit <= 0 || !Numeric.IsFinite(moneyPerPriceUnit))
                return new RiskNumbers(stopDist, 0, double.NaN, commissionRoundTripUsd, slippageRoundTripUsd, rewardDist, double.NaN, double.NaN, 0, 0);

            double grossRisk = stopDist * moneyPerPriceUnit;
            double riskUsd = grossRisk + commissionRoundTripUsd + slippageRoundTripUsd;
            double grossReward = rewardDist * moneyPerPriceUnit;
            double netReward = grossReward - commissionRoundTripUsd - slippageRoundTripUsd;
            double rrGross = grossRisk > 0 ? grossReward / grossRisk : 0;
            double rrNet = riskUsd > 0 ? netReward / riskUsd : 0;
            return new RiskNumbers(stopDist, 0, riskUsd, commissionRoundTripUsd, slippageRoundTripUsd,
                                   rewardDist, grossReward, netReward, rrGross, rrNet);
        }

        /// <summary>
        /// Approves a plan only when the FULL structural stop fits the monetary cap.
        /// The stop is never tightened to force an entry: this function only accepts/rejects.
        /// </summary>
        public static bool Approve(in RiskNumbers n, RiskCaps caps, out string reason)
        {
            reason = null;
            if (!Numeric.IsFinite(n.RiskUsd) || n.RiskUsd <= 0) { reason = "risk not computable"; return false; }
            if (caps.EnforceRiskCap && n.RiskUsd > caps.MaxRiskPerTradeUsd + 1e-9)
            {
                reason = "planned risk " + Numeric.F(n.RiskUsd, 2) + " > cap " + Numeric.F(caps.MaxRiskPerTradeUsd, 2) +
                         " (stop distance " + Numeric.F(n.StopDistance, 3) + ")";
                return false;
            }
            if (n.RewardDistance <= 0 || !Numeric.IsFinite(n.RrNet))
            {
                reason = "no valid target";
                return false;
            }
            if (n.RrNet < caps.MinRewardRisk)
            {
                reason = "net R:R " + Numeric.F(n.RrNet, 2) + " < " + Numeric.F(caps.MinRewardRisk, 2) +
                         " (net reward " + Numeric.F(n.NetRewardUsd, 2) + ")";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Maximum stop distance (price units) that still fits the cap, used for pre-filtering
        /// and for reporting. If the structural stop is wider, the trade is skipped, not resized.
        /// </summary>
        public static double MaxStopDistanceForCap(RiskCaps caps, double moneyPerPriceUnit, double commissionUsd, double slippageUsd)
        {
            if (moneyPerPriceUnit <= 0) return 0;
            double budget = caps.MaxRiskPerTradeUsd - commissionUsd - slippageUsd;
            return budget <= 0 ? 0 : budget / moneyPerPriceUnit;
        }
    }

    public sealed class GuardState
    {
        public bool Allowed;
        public string Reason = "";
        public double DailyRealizedPnl;
        public double EquityDrawdown;
        public double Equity;
        public double DayStartEquity;
        public double ReferenceBalance;
        public bool KillSwitchLatched;
    }

    /// <summary>
    /// Capital-protection state machine. Pure and deterministic so it can be unit tested.
    /// Definitions (also in docs/RISK_MODEL.md):
    ///   * Daily loss limit  : realized net PnL of the current UTC day &lt;= -DailyLossLimit.
    ///   * Equity drawdown   : DayStartEquity - current equity &gt;= EquityDrawdownLimit (blocks the day).
    ///   * Lifetime drawdown : ReferenceBalance - current equity &gt;= EquityDrawdownLimit (latches a kill switch).
    /// Open-position PnL is included because equity is used, not balance.
    /// </summary>
    public sealed class DrawdownGuard
    {
        public double ReferenceBalance { get; private set; }
        public double DailyLossLimitUsd;
        public double EquityDrawdownLimitUsd;
        public bool DailyLimitEnabled = true;
        public bool EquityLimitEnabled = true;
        public bool LatchKillSwitch = true;

        DateTime _day = DateTime.MinValue;
        double _dayStartEquity;
        double _realizedToday;
        double _peakEquity;
        bool _killLatched;

        public DrawdownGuard(double referenceBalance, double dailyLossLimitUsd, double equityDrawdownLimitUsd)
        {
            ReferenceBalance = referenceBalance;
            DailyLossLimitUsd = dailyLossLimitUsd;
            EquityDrawdownLimitUsd = equityDrawdownLimitUsd;
            _peakEquity = referenceBalance;
        }

        public double DailyRealizedPnl => _realizedToday;
        public DateTime CurrentDay => _day;
        public bool KillSwitchLatched => _killLatched;

        public void OnEquity(double equity, DateTime utc)
        {
            RollDay(utc, equity);
            if (equity > _peakEquity) _peakEquity = equity;
            if (LatchKillSwitch && EquityLimitEnabled && ReferenceBalance - equity >= EquityDrawdownLimitUsd)
                _killLatched = true;
        }

        public void OnTradeClosed(double netPnl, DateTime utc, double equity)
        {
            RollDay(utc, equity);
            _realizedToday += netPnl;
        }

        void RollDay(DateTime utc, double equity)
        {
            var d = utc.Date;
            if (d == _day) return;
            _day = d;
            _dayStartEquity = equity;
            _realizedToday = 0;
        }

        public GuardState Evaluate(double equity, DateTime utc)
        {
            RollDay(utc, equity);
            var s = new GuardState
            {
                Equity = equity,
                DayStartEquity = _dayStartEquity,
                ReferenceBalance = ReferenceBalance,
                DailyRealizedPnl = _realizedToday,
                EquityDrawdown = _dayStartEquity - equity,
                KillSwitchLatched = _killLatched,
                Allowed = true
            };

            if (_killLatched)
            {
                s.Allowed = false;
                s.Reason = "kill switch latched: lifetime drawdown >= " + Numeric.F(EquityDrawdownLimitUsd, 2);
                return s;
            }
            if (DailyLimitEnabled && _realizedToday <= -Math.Abs(DailyLossLimitUsd))
            {
                s.Allowed = false;
                s.Reason = "daily loss limit reached (" + Numeric.F(_realizedToday, 2) + " <= -" + Numeric.F(Math.Abs(DailyLossLimitUsd), 2) + ")";
                return s;
            }
            if (EquityLimitEnabled && s.EquityDrawdown >= Math.Abs(EquityDrawdownLimitUsd))
            {
                s.Allowed = false;
                s.Reason = "equity drawdown " + Numeric.F(s.EquityDrawdown, 2) + " >= " + Numeric.F(Math.Abs(EquityDrawdownLimitUsd), 2) + " (day reference)";
                return s;
            }
            return s;
        }

        public void ResetKillSwitch(double referenceBalance)
        {
            _killLatched = false;
            ReferenceBalance = referenceBalance;
            _peakEquity = referenceBalance;
        }
    }

    /// <summary>Duplicate-signal suppression, cool-downs and "one position at a time" bookkeeping.</summary>
    public sealed class TradingGate
    {
        public DateTime LastEntryUtc = DateTime.MinValue;
        public DateTime LastLossUtc = DateTime.MinValue;
        public string LastSignalKey = "";
        public int ConsecutiveLosses;

        public bool CanOpen(DateTime nowUtc, int cooldownAfterTradeMinutes, int cooldownAfterLossMinutes, out string reason)
        {
            reason = null;
            if (LastEntryUtc != DateTime.MinValue && cooldownAfterTradeMinutes > 0)
            {
                var elapsed = (nowUtc - LastEntryUtc).TotalMinutes;
                if (elapsed >= 0 && elapsed < cooldownAfterTradeMinutes)
                {
                    reason = "cool-down after trade: " + Numeric.F(cooldownAfterTradeMinutes - elapsed, 1) + " min left";
                    return false;
                }
            }
            if (LastLossUtc != DateTime.MinValue && cooldownAfterLossMinutes > 0)
            {
                var elapsed = (nowUtc - LastLossUtc).TotalMinutes;
                if (elapsed >= 0 && elapsed < cooldownAfterLossMinutes)
                {
                    reason = "cool-down after loss: " + Numeric.F(cooldownAfterLossMinutes - elapsed, 1) + " min left";
                    return false;
                }
            }
            return true;
        }

        public bool IsDuplicate(string key) => !string.IsNullOrEmpty(key) && string.Equals(key, LastSignalKey, StringComparison.Ordinal);

        public void OnEntry(DateTime nowUtc, string signalKey)
        {
            LastEntryUtc = nowUtc;
            LastSignalKey = signalKey ?? "";
        }

        public void OnExit(DateTime nowUtc, double netPnl)
        {
            if (netPnl < 0) { LastLossUtc = nowUtc; ConsecutiveLosses++; }
            else ConsecutiveLosses = 0;
        }
    }
}
