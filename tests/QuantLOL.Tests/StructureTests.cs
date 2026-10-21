using System;
using System.Linq;
using QuantLOL.Core;
using Xunit;

namespace QuantLOL.Tests
{
    public class StructureTests
    {
        static StructureOptions Opt() => new StructureOptions
        {
            SwingLookback = 2, ScanBars = 400, TickSize = 0.01, MinSweepPierceTicks = 2,
            MinFvgTicks = 10, ObLookbackBars = 20, SweepWindowBars = 20, DealingRangeSwings = 4, FvgScanBars = 60
        };

        [Fact]
        public void SwingsAreOnlyKnowableAfterTheirConfirmationBar()
        {
            var bars = BuildTrendingSeries();
            var s = StructureAnalyzer.Analyze(bars, bars.Length - 1, Opt());
            Assert.NotEmpty(s.Swings);
            foreach (var sw in s.Swings)
            {
                Assert.Equal(sw.Index + Opt().SwingLookback, sw.ConfirmIndex);
                Assert.True(sw.ConfirmIndex <= bars.Length - 1, "confirmation cannot be in the future");
            }
        }

        [Fact]
        public void AnalysisIsDeterministic()
        {
            var bars = BuildTrendingSeries();
            var a = StructureAnalyzer.Analyze(bars, bars.Length - 1, Opt());
            var b = StructureAnalyzer.Analyze(bars, bars.Length - 1, Opt());
            Assert.Equal(a.Trend, b.Trend);
            Assert.Equal(a.LastBosIndex, b.LastBosIndex);
            Assert.Equal(a.LastChochIndex, b.LastChochIndex);
            Assert.Equal(a.DealingHigh, b.DealingHigh, 9);
            Assert.Equal(a.DealingLow, b.DealingLow, 9);
        }

        [Fact]
        public void BullishBreakOfStructureChangesTrendAndRecordsChoch()
        {
            // flat base with a confirmed swing high at 100.6, then a close decisively above it
            var bars = BuildBosSeries();
            var s = StructureAnalyzer.Analyze(bars, bars.Length - 1, Opt());
            Assert.Equal(1, s.Trend);
            Assert.True(s.LastBosIndex > 0);
            Assert.Equal(1, s.LastBosDirection);
            Assert.True(s.LastChochIndex > 0, "first break from an undecided state is a CHoCH");
            Assert.Equal(1, s.LastChochDirection);
            Assert.True(s.LastBosIndex > Opt().SwingLookback, "BOS cannot happen before swings are confirmable");
            Assert.True(s.BullishOrderBlock.IsValid, "an order block should be recorded on a bullish BOS");
        }

        [Fact]
        public void NoFutureDataLeaksIntoTheAnalysis()
        {
            // State at bar N must be identical whether or not bars after N exist.
            var bars = BuildBosSeries();
            int n = bars.Length - 2;
            var withFuture = StructureAnalyzer.Analyze(bars, n, Opt());
            var cutAtN = StructureAnalyzer.Analyze(bars.Take(n + 1).ToArray(), n, Opt());
            Assert.Equal(withFuture.Trend, cutAtN.Trend);
            Assert.Equal(withFuture.LastBosIndex, cutAtN.LastBosIndex);
            Assert.Equal(withFuture.LastChochIndex, cutAtN.LastChochIndex);
            Assert.Equal(withFuture.DealingHigh, cutAtN.DealingHigh, 9);
            Assert.Equal(withFuture.DealingLow, cutAtN.DealingLow, 9);
            Assert.Equal(withFuture.Swings.Count, cutAtN.Swings.Count);
        }

        [Fact]
        public void SweepRequiresPierceAndReclaim()
        {
            var bars = BuildSweepSeries();
            var s = StructureAnalyzer.Analyze(bars, bars.Length - 1, Opt());
            Assert.NotNull(s.BullishSweep);
            Assert.True(s.BullishSweep.PierceTicks >= Opt().MinSweepPierceTicks - 1e-9, "must pierce by at least the minimum");
            Assert.True(bars[s.BullishSweep.BarIndex].Close > s.BullishSweep.LevelPrice, "must close back above the swept level");
            Assert.True(s.BullishSweep.Direction > 0);
        }

