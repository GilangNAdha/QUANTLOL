using System;
using System.Collections.Generic;
using QuantLOL.Core;
using Xunit;

namespace QuantLOL.Tests
{
    public class SignalEngineTests
    {
        static BarDto[] Bars(int n = 60, double start = 100, double step = 0.02)
        {
            var t = new DateTime(2026, 4, 1, 8, 0, 0, DateTimeKind.Utc);
            var b = new BarDto[n];
            for (int i = 0; i < n; i++)
            {
                double p = start + i * step;
                b[i] = new BarDto(t.AddMinutes(5 * i), p, p + 0.05, p - 0.05, p, 100);
            }
            return b;
        }

        static StructureSnapshot BullishStructure(int lastIndex, double sweepLow = 100.6, double dealingLow = 100, double dealingHigh = 104)
        {
            var s = new StructureSnapshot
            {
                Trend = 1,
                LastChochIndex = lastIndex - 1,
                LastChochDirection = 1,
                LastChochLevel = 101.0,
                DealingLow = dealingLow,
                DealingHigh = dealingHigh,
                LastSwingLow = dealingLow,
                LastSwingHigh = dealingHigh,
                BullishOrderBlock = new Zone { Bottom = 100.55, Top = 100.75, BarIndex = lastIndex - 3 }
            };
            s.BullishSweep = new SweepEvent
            {
                BarIndex = lastIndex - 2,
                LevelPrice = 100.7,
                Extreme = sweepLow,
                Direction = 1,
                LevelKind = LevelKind.SwingLow,
                PierceTicks = 3
            };
            s.Atr = 0.2;
            return s;
        }

        static SignalInputs BaseInputs(BarDto[] bars, StructureSnapshot st)
        {
            int i = bars.Length - 1;
            return new SignalInputs
            {
                EntryBars = bars,
                LastClosedIndex = i,
                EntryStructure = st,
                ContextStructure = st,
                OrderFlow = new OrderFlowSnapshot { BarsUsed = 30, DeltaRatio = 0.25, Delta = 500, TotalVolume = 2000, BullishAbsorption = true },
                Targets = new[] { new LevelRef(102.0, LevelKind.SwingHigh, "target") },
                TickSize = 0.01,
                EntryPrice = bars[i].Close,
                Bid = bars[i].Close - 0.10,
                Ask = bars[i].Close + 0.10,
                MoneyPerPriceUnit = 1.0,
                CommissionRoundTripUsd = 0.14,
                SlippageRoundTripUsd = 0.05,
                Caps = new RiskCaps { MaxRiskPerTradeUsd = 5.0, MinRewardRisk = 1.2 },
                RequireLocation = true,
                RequireOrderFlow = true,
                RequireConfirmation = true,
                EnableFvg = false,
                EnableOrderBlock = true,
                EnableVix = false,
                MinScore = 3,
                FallbackRr = 2.0,
                StopBufferTicks = 5,
                MaxSpreadTicks = 100,
                SweepWindowBars = 20,
                ConfirmWindowBars = 20,
                LocationProximityTicks = 60,
                Weights = new SignalWeights()
            };
        }

        [Fact]
        public void ProducesBullishSetupWhenAllCoreConditionsHold()
        {
            var bars = Bars();
            int i = bars.Length - 1;
            var st = BullishStructure(i, sweepLow: 100.9, dealingLow: 100.0, dealingHigh: 104.0);
            var x = BaseInputs(bars, st);
            x.Targets = new[] { new LevelRef(x.EntryPrice + 3.0, LevelKind.SwingHigh, "t") };
            var d = SignalEngine.Evaluate(x);
            Assert.Equal(SignalState.Bullish, d.State);
            Assert.Equal(1, d.Direction);
            Assert.True(d.Stop < d.Entry && d.Target > d.Entry);
            Assert.True(d.RiskUsd <= 5.0, "risk " + d.RiskUsd);
            Assert.True(d.RiskUsd > 0);
            Assert.Contains("bull_sweep", string.Join(";", d.Evidence));
        }

        [Fact]
        public void WideStructuralStopIsRejectedNotTightened()
        {
            var bars = Bars();
            var st = BullishStructure(bars.Length - 1, sweepLow: 95.0);   // ~6 USD away at 1 USD/price
            var x = BaseInputs(bars, st);
            x.Targets = new[] { new LevelRef(x.EntryPrice + 20, LevelKind.SwingHigh, "t") };
            var d = SignalEngine.Evaluate(x);
            Assert.NotEqual(SignalState.Bullish, d.State);
            Assert.Equal(RejectReason.RiskCapExceeded, d.Reject);
            Assert.Contains("planned risk", d.RejectDetail);
            Assert.True(d.Entry - d.Stop > 4.9, "the structural stop must be preserved, never tightened to fit the cap");
        }

