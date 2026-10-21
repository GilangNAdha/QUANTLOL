using System;
using System.Collections.Generic;
using System.Text;

namespace QuantLOL.Core
{
    public enum SignalState { Unavailable = 0, Neutral = 1, Bullish = 2, Bearish = 3 }

    /// <summary>
    /// Optional confluence weights. These defaults are a transparent, unoptimised prior:
    /// they are NOT claimed to be statistically validated. They are exposed as parameters
    /// so the research pipeline can test whether adding a module improves out-of-sample
    /// expectancy. Core requirements (sweep + confirmation) are unweighted gates.
    /// </summary>
    public sealed class SignalWeights
    {
        public double Sweep = 2.0;
        public double Confirmation = 2.0;
        public double Location = 1.0;
        public double OrderFlow = 1.0;
        public double Fvg = 0.5;
        public double OrderBlock = 0.5;
        public double GexConfluence = 1.0;
        public double VixContext = 0.5;
        public double AmtAcceptance = 0.5;
    }

    public sealed class SignalInputs
    {
        public BarDto[] EntryBars;
        public int LastClosedIndex;
        public StructureSnapshot EntryStructure;
        public StructureSnapshot ContextStructure;
        public ProfileSnapshot SessionProfile;
        public ProfileSnapshot PrevSessionProfile;
        public OrderFlowSnapshot OrderFlow;
        public GexSnapshot Gex;
        public VixSnapshot Vix;
        public LevelRef[] Targets = Array.Empty<LevelRef>();

        public double TickSize = 0.01;
        public double EntryPrice;                 // last closed price (decision price)
        public double Ask, Bid;
        public double MoneyPerPriceUnit = 1.0;
        public double CommissionRoundTripUsd;
        public double SlippageRoundTripUsd;
        public RiskCaps Caps = new RiskCaps();

        public bool RequireLocation = true;
        public bool RequireOrderFlow = true;
        public bool RequireConfirmation = true;
        public bool EnableFvg = true;
        public bool EnableOrderBlock = true;
        public bool EnableGex = false;
        public bool EnableVix = true;
        public bool AllowSyntheticTarget = true;
        public bool BlockOnHighVix = true;
        public double VixHighThreshold = 30;
        public double VixLowThreshold = 12;
        public int SweepWindowBars = 12;
        public int ConfirmWindowBars = 12;
        public double LocationProximityTicks = 60;
        public double MinScore = 4.0;
        public double FallbackRr = 2.0;
        public double StopBufferTicks = 5;
        public int MaxSpreadTicks = 40;
        public bool SpreadKnown = true;
        public SignalWeights Weights = new SignalWeights();
        public bool OrderFlowEnabled = true;
        public string EntryTimeFrameLabel = "M5";
    }

    public sealed class SignalDecision
    {
        public SignalState State = SignalState.Neutral;
        public RejectReason Reject = RejectReason.NoSetup;
        public string RejectDetail = "";
        public int Direction;                 // +1 long, -1 short, 0 none
        public double Entry, Stop, Target;
        public int Score;
        public double RiskUsd, RrGross, RrNet;
        public string SetupType = "";
        public readonly List<string> Evidence = new List<string>(12);
        public bool TargetIsSynthetic;
        public string SignalKey = "";

        public string Summary()
        {
            var sb = new StringBuilder();
            sb.Append(State).Append(" dir=").Append(Direction > 0 ? "LONG" : Direction < 0 ? "SHORT" : "NONE");
            sb.Append(" score=").Append(Numeric.F(Score, 1));
            if (State == SignalState.Bullish || State == SignalState.Bearish)
            {
                sb.Append(" entry=").Append(Numeric.F(Entry, 3));
                sb.Append(" stop=").Append(Numeric.F(Stop, 3));
                sb.Append(" target=").Append(Numeric.F(Target, 3)).Append(TargetIsSynthetic ? "(synthetic)" : "");
                sb.Append(" risk=").Append(Numeric.F(RiskUsd, 2));
                sb.Append(" rrNet=").Append(Numeric.F(RrNet, 2));
            }
            else sb.Append(" reject=").Append(Reject).Append(" (").Append(RejectDetail).Append(")");
            if (Evidence.Count > 0) sb.Append(" [").Append(string.Join("; ", Evidence)).Append(']');
            return sb.ToString();
        }
    }

