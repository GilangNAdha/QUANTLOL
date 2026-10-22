using System;
using cAlgo.API;
using cAlgo.API.Internals;
using QuantLOL.Core;

namespace QuantLOL.Runtime
{
    public sealed class EntryPlan
    {
        public TradeType Type;
        public double Units;
        public double DecisionPrice;
        public double StopPrice;
        public double TargetPrice;
        public string Label = "";
        public string Comment = "";
        public string SignalKey = "";
        public double Score;
        public string SetupType = "";
        public string Session = "";
        public string Regime = "";
        public string ExternalStatus = "";
        public string Filters = "";
        public double PlannedRiskUsd;
        public double SpreadTicks;
        public double SlippageAllowanceUsd;
        public bool SyntheticTarget;
        public DateTime SignalTimeUtc;
        public double DecisionLatencyMs;
        public long T3Submit;
        public long T0Decision;
    }

    /// <summary>
    /// Order submission, acknowledgement handling, protection verification and in-flight
    /// de-duplication. Exactly one order can be in flight at any time; a second submission
    /// is impossible until the first resolves. Protective stops are set at submission and
    /// then snapped to the exact structural price with ProtectionType.Absolute.
    /// </summary>
    public sealed class ExecutionEngine
    {
        readonly Robot _bot;
        readonly Action<string, bool> _log;
        public BrokerSpec Spec;
        public LatencyRegistry Latencies;

        public bool InFlight { get; private set; }
        public DateTime InFlightSinceUtc { get; private set; }
        public string InFlightLabel { get; private set; } = "";
        public EntryPlan CurrentPlan { get; private set; }
        public Position Position { get; private set; }
        public TradeRecord Working { get; private set; }
        public int SubmitCount { get; private set; }
        public int RejectCount { get; private set; }
        public int TimeoutCount { get; private set; }
        public string LastError { get; private set; } = "";
        public bool Enabled = true;
        public int OrderTimeoutMs = 8000;
        public double MaxSlippageTicks = 20;
        public bool UseAbsoluteProtection = true;

        public event Action<TradeRecord> TradeClosed;
        public event Action<string, RejectReason> EntryFailed;

        public ExecutionEngine(Robot bot, Action<string, bool> log)
        {
            _bot = bot;
            _log = log;
        }

        public void BindPosition(Position p)
        {
            Position = p;
        }

        /// <summary>Submits a market order. Synchronous in backtests (deterministic), async live.</summary>
        public bool SubmitEntry(EntryPlan plan, long t3Submit)
        {
            if (!Enabled) return false;
            if (InFlight)
            {
                _log("entry suppressed: order already in flight (" + InFlightLabel + ")", false);
                return false;
            }
            if (Spec == null || !Spec.Verified)
            {
                Fail(plan, "spec not verified: " + (Spec?.Reason ?? "null"), RejectReason.SpecUnverified);
                return false;
            }
            if (plan.StopPrice <= 0 || plan.TargetPrice <= 0)
            {
                Fail(plan, "invalid stop/target", RejectReason.NoSetup);
                return false;
            }

            double pip = Spec.PipSize > 0 ? Spec.PipSize : Spec.TickSize;
            double slPips = Math.Abs(plan.DecisionPrice - plan.StopPrice) / pip;
            double tpPips = Math.Abs(plan.TargetPrice - plan.DecisionPrice) / pip;
            if (Spec.MinStopLossDistance > 0 && Math.Abs(plan.DecisionPrice - plan.StopPrice) < Spec.MinStopLossDistance - 1e-9)
            {
                Fail(plan, "stop distance " + Numeric.F(Math.Abs(plan.DecisionPrice - plan.StopPrice), 4) +
                           " < broker minimum " + Numeric.F(Spec.MinStopLossDistance, 4), RejectReason.RiskCapExceeded);
                return false;
            }
            if (slPips <= 0 || tpPips <= 0)
            {
                Fail(plan, "degenerate SL/TP in pips", RejectReason.NoSetup);
                return false;
            }

            InFlight = true;
            InFlightSinceUtc = _bot.Server.TimeInUtc;
            InFlightLabel = plan.Label;
            CurrentPlan = plan;
            SubmitCount++;

            try
            {
                if (_bot.IsBacktesting)
                {
                    var r = _bot.ExecuteMarketOrder(plan.Type, _bot.SymbolName, plan.Units, plan.Label,
                                                    slPips, tpPips, plan.Comment);
                    HandleResult(r, plan, t3Submit);
                }
                else
                {
                    _bot.ExecuteMarketOrderAsync(plan.Type, _bot.SymbolName, plan.Units, plan.Label,
                                                 slPips, tpPips, plan.Comment, r => HandleResult(r, plan, t3Submit));
                }
                return true;
            }
            catch (Exception ex)
            {
                InFlight = false;
                LastError = ex.GetType().Name + ": " + ex.Message;
                _log("entry exception: " + LastError, true);
                RaiseFailed(plan, "exception: " + LastError, RejectReason.Exception);
                return false;
            }
        }