        [Fact]
        public void ContextLevelsAreAlsoSweepCandidates()
        {
            var bars = BuildSweepSeries();
            var levels = new[] { new LevelRef(99.89, LevelKind.PrevSessionLow, "pdl") };
            var s = StructureAnalyzer.Analyze(bars, bars.Length - 1, Opt(), levels);
            Assert.NotNull(s.BullishSweep);
            Assert.Equal(LevelKind.PrevSessionLow, s.BullishSweep.LevelKind);
        }

        [Fact]
        public void FairValueGapDetectedAndTracked()
        {
            var bars = BuildFvgSeries();
            var s = StructureAnalyzer.Analyze(bars, bars.Length - 1, Opt());
            Assert.NotNull(s.LastBullishFvg);
            Assert.True(s.LastBullishFvg.Top > s.LastBullishFvg.Bottom);
        }

        [Fact]
        public void AtrIsPositiveAndBoundedByObservedRanges()
        {
            var bars = BuildTrendingSeries();
            double atr = StructureAnalyzer.AverageTrueRange(bars, 1, bars.Length - 1, 14);
            Assert.True(atr > 0);
            Assert.True(atr < 50);
        }

        // ---- synthetic series builders -------------------------------------------------
        static BarDto[] BuildBars(double[][] hlc)
        {
            var t = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
            var bars = new BarDto[hlc.Length];
            double prev = (hlc[0][0] + hlc[0][1]) / 2;
            for (int i = 0; i < hlc.Length; i++)
            {
                bars[i] = new BarDto(t.AddMinutes(15 * i), prev, hlc[i][0], hlc[i][1], hlc[i][2], 100);
                prev = hlc[i][2];
            }
            return bars;
        }

        static BarDto[] BuildTrendingSeries()
        {
            var rows = new System.Collections.Generic.List<double[]>();
            for (int i = 0; i < 60; i++)
            {
                double mid = 100 + Math.Sin(i / 3.0) * 0.8;
                rows.Add(new[] { mid + 0.3, mid - 0.3, mid });
            }
            return BuildBars(rows.ToArray());
        }

        static BarDto[] BuildBosSeries()
        {
            var rows = new System.Collections.Generic.List<double[]>();
            // quiet range 99.6-100.2 with a clear fractal high at index 6
            double[] highs = { 100.1, 100.15, 100.2, 100.15, 100.1, 100.2, 100.6, 100.2, 100.1, 100.15, 100.2, 100.1, 100.05 };
            // alternate closes so that bearish candles exist (order-block candidate at index 12)
            for (int i = 0; i < highs.Length; i++) rows.Add(new[] { highs[i], 99.6, i % 2 == 0 ? 99.8 : 100.0 });
            // breakout bars closing above 100.6
            rows.Add(new[] { 100.9, 100.0, 100.8 });
            rows.Add(new[] { 101.2, 100.7, 101.1 });
            rows.Add(new[] { 101.4, 101.0, 101.3 });
            return BuildBars(rows.ToArray());
        }

        static BarDto[] BuildSweepSeries()
        {
            var rows = new System.Collections.Generic.List<double[]>();
            double[] lows = { 100.2, 100.1, 100.0, 100.05, 100.1, 99.9, 100.0, 100.05, 100.1, 100.05, 100.0, 100.02 };
            for (int i = 0; i < lows.Length; i++) rows.Add(new[] { 100.4, lows[i], Math.Max(lows[i], 100.2) });
            // pierce 99.9 by 4 ticks and close back above
            rows.Add(new[] { 100.1, 99.86, 100.0 });
            rows.Add(new[] { 100.2, 99.95, 100.15 });
            return BuildBars(rows.ToArray());
        }

        static BarDto[] BuildFvgSeries()
        {
            var rows = new System.Collections.Generic.List<double[]>();
            for (int i = 0; i < 12; i++) rows.Add(new[] { 100.2, 99.9, 100.0 });
            rows.Add(new[] { 100.4, 100.0, 100.3 });
            rows.Add(new[] { 100.8, 100.35, 100.7 });   // gap between bar[-2].High (100.2) and bar.Low (100.35)
            rows.Add(new[] { 101.0, 100.6, 100.9 });
            return BuildBars(rows.ToArray());
        }
    }
}