    /// <summary>
    /// Deterministic confluence engine. Priority order is fixed and explicit:
    ///   1. AMT/context location and GEX levels decide *where* a trade is allowed.
    ///   2. SMC (sweep + structure shift) decides *that* there is a setup.
    ///   3. Order flow confirms the entry.
    ///   4. Delta/Vanna and VIX are context only and can veto, never force, a trade.
    ///   5. Risk maths has the final word in RiskMath.Approve.
    /// </summary>
    public static class SignalEngine
    {
        public static SignalDecision Evaluate(SignalInputs x)
        {
            var d = new SignalDecision();
            if (x == null) { d.State = SignalState.Unavailable; d.Reject = RejectReason.Exception; d.RejectDetail = "null inputs"; return d; }
            var bars = x.EntryBars;
            int i = x.LastClosedIndex;
            if (bars == null || bars.Length == 0 || i < 5 || i >= bars.Length)
            {
                d.State = SignalState.Unavailable; d.Reject = RejectReason.DataStale; d.RejectDetail = "insufficient closed bars";
                return d;
            }
            if (!x.SpreadKnown) { d.Reject = RejectReason.QuoteStale; d.RejectDetail = "bid/ask unavailable"; return d; }
            double spreadTicks = (x.Ask - x.Bid) / Math.Max(1e-12, x.TickSize);
            if (spreadTicks > x.MaxSpreadTicks)
            {
                d.Reject = RejectReason.SpreadTooWide;
                d.RejectDetail = "spread " + Numeric.F(spreadTicks, 1) + " ticks > " + x.MaxSpreadTicks;
                return d;
            }

            var st = x.EntryStructure;
            if (st == null) { d.State = SignalState.Unavailable; d.Reject = RejectReason.DataStale; d.RejectDetail = "no structure"; return d; }

            // ---------- VIX veto (context only) ----------
            if (x.EnableVix && x.Vix != null && x.Vix.Valid)
            {
                if (x.BlockOnHighVix && x.Vix.Level >= x.VixHighThreshold)
                {
                    d.Reject = RejectReason.VixFilter;
                    d.RejectDetail = "VIX " + Numeric.F(x.Vix.Level, 1) + " >= " + Numeric.F(x.VixHighThreshold, 1);
                    d.Evidence.Add("vix_block");
                    return d;
                }
            }
            else if (x.EnableVix && x.Vix != null && !x.Vix.Valid)
            {
                d.Evidence.Add("vix_invalid");
            }

            var longCand = BuildCandidate(x, +1);
            var shortCand = BuildCandidate(x, -1);

            bool longOk = longCand.CoreOk;
            bool shortOk = shortCand.CoreOk;
            if (longOk && shortOk)
            {
                // Material conflict: two opposing setups on the same closed bar. Stand aside.
                d.Reject = RejectReason.Conflict;
                d.RejectDetail = "bullish and bearish setups both valid";
                d.Evidence.Add("long:" + longCand.Describe());
                d.Evidence.Add("short:" + shortCand.Describe());
                return d;
            }
            if (!longOk && !shortOk)
            {
                d.Reject = RejectReason.NoSetup;
                var relevant = longCand.HasSweep ? longCand : (shortCand.HasSweep ? shortCand : null);
                d.RejectDetail = relevant?.BestRejectReason(x) ?? "no sweep+confirmation";
                if (relevant != null) d.Evidence.AddRange(relevant.Evidence);
                return d;
            }

            var cand = longOk ? longCand : shortCand;
            d.Direction = cand.Direction;
            d.Score = (int)Math.Round(cand.Score);
            d.Evidence.AddRange(cand.Evidence);
            d.SetupType = cand.SetupType;

            if (cand.Score < x.MinScore)
            {
                d.Reject = RejectReason.ScoreBelowThreshold;
                d.RejectDetail = "score " + Numeric.F(cand.Score, 1) + " < " + Numeric.F(x.MinScore, 1);
                return d;
            }

            // ---------- Entry / stop / target ----------
            double entry = x.EntryPrice;
            double buffer = Math.Max(0, x.StopBufferTicks) * x.TickSize;
            double stop;
            if (cand.Direction > 0)
            {
                double structuralLow = cand.SweepExtreme;
                if (x.EnableOrderBlock && st.BullishOrderBlock.IsValid && st.BullishOrderBlock.Bottom < structuralLow)
                    structuralLow = Math.Min(structuralLow, st.BullishOrderBlock.Bottom);
                stop = structuralLow - buffer;
            }
            else
            {
                double structuralHigh = cand.SweepExtreme;
                if (x.EnableOrderBlock && st.BearishOrderBlock.IsValid && st.BearishOrderBlock.Top > structuralHigh)
                    structuralHigh = Math.Max(structuralHigh, st.BearishOrderBlock.Top);
                stop = structuralHigh + buffer;
            }
            stop = Numeric.RoundToTick(stop, x.TickSize);

            double riskDist = Math.Abs(entry - stop);
            if (riskDist <= 0)
            {
                d.Reject = RejectReason.NoSetup; d.RejectDetail = "degenerate stop";
                return d;
            }

            double target = SelectTarget(x, cand.Direction, entry, riskDist, out bool synthetic);
            d.TargetIsSynthetic = synthetic;
            if (double.IsNaN(target))
            {
                d.Reject = RejectReason.RewardTooLow;
                d.RejectDetail = "no structural target with adequate reward";
                return d;
            }
            target = Numeric.RoundToTick(target, x.TickSize);

            var nums = RiskMath.Compute(entry, stop, target, x.MoneyPerPriceUnit, x.CommissionRoundTripUsd, x.SlippageRoundTripUsd);
            d.Entry = entry; d.Stop = stop; d.Target = target;
            d.RiskUsd = nums.RiskUsd; d.RrGross = nums.RrGross; d.RrNet = nums.RrNet;

            if (!RiskMath.Approve(nums, x.Caps, out string why))
            {
                d.Reject = why != null && why.Contains("R:R") ? RejectReason.RewardTooLow : RejectReason.RiskCapExceeded;
                d.RejectDetail = why;
                return d;
            }

            d.State = cand.Direction > 0 ? SignalState.Bullish : SignalState.Bearish;
            d.Reject = RejectReason.None;
            d.RejectDetail = "";
            d.SignalKey = BuildSignalKey(x, cand.Direction, entry, i);
            return d;
        }