        void HandleResult(TradeResult result, EntryPlan plan, long t3Submit)
        {
            long t4 = MonotonicClock.Timestamp;
            Latencies?.SubmitToAck.RecordMs(MonotonicClock.MsBetween(t3Submit, t4));
            InFlight = false;

            if (result == null)
            {
                RejectCount++;
                LastError = "null TradeResult";
                _log("order result: null (state unknown)", true);
                RaiseFailed(plan, "null result, position state unknown", RejectReason.PositionStateUnknown);
                return;
            }
            if (!result.IsSuccessful)
            {
                RejectCount++;
                string why = result.Error.HasValue ? result.Error.Value.ToString() : "unknown";
                LastError = why;
                _log("order rejected by broker: " + why + " label=" + plan.Label, true);
                RaiseFailed(plan, "broker rejected: " + why, MapError(result.Error));
                return;
            }

            var pos = result.Position;
            if (pos == null)
            {
                var found = _bot.Positions.Find(plan.Label, _bot.SymbolName);
                pos = found;
            }
            if (pos == null)
            {
                TimeoutCount++;
                _log("order ack ok but no position visible yet; will reconcile label=" + plan.Label, true);
                RaiseFailed(plan, "ack without visible position", RejectReason.PositionStateUnknown);
                return;
            }

            long t5 = MonotonicClock.Timestamp;
            Latencies?.SubmitToFill.RecordMs(MonotonicClock.MsBetween(t3Submit, t5));
            if (plan.T0Decision > 0) Latencies?.DecisionToFill.RecordMs(MonotonicClock.MsBetween(plan.T0Decision, t5));

            Position = pos;
            double slipUsd = 0;
            double slipTicks = 0;
            if (plan.Type == TradeType.Buy) { slipTicks = (pos.EntryPrice - plan.DecisionPrice) / Spec.TickSize; }
            else { slipTicks = (plan.DecisionPrice - pos.EntryPrice) / Spec.TickSize; }
            slipUsd = slipTicks * Spec.TickSize * Spec.MoneyPerPriceUnit;

            Working = new TradeRecord
            {
                Label = plan.Label,
                Direction = plan.Type == TradeType.Buy ? "LONG" : "SHORT",
                SetupType = plan.SetupType,
                Session = plan.Session,
                Regime = plan.Regime,
                SignalTimeUtc = plan.SignalTimeUtc,
                EntryTimeUtc = pos.EntryTime.ToUniversalTime(),
                EntryPrice = pos.EntryPrice,
                StopPrice = plan.StopPrice,
                TargetPrice = plan.TargetPrice,
                VolumeUnits = pos.VolumeInUnits,
                RiskUsd = plan.PlannedRiskUsd,
                SpreadEntryTicks = plan.SpreadTicks,
                SlippageUsd = slipUsd,
                Score = plan.Score,
                ExternalStatus = plan.ExternalStatus,
                Filters = plan.Filters,
                SyntheticTarget = plan.SyntheticTarget,
                LatencyDecisionMs = plan.DecisionLatencyMs,
                LatencyAckMs = MonotonicClock.MsBetween(t3Submit, t4),
                LatencyFillMs = MonotonicClock.MsBetween(t3Submit, t5),
                MaeUsd = 0, MfeUsd = 0
            };

            EnsureProtection(pos, plan);

            double adverseTicks = Math.Abs(slipTicks);
            if (adverseTicks > MaxSlippageTicks && MaxSlippageTicks > 0)
            {
                _log("fill slippage " + Numeric.F(slipTicks, 1) + " ticks exceeds limit " + Numeric.F(MaxSlippageTicks, 1) +
                     " -> closing immediately (risk control, not signal)", true);
                ClosePosition("slippage_exceeded", "slippage " + Numeric.F(slipTicks, 1) + "t");
            }
        }

        void EnsureProtection(Position pos, EntryPlan plan)
        {
            if (!UseAbsoluteProtection || pos == null) return;
            try
            {
                bool needs = !pos.StopLoss.HasValue || Math.Abs(pos.StopLoss.Value - plan.StopPrice) > Spec.TickSize * 0.51;
                if (needs)
                {
                    var r = _bot.ModifyPosition(pos, plan.StopPrice, plan.TargetPrice, ProtectionType.Absolute);
                    if (r == null || !r.IsSuccessful)
                        _log("WARNING: could not snap protection to absolute prices (" + (r?.Error?.ToString() ?? "null") + "); broker SL/TP in pips remain", true);
                }
            }
            catch (Exception ex)
            {
                _log("protection modify exception: " + ex.Message, true);
            }
        }