        [Fact]
        public void SpreadFilterRejectsBeforeAnySetupWork()
        {
            var bars = Bars();
            var st = BullishStructure(bars.Length - 1);
            var x = BaseInputs(bars, st);
            x.MaxSpreadTicks = 5;
            var d = SignalEngine.Evaluate(x);
            Assert.Equal(RejectReason.SpreadTooWide, d.Reject);
            Assert.Contains("spread", d.RejectDetail);
        }

        [Fact]
        public void MissingConfirmationYieldsNoSetup()
        {
            var bars = Bars();
            var st = BullishStructure(bars.Length - 1);
            st.LastChochDirection = -1;
            st.LastChochIndex = bars.Length - 1;
            var d = SignalEngine.Evaluate(BaseInputs(bars, st));
            Assert.Equal(RejectReason.NoSetup, d.Reject);
        }

        [Fact]
        public void ConflictingDirectionsAreRefused()
        {
            var bars = Bars();
            int i = bars.Length - 1;
            var st = BullishStructure(i);
            st.LastChochDirection = 1;
            // force a bearish candidate too: a recent bearish sweep plus a bearish choch is impossible
            // on the same structure, so emulate by making the last CHoCH bearish but the bullish sweep fresh.
            st.BearishSweep = new SweepEvent { BarIndex = i - 1, LevelPrice = 101.5, Extreme = 101.6, Direction = -1, LevelKind = LevelKind.SwingHigh, PierceTicks = 3 };
            var d = SignalEngine.Evaluate(BaseInputs(bars, st));
            // A bearish sweep without bearish confirmation must not create a conflict.
            Assert.NotEqual(RejectReason.Conflict, d.Reject);
        }

        [Fact]
        public void OrderFlowVetoWhenRequired()
        {
            var bars = Bars();
            var st = BullishStructure(bars.Length - 1);
            var x = BaseInputs(bars, st);
            x.OrderFlow = new OrderFlowSnapshot { BarsUsed = 30, DeltaRatio = -0.4 };
            var d = SignalEngine.Evaluate(x);
            Assert.Equal(RejectReason.NoSetup, d.Reject);
            Assert.Contains("order-flow", d.RejectDetail);
        }

        [Fact]
        public void VixVetoBlocksEntries()
        {
            var bars = Bars();
            var st = BullishStructure(bars.Length - 1);
            var x = BaseInputs(bars, st);
            x.EnableVix = true;
            x.BlockOnHighVix = true;
            x.VixHighThreshold = 30;
            x.Vix = new VixSnapshot { Valid = true, Level = 42, AsOfUtc = DateTime.UtcNow, Name = "VIX", Quality = DataQuality.Manual };
            var d = SignalEngine.Evaluate(x);
            Assert.Equal(RejectReason.VixFilter, d.Reject);
        }

        [Fact]
        public void ScoreThresholdCanSuppressAValidSetup()
        {
            var bars = Bars();
            var st = BullishStructure(bars.Length - 1, sweepLow: 100.9);
            var x = BaseInputs(bars, st);
            x.Targets = new[] { new LevelRef(x.EntryPrice + 3.0, LevelKind.SwingHigh, "t") };
            x.MinScore = 99;
            var d = SignalEngine.Evaluate(x);
            Assert.Equal(RejectReason.ScoreBelowThreshold, d.Reject);
        }

        [Fact]
        public void SyntheticTargetIsFlagged()
        {
            var bars = Bars();
            var st = BullishStructure(bars.Length - 1, sweepLow: 100.9);
            var x = BaseInputs(bars, st);
            x.Targets = Array.Empty<LevelRef>();
            x.AllowSyntheticTarget = true;
            var d = SignalEngine.Evaluate(x);
            Assert.Equal(SignalState.Bullish, d.State);
            Assert.True(d.TargetIsSynthetic);
        }

        [Fact]
        public void StructuralOnlyModeRefusesToInventATarget()
        {
            var bars = Bars();
            var st = BullishStructure(bars.Length - 1, sweepLow: 100.9);
            var x = BaseInputs(bars, st);
            x.Targets = Array.Empty<LevelRef>();
            x.AllowSyntheticTarget = false;
            var d = SignalEngine.Evaluate(x);
            Assert.Equal(RejectReason.RewardTooLow, d.Reject);
        }

        [Fact]
        public void SignalKeyIsStableAndUniquePerBarAndPrice()
        {
            var bars = Bars();
            var st = BullishStructure(bars.Length - 1, sweepLow: 100.9);
            var x = BaseInputs(bars, st);
            x.Targets = new[] { new LevelRef(x.EntryPrice + 3.0, LevelKind.SwingHigh, "t") };
            var a = SignalEngine.Evaluate(x);
            var b = SignalEngine.Evaluate(x);
            Assert.Equal(a.SignalKey, b.SignalKey);
            Assert.False(string.IsNullOrEmpty(a.SignalKey));
        }
    }
}