        public static string BuildSignalKey(SignalInputs x, int direction, double entry, int barIndex)
        {
            var t = x.EntryBars[barIndex].TimeUtc;
            return (direction > 0 ? "L" : "S") + "-" + t.ToString("yyyyMMddHHmm") + "-" + Numeric.F(entry, 3) + "-" + x.EntryTimeFrameLabel;
        }

        sealed class Candidate
        {
            public int Direction;
            public bool CoreOk;
            public bool HasSweep;
            public double Score;
            public double SweepExtreme;
            public string SetupType = "";
            public readonly List<string> Evidence = new List<string>(8);
            public string BestReject;
            public string Describe() => (Direction > 0 ? "L" : "S") + (CoreOk ? "+" : "-") + " score=" + Numeric.F(Score, 1) + " " + (BestReject ?? "");
            public string BestRejectReason(SignalInputs x) => BestReject;
        }

        static Candidate BuildCandidate(SignalInputs x, int dir)
        {
            var c = new Candidate { Direction = dir };
            var st = x.EntryStructure;
            int i = x.LastClosedIndex;
            var sweep = st.ActiveSweep(dir, i, x.SweepWindowBars);
            if (sweep == null)
            {
                c.BestReject = (dir > 0 ? "no bullish" : "no bearish") + " sweep within " + x.SweepWindowBars + " bars";
                return c;
            }
            bool confirmation = st.LastChochDirection == dir && st.LastChochIndex >= Math.Max(0, sweep.BarIndex) - 1
                                && (i - st.LastChochIndex) <= x.ConfirmWindowBars;
            if (x.RequireConfirmation && !confirmation)
            {
                c.BestReject = "no confirmed " + (dir > 0 ? "bullish" : "bearish") + " shift after sweep";
                return c;
            }
            c.CoreOk = true;
            c.HasSweep = true;
            c.SweepExtreme = sweep.Extreme;
            c.Score += x.Weights.Sweep + x.Weights.Confirmation;
            c.Evidence.Add((dir > 0 ? "bull_sweep:" : "bear_sweep:") + sweep.LevelKind + "@" + Numeric.F(sweep.LevelPrice, 2) + "/pierce" + Numeric.F(sweep.PierceTicks, 0) + "t");
            c.Evidence.Add((dir > 0 ? "choch_bull" : "choch_bear") + "@" + Numeric.F(st.LastChochLevel, 2));
            c.SetupType = sweep.LevelKind + "_sweep+" + (dir > 0 ? "bull" : "bear") + "_choch";

            // Location: discount/premium of the dealing range, or proximity to a swept level.
            double mid = (st.DealingHigh + st.DealingLow) / 2.0;
            bool inDiscount = st.DealingHigh > st.DealingLow && x.EntryPrice <= mid;
            bool inPremium = st.DealingHigh > st.DealingLow && x.EntryPrice >= mid;
            bool locOk = dir > 0 ? inDiscount : inPremium;
            double prox = Math.Abs(x.EntryPrice - sweep.LevelPrice) / Math.Max(1e-12, x.TickSize);
            bool nearSwept = prox <= x.LocationProximityTicks;
            if (locOk || nearSwept)
            {
                c.Score += x.Weights.Location;
                c.Evidence.Add(dir > 0 ? (inDiscount ? "discount" : "near_level") : (inPremium ? "premium" : "near_level"));
            }
            else if (x.RequireLocation)
            {
                c.CoreOk = false;
                c.BestReject = "price not in " + (dir > 0 ? "discount" : "premium") + " and far from swept level";
                return c;
            }

            // AMT context: value-area location and acceptance.
            if (x.SessionProfile != null && x.SessionProfile.HasData)
            {
                if (dir > 0 && x.EntryPrice <= x.SessionProfile.Val) { c.Score += x.Weights.AmtAcceptance; c.Evidence.Add("below_val"); }
                else if (dir < 0 && x.EntryPrice >= x.SessionProfile.Vah) { c.Score += x.Weights.AmtAcceptance; c.Evidence.Add("above_vah"); }
                else if (x.SessionProfile.PriceInValueArea(x.EntryPrice)) c.Evidence.Add("inside_value");
            }

            // Order flow confirmation.
            if (x.OrderFlowEnabled && x.OrderFlow != null && x.OrderFlow.BarsUsed > 1)
            {
                bool ofOk = dir > 0
                    ? (x.OrderFlow.DeltaRatio > 0 || x.OrderFlow.BullishAbsorption || x.OrderFlow.BullishDivergence)
                    : (x.OrderFlow.DeltaRatio < 0 || x.OrderFlow.BearishAbsorption || x.OrderFlow.BearishDivergence);
                if (ofOk)
                {
                    c.Score += x.Weights.OrderFlow;
                    c.Evidence.Add("of:" + (dir > 0 ? "buy" : "sell") + Numeric.F(x.OrderFlow.DeltaRatio, 2));
                }
                else if (x.RequireOrderFlow)
                {
                    c.CoreOk = false;
                    c.BestReject = "order-flow proxy does not confirm (" + Numeric.F(x.OrderFlow.DeltaRatio, 2) + ")";
                    return c;
                }
            }

            // FVG confluence.
            if (x.EnableFvg)
            {
                var fvg = dir > 0 ? st.LastBullishFvg : st.LastBearishFvg;
                if (fvg != null && !fvg.Filled && (i - fvg.BarIndex) <= 40)
                {
                    c.Score += x.Weights.Fvg;
                    c.Evidence.Add((dir > 0 ? "bull_fvg:" : "bear_fvg:") + Numeric.F(fvg.Bottom, 2) + "-" + Numeric.F(fvg.Top, 2));
                }
            }
            // Order block confluence.
            if (x.EnableOrderBlock)
            {
                var ob = dir > 0 ? st.BullishOrderBlock : st.BearishOrderBlock;
                if (ob != null && ob.IsValid)
                {
                    c.Score += x.Weights.OrderBlock;
                    c.Evidence.Add("ob:" + Numeric.F(ob.Bottom, 2) + "-" + Numeric.F(ob.Top, 2));
                }
            }
            // GEX confluence: a wall or the gamma flip just beyond the entry in trade direction.
            if (x.EnableGex && x.Gex != null && x.Gex.Valid)
            {
                double near = double.NaN;
                foreach (var lv in x.Gex.Levels)
                {
                    if (lv.Kind != LevelKind.CallWall && lv.Kind != LevelKind.PutWall && lv.Kind != LevelKind.GammaFlip) continue;
                    bool correctSide = dir > 0 ? lv.Price < x.EntryPrice : lv.Price > x.EntryPrice;
                    if (!correctSide) continue;
                    if (Math.Abs(lv.Price - x.EntryPrice) / Math.Max(1e-12, x.TickSize) > x.LocationProximityTicks * 5) continue;
                    near = lv.Price; break;
                }
                if (Numeric.IsFinite(near))
                {
                    c.Score += x.Weights.GexConfluence;
                    c.Evidence.Add("gex_level:" + Numeric.F(near, 2));
                }
            }
            // VIX context (never forces a direction).
            if (x.EnableVix && x.Vix != null && x.Vix.Valid && x.Vix.Level < x.VixHighThreshold)
            {
                c.Score += x.Weights.VixContext;
                c.Evidence.Add("vix_ok:" + Numeric.F(x.Vix.Level, 1));
            }
            return c;
        }

        static double SelectTarget(SignalInputs x, int dir, double entry, double riskDist, out bool synthetic)
        {
            synthetic = false;
            double best = double.NaN;
            double bestDist = double.MaxValue;
            double minDist = riskDist * x.Caps.MinRewardRisk;
            if (x.Targets != null)
            {
                foreach (var t in x.Targets)
                {
                    if (t.Price <= 0) continue;
                    bool ahead = dir > 0 ? t.Price > entry : t.Price < entry;
                    if (!ahead) continue;
                    double dist = Math.Abs(t.Price - entry);
                    if (dist < minDist) continue;
                    if (dist < bestDist) { bestDist = dist; best = t.Price; }
                }
            }
            if (!double.IsNaN(best)) return best;
            if (!x.AllowSyntheticTarget) return double.NaN;
            synthetic = true;
            return dir > 0 ? entry + riskDist * x.FallbackRr : entry - riskDist * x.FallbackRr;
        }
    }
}