        static RejectReason MapError(ErrorCode? e)
        {
            if (!e.HasValue) return RejectReason.OrderRejected;
            switch (e.Value)
            {
                case ErrorCode.BadVolume: return RejectReason.VolumeNotAccepted;
                case ErrorCode.MarketClosed: return RejectReason.MarketClosed;
                case ErrorCode.Disconnected: return RejectReason.Disconnected;
                case ErrorCode.Timeout: return RejectReason.OrderRejected;
                case ErrorCode.InvalidStopLossTakeProfit: return RejectReason.RiskCapExceeded;
                case ErrorCode.NoMoney: return RejectReason.RiskCapExceeded;
                default: return RejectReason.OrderRejected;
            }
        }

        void Fail(EntryPlan plan, string why, RejectReason reason)
        {
            RejectCount++;
            LastError = why;
            _log("entry blocked: " + why, false);
            RaiseFailed(plan, why, reason);
        }

        void RaiseFailed(EntryPlan plan, string why, RejectReason reason)
        {
            try { EntryFailed?.Invoke(why, reason); } catch { }
        }

        /// <summary>Closes the current position. Never throws into the trading loop.</summary>
        public bool ClosePosition(string reason, string detail = "")
        {
            var pos = Position;
            if (pos == null) return false;
            try
            {
                var r = _bot.ClosePosition(pos);
                if (r == null || !r.IsSuccessful)
                {
                    _log("close failed (" + reason + "): " + (r?.Error?.ToString() ?? "null"), true);
                    return false;
                }
                if (Working != null && Working.ExitReason.Length == 0) Working.ExitReason = reason + (detail.Length > 0 ? ":" + detail : "");
                return true;
            }
            catch (Exception ex)
            {
                _log("close exception (" + reason + "): " + ex.Message, true);
                return false;
            }
        }

        /// <summary>Updates MFE/MAE and returns the current unrealized PnL.</summary>
        public double TrackExcursion()
        {
            var pos = Position;
            if (pos == null || Working == null) return 0;
            double pnl = pos.NetProfit;
            if (pnl < Working.MaeUsd) Working.MaeUsd = pnl;
            if (pnl > Working.MfeUsd) Working.MfeUsd = pnl;
            return pnl;
        }

        public void OnPositionClosed(Position pos, PositionCloseReason? closeReason, HistoricalTrade hist)
        {
            if (Working == null || pos == null) return;
            if (pos.Id != Position?.Id && Position != null) return;

            var t = Working;
            double exitPrice = hist?.ClosingPrice ?? pos.CurrentPrice;
            t.ExitPrice = exitPrice;
            t.ExitTimeUtc = (hist?.ClosingTime ?? _bot.Server.TimeInUtc).ToUniversalTime();
            t.GrossPnl = hist?.GrossProfit ?? pos.GrossProfit;
            t.CommissionUsd = hist?.Commissions ?? pos.Commissions;
            t.SwapUsd = hist?.Swap ?? pos.Swap;
            t.NetPnl = hist?.NetProfit ?? pos.NetProfit;
            if (t.ExitReason.Length == 0) t.ExitReason = closeReason?.ToString() ?? "unknown";
            double risk = Math.Abs(t.EntryPrice - t.StopPrice) * Spec.MoneyPerPriceUnit;
            t.RMultiple = risk > 0 ? t.NetPnl / risk : double.NaN;
            t.MaeTicks = Spec.TickSize > 0 ? t.MaeUsd / (Spec.TickSize * Spec.MoneyPerPriceUnit) : double.NaN;
            t.MfeTicks = Spec.TickSize > 0 ? t.MfeUsd / (Spec.TickSize * Spec.MoneyPerPriceUnit) : double.NaN;
            t.BarsHeld = Math.Max(0, (int)((t.ExitTimeUtc - t.EntryTimeUtc).TotalMinutes / 5));
            t.Id = pos.Id;

            Position = null;
            Working = null;
            try { TradeClosed?.Invoke(t); } catch { }
            _log("closed " + t.Direction + " #" + pos.Id + " net=" + Numeric.F(t.NetPnl, 2) + " R=" + Numeric.F(t.RMultiple, 2) +
                 " reason=" + t.ExitReason + " mae=" + Numeric.F(t.MaeUsd, 2) + " mfe=" + Numeric.F(t.MfeUsd, 2), false);
        }

        public void CheckTimeout(DateTime nowUtc)
        {
            if (!InFlight) return;
            if ((nowUtc - InFlightSinceUtc).TotalMilliseconds > OrderTimeoutMs)
            {
                TimeoutCount++;
                InFlight = false;
                _log("order in-flight timeout after " + OrderTimeoutMs + "ms; reconciling by label", true);
            }
        }
    }
}
