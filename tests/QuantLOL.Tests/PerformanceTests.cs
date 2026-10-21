using System;
using System.Collections.Generic;
using QuantLOL.Core;
using Xunit;

namespace QuantLOL.Tests
{
    public class PerformanceTests
    {
        static TradeRecord T(int id, double pnl, string session = "London", string setup = "sweep_choch", double mae = -1, double mfe = 3)
        {
            var entry = new DateTime(2026, 5, 4, 10, 0, 0, DateTimeKind.Utc).AddHours(id);
            return new TradeRecord
            {
                Id = id, NetPnl = pnl, GrossPnl = pnl + 0.14, CommissionUsd = 0.14,
                Direction = pnl >= 0 ? "LONG" : "SHORT", Session = session, SetupType = setup,
                Regime = "Balance", ExitReason = "take_profit", EntryTimeUtc = entry, ExitTimeUtc = entry.AddMinutes(45),
                EntryPrice = 3300, StopPrice = 3299.5, VolumeUnits = 1, RiskUsd = 0.5,
                RMultiple = pnl / 0.5, MaeUsd = mae, MfeUsd = mfe, LatencyDecisionMs = 3.5,
                SpreadEntryTicks = 20
            };
        }

        [Fact]
        public void CoreAggregatesAreCorrect()
        {
            var trades = new List<TradeRecord> { T(1, 2.0), T(2, -1.0), T(3, 3.0), T(4, -1.0) };
            var m = PerformanceAnalyzer.Compute(trades);
            Assert.Equal(4, m.TradeCount);
            Assert.Equal(3.0, m.NetProfit, 9);
            Assert.Equal(5.0, m.GrossProfit, 9);
            Assert.Equal(-2.0, m.GrossLoss, 9);
            Assert.Equal(2.5, m.ProfitFactor, 9);
            Assert.Equal(0.75, m.Expectancy, 9);
            Assert.Equal(50.0, m.WinRate, 9);
            Assert.Equal(2.5, m.AvgWin, 9);
            Assert.Equal(-1.0, m.AvgLoss, 9);
        }

        [Fact]
        public void MaxDrawdownFollowsTheClosedEquityCurve()
        {
            var trades = new List<TradeRecord> { T(1, 2), T(2, -3), T(3, -2), T(4, 5) };
            var m = PerformanceAnalyzer.Compute(trades);
            // cumulative: 2, -1, -3, 2 -> peak 2, trough -3 -> dd 5
            Assert.Equal(5.0, m.MaxDrawdown, 9);
            Assert.Equal(0.4, m.RecoveryFactor, 9);
        }

        [Fact]
        public void SmallSamplesAreFlaggedNotAnnualised()
        {
            var m = PerformanceAnalyzer.Compute(new List<TradeRecord> { T(1, 1), T(2, -1), T(3, 2) });
            Assert.Contains(m.Warnings, w => w.Contains("sample size"));
            Assert.Contains(m.Warnings, w => w.Contains("Sharpe"));
            Assert.True(double.IsNaN(m.Sharpe));
            Assert.True(m.BootstrapAvailable);
            Assert.True(double.IsFinite(m.ExpectancyCiLow) && double.IsFinite(m.ExpectancyCiHigh));
            Assert.True(m.ExpectancyCiLow <= m.ExpectancyCiHigh);
        }

        [Fact]
        public void EmptyInputProducesAWarningNotAnException()
        {
            var m = PerformanceAnalyzer.Compute(new List<TradeRecord>());
            Assert.Equal(0, m.TradeCount);
            Assert.NotEmpty(m.Warnings);
            Assert.Contains("undefined", PerformanceAnalyzer.RenderMarkdown(m, "t", ""), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BreakdownsUseTheDeclaredDimensions()
        {
            var trades = new List<TradeRecord> { T(1, 1, "Asia"), T(2, -1, "Asia"), T(3, 2, "NewYork") };
            var m = PerformanceAnalyzer.Compute(trades);
            Assert.Equal(2, m.BySession.Count);
            Assert.Contains(m.BySession, b => b.Key == "Asia" && b.Count == 2);
            Assert.Contains(m.BySession, b => b.Key == "NewYork" && b.Net == 2);
        }

        [Fact]
        public void MarkdownReportContainsAllHeadlineMetrics()
        {
            var md = PerformanceAnalyzer.RenderMarkdown(PerformanceAnalyzer.Compute(new List<TradeRecord> { T(1, 2), T(2, -1) }), "Test", "provenance");
            foreach (var key in new[] { "Closed trades", "Profit factor", "Expectancy", "Max closed-trade drawdown", "Recovery factor", "Commission total", "Caveats" })
                Assert.Contains(key, md);
        }
    }
}
