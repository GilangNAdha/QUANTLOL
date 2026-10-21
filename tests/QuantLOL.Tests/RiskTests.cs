using System;
using QuantLOL.Core;
using Xunit;

namespace QuantLOL.Tests
{
    public class RiskTests
    {
        static readonly DateTime Day = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void DailyLossLimitBlocksRestOfDayAndResetsNextDay()
        {
            var g = new DrawdownGuard(1000, 15, 30);
            g.OnEquity(1000, Day.AddHours(9));
            Assert.True(g.Evaluate(1000, Day.AddHours(9)).Allowed);

            g.OnTradeClosed(-6, Day.AddHours(10), 994);
            g.OnTradeClosed(-6, Day.AddHours(11), 988);
            var s = g.Evaluate(988, Day.AddHours(11));
            Assert.True(s.Allowed, "12 USD loss is still under the 15 USD daily limit");

            g.OnTradeClosed(-3.5, Day.AddHours(12), 984.5);
            s = g.Evaluate(984.5, Day.AddHours(12));
            Assert.False(s.Allowed);
            Assert.Contains("daily loss", s.Reason);

            // next UTC day resets the daily counter, but the 30 USD equity drawdown is still armed
            var next = Day.AddDays(1).AddHours(1);
            var s2 = g.Evaluate(984.5, next);
            Assert.True(s2.Allowed, "new day resets daily realised loss");
            Assert.Equal(0.0, s2.DailyRealizedPnl, 9);
        }

        [Fact]
        public void DayEquityDrawdownBlocksWithoutLatchedKillSwitch()
        {
            var g = new DrawdownGuard(1000, 15, 30);
            g.OnEquity(1000, Day.AddHours(8));
            g.OnEquity(1020, Day.AddHours(9));                  // equity grows above the reference
            var nextDay = Day.AddDays(1);
            g.OnEquity(1020, nextDay);                          // new day reference = 1020
            g.OnEquity(990, nextDay.AddHours(1));               // 30 USD below the day reference
            var s = g.Evaluate(990, nextDay.AddHours(1));
            Assert.False(s.Allowed);
            Assert.Contains("drawdown", s.Reason);
            Assert.False(g.KillSwitchLatched, "reference-balance drawdown is only 10 USD, so no latch");
        }

        [Fact]
        public void LatchSurvivesRecoveryAndOnlyResetClearsIt()
        {
            var g = new DrawdownGuard(1000, 15, 30) { LatchKillSwitch = true };
            g.OnEquity(1000, Day);
            g.OnEquity(969, Day.AddMinutes(5));
            Assert.True(g.KillSwitchLatched);
            g.OnEquity(1010, Day.AddDays(1));
            Assert.False(g.Evaluate(1010, Day.AddDays(1)).Allowed, "latched kill switch must survive recovery");
            g.ResetKillSwitch(1010);
            Assert.True(g.Evaluate(1010, Day.AddDays(1)).Allowed);
        }

        [Fact]
        public void LifetimeDrawdownFromReferenceBalanceLatches()
        {
            var g = new DrawdownGuard(1000, 15, 30);
            g.OnEquity(1000, Day);
            g.OnEquity(969, Day.AddMinutes(5));
            Assert.False(g.Evaluate(969, Day.AddMinutes(5)).Allowed);
            Assert.True(g.KillSwitchLatched, "reference balance drawdown >= 30 must latch");
        }

        [Fact]
        public void OpenPositionPnlCountsThroughEquity()
        {
            var g = new DrawdownGuard(1000, 15, 30);
            g.OnEquity(1000, Day);
            // equity includes floating PnL, so a large open loss triggers the guard without a closed trade
            var s = g.Evaluate(969, Day.AddMinutes(1));
            Assert.False(s.Allowed);
        }

        [Fact]
        public void CooldownAndDuplicateSuppression()
        {
            var gate = new TradingGate();
            var t = Day.AddHours(10);
            Assert.True(gate.CanOpen(t, 5, 15, out _));
            gate.OnEntry(t, "L-1");
            Assert.False(gate.CanOpen(t.AddMinutes(2), 5, 15, out string why));
            Assert.Contains("cool-down", why);
            Assert.True(gate.CanOpen(t.AddMinutes(6), 5, 15, out _));
            Assert.True(gate.IsDuplicate("L-1"));
            Assert.False(gate.IsDuplicate("L-2"));

            gate.OnExit(t.AddMinutes(7), -1.0);
            Assert.False(gate.CanOpen(t.AddMinutes(10), 5, 15, out string why2));
            Assert.Contains("after loss", why2);
            Assert.True(gate.CanOpen(t.AddMinutes(30), 5, 15, out _));
        }

        [Fact]
        public void RejectsInadequateRewardToRiskEvenWhenRiskFits()
        {
            var n = RiskMath.Compute(100, 99.5, 100.6, 1.0, 0.14, 0.05);
            Assert.True(n.RiskUsd <= 5.0);
            Assert.False(RiskMath.Approve(n, new RiskCaps { MaxRiskPerTradeUsd = 5, MinRewardRisk = 1.5 }, out string why));
            Assert.Contains("R:R", why);
        }

        [Fact]
        public void StopIsNeverTightenedToFitTheCap()
        {
            // Structural stop 12 USD away at this volume: must be rejected, not shrunk.
            var n = RiskMath.Compute(3300, 3288, 3340, 1.0, 0.14, 0.05);
            Assert.False(RiskMath.Approve(n, new RiskCaps { MaxRiskPerTradeUsd = 5 }, out string why));
            Assert.Contains("planned risk", why);
            Assert.Equal(12.0, n.StopDistance, 9);
        }
    }
}
