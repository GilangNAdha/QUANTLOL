using System;
using System.Collections.Generic;
using cAlgo.Robots;
using Xunit;

namespace MacroAmt.Tests
{
    /// <summary>
    /// Test untuk bagian pure core bot (tanpa cAlgo.API). File bot yang sama di-compile
    /// dengan define MACROAMT_CORE_ONLY, jadi yang diuji benar-benar kode yang dipakai bot.
    /// </summary>
    public class ProfileTests
    {
        static ProfileBar Bar(double o, double h, double l, double c, double v = 100, int day = 0)
        {
            return new ProfileBar(new DateTime(2026, 1, 5).AddDays(day), o, h, l, c, v);
        }

        [Fact]
        public void Build_DistributesVolumeAndComputesValueArea()
        {
            var bars = new List<ProfileBar>
            {
                Bar(10.2, 10.9, 10.1, 10.5, 100),
                Bar(11.2, 11.9, 11.1, 11.5, 200),
                Bar(12.2, 12.9, 12.1, 12.5, 100)
            };

            ProfileHistogram h;
            string err;
            Assert.True(VolumeProfile.TryBuild(bars, 1.0, 4000, out h, out err), err);
            Assert.Equal(400.0, h.TotalVolume, 9);
            Assert.Equal(3, h.Count);
            Assert.Equal(10, h.BaseBin);

            var va = VolumeProfile.ComputeValueArea(h, 70);
            Assert.NotNull(va);
            Assert.Equal(11.5, va.Poc, 9);
            Assert.Equal(11.0, va.Val, 9);
            Assert.Equal(13.0, va.Vah, 9);
            Assert.True(va.Contains(12.0));
            Assert.False(va.Contains(13.5));
        }

        [Fact]
        public void Build_ZeroWidthBarGoesToSingleBin()
        {
            var bars = new List<ProfileBar> { Bar(10.9, 10.9, 10.9, 10.9, 50) };
            ProfileHistogram h;
            string err;
            Assert.True(VolumeProfile.TryBuild(bars, 1.0, 4000, out h, out err), err);
            Assert.Equal(1, h.Count);
            Assert.Equal(50.0, h.TotalVolume, 9);
        }

        [Fact]
        public void Build_RejectsTooManyBins()
        {
            var bars = new List<ProfileBar> { Bar(10, 5000, 10, 20, 100) };
            ProfileHistogram h;
            string err;
            Assert.False(VolumeProfile.TryBuild(bars, 1.0, 100, out h, out err));
            Assert.Contains("bin", err);
        }

        [Fact]
        public void Merge_MatchesSingleBuild()
        {
            var day1 = new List<ProfileBar> { Bar(10.2, 10.9, 10.1, 10.5, 100) };
            var day2 = new List<ProfileBar>
            {
                Bar(11.2, 11.9, 11.1, 11.5, 200, 1),
                Bar(12.2, 12.9, 12.1, 12.5, 100, 1)
            };

            ProfileHistogram h1, h2, merged;
            string err;
            Assert.True(VolumeProfile.TryBuild(day1, 1.0, 4000, out h1, out err), err);
            Assert.True(VolumeProfile.TryBuild(day2, 1.0, 4000, out h2, out err), err);
            Assert.True(VolumeProfile.TryMerge(new[] { h1, h2 }, 4000, out merged, out err), err);

            var all = new List<ProfileBar>();
            all.AddRange(day1);
            all.AddRange(day2);
            ProfileHistogram single;
            Assert.True(VolumeProfile.TryBuild(all, 1.0, 4000, out single, out err), err);

            Assert.Equal(single.TotalVolume, merged.TotalVolume, 9);
            Assert.Equal(single.Count, merged.Count);
            Assert.Equal(single.BaseBin, merged.BaseBin);
            for (int i = 0; i < single.Count; i++) Assert.Equal(single.Bins[i], merged.Bins[i], 9);
        }

        [Fact]
        public void ValueArea_ExpandsTowardsHeavierSide()
        {
            // bin 10=10, 11=100 (POC), 12=10, 13=90 ; 70% = 147
            var bins = new double[] { 10, 100, 10, 90 };
            var h = new ProfileHistogram(1.0, 10, bins, 210, 10.2, 13.8);
            var va = VolumeProfile.ComputeValueArea(h, 70);
            Assert.NotNull(va);
            Assert.Equal(11.5, va.Poc, 9);
            Assert.Equal(11.0, va.Val, 9);
            Assert.Equal(14.0, va.Vah, 9); // 100 -> +90 (bin13) = 190 >= 147
        }

        [Fact]
        public void Engine_BuildsCompositeAndTracksMigration()
        {
            var engine = new ValueProfileEngine(70, 5, 4000);
            string err;

            for (int d = 0; d < 4; d++)
            {
                double lo = 100.1 + d * 2;
                var bars = new List<ProfileBar> { Bar(lo + 1, lo + 7.8, lo, lo + 4, 100, d) };
                DateTime key = new DateTime(2026, 1, 5).AddDays(d);
                Assert.True(engine.IngestDay(key, bars, 1.0, out err), err);
                engine.Rebuild(key);
            }

            Assert.NotNull(engine.Session);
            Assert.NotNull(engine.Composite);
            Assert.Equal(MigrationDirection.Up, engine.Migration.Last);
            Assert.Equal(2, engine.Migration.UpStreak);
        }
    }

    public class RegimeTests
    {
        static ValueProfile P(double poc, double val, double vah)
        {
            return new ValueProfile { Poc = poc, Val = val, Vah = vah, TotalVolume = 1000, BinSize = 1.0, ValueAreaPercent = 70 };
        }

        [Fact]
        public void Compare_UpDownOverlap()
        {
            Assert.Equal(MigrationDirection.Up, RegimeClassifier.Compare(P(105, 100, 110), P(104, 99, 110)));
            Assert.Equal(MigrationDirection.Down, RegimeClassifier.Compare(P(99, 95, 105), P(105, 100, 110)));
            Assert.Equal(MigrationDirection.Overlap, RegimeClassifier.Compare(P(106, 100, 112), P(105, 101, 111)));
        }

        [Fact]
        public void Classify_TrendRangeUnknownAndOverrides()
        {
            Assert.Equal(MarketRegime.TrendUp, RegimeClassifier.Classify(2, 0, 0, 25, RegimeOverrideMode.Auto, 2, 3, 20));
            Assert.Equal(MarketRegime.TrendDown, RegimeClassifier.Classify(0, 3, 0, 25, RegimeOverrideMode.Auto, 2, 3, 20));
            Assert.Equal(MarketRegime.Range, RegimeClassifier.Classify(0, 0, 3, 15, RegimeOverrideMode.Auto, 2, 3, 20));
            Assert.Equal(MarketRegime.Unknown, RegimeClassifier.Classify(0, 0, 3, 25, RegimeOverrideMode.Auto, 2, 3, 20));
            Assert.Equal(MarketRegime.Unknown, RegimeClassifier.Classify(1, 0, 0, 25, RegimeOverrideMode.Auto, 2, 3, 20));
            Assert.Equal(MarketRegime.TrendUp, RegimeClassifier.Classify(0, 0, 5, 10, RegimeOverrideMode.ForceTrend, 2, 3, 20));
            Assert.Equal(MarketRegime.Range, RegimeClassifier.Classify(5, 0, 0, 40, RegimeOverrideMode.ForceRange, 2, 3, 20));
        }

        [Fact]
        public void Tracker_CountsStreaks()
        {
            var t = new MigrationTracker();
            t.Push(MigrationDirection.Up);
            t.Push(MigrationDirection.Up);
            t.Push(MigrationDirection.Overlap);
            Assert.Equal(1, t.OverlapStreak);
            Assert.Equal(0, t.UpStreak);
            t.Push(MigrationDirection.Down);
            Assert.Equal(1, t.DownStreak);
            Assert.Equal(MigrationDirection.Down, t.Last);
        }
    }

    public class CotTests
    {
        static List<string> Csv()
        {
            return new List<string>
            {
                "date,market,noncomm_long,noncomm_short",
                "2026-01-06,GOLD - COMMODITY EXCHANGE INC.,200000,100000",
                "2026-01-13,GOLD - COMMODITY EXCHANGE INC.,210000,100000",
                "2026-01-20,GOLD - COMMODITY EXCHANGE INC.,220000,100000",
                "2026-01-27,GOLD - COMMODITY EXCHANGE INC.,230000,100000",
                "2026-02-03,GOLD - COMMODITY EXCHANGE INC.,240000,100000",
                "2026-02-10,GOLD - COMMODITY EXCHANGE INC.,250000,100000"
            };
        }

        [Fact]
        public void Parse_ComputesWeeklyNetAndChange()
        {
            List<CotRecord> recs; string market; int skipped; string err;
            Assert.True(CotCsv.TryParse(Csv(), "GOLD", out recs, out market, out skipped, out err), err);
            Assert.Equal(6, recs.Count);
            Assert.Contains("GOLD", market);
            Assert.Equal(140000.0, recs[4].Net, 6);
        }

        [Fact]
        public void SelectAsOf_NoLookAheadBeforeFridayRelease()
        {
            List<CotRecord> recs; string market; int skipped; string err;
            Assert.True(CotCsv.TryParse(Csv(), "GOLD", out recs, out market, out skipped, out err), err);

            var before = CotCsv.SelectAsOf(recs, new DateTime(2026, 2, 13, 20, 0, 0, DateTimeKind.Utc), 10);
            Assert.True(before.Available);
            Assert.Equal(new DateTime(2026, 2, 3), before.ReportDate);
            Assert.Equal(40000.0, before.Change4W, 6);

            var after = CotCsv.SelectAsOf(recs, new DateTime(2026, 2, 13, 22, 0, 0, DateTimeKind.Utc), 10);
            Assert.True(after.Available);
            Assert.Equal(new DateTime(2026, 2, 10), after.ReportDate);
            Assert.Equal(150000.0, after.Net, 6);
            Assert.Equal(40000.0, after.Change4W, 6);
        }

        [Fact]
        public void Policy_AgreementSoftAndHard()
        {
            var snap = new CotSnapshot { Available = true, ReportDate = new DateTime(2026, 2, 10), Net = 150000, Change4W = 40000 };
            string why;

            Assert.Equal(CotVerdict.Pass, CotPolicy.Evaluate(snap, TradeSide.Long, CotMode.Hard, StaleCotBehavior.Block, new DateTime(2026, 2, 13, 22, 0, 0), 10, out why));
            Assert.Equal(CotVerdict.Block, CotPolicy.Evaluate(snap, TradeSide.Short, CotMode.Hard, StaleCotBehavior.Block, new DateTime(2026, 2, 13, 22, 0, 0), 10, out why));
            Assert.Equal(CotVerdict.PassSoftCut, CotPolicy.Evaluate(snap, TradeSide.Short, CotMode.Soft, StaleCotBehavior.Block, new DateTime(2026, 2, 13, 22, 0, 0), 10, out why));
            Assert.Equal(CotVerdict.Pass, CotPolicy.Evaluate(snap, TradeSide.Short, CotMode.Off, StaleCotBehavior.Block, new DateTime(2026, 2, 13, 22, 0, 0), 10, out why));
        }

        [Fact]
        public void Policy_StaleDataIgnoreOrBlock()
        {
            var snap = new CotSnapshot { Available = true, ReportDate = new DateTime(2026, 2, 10), Net = 150000, Change4W = 40000 };
            DateTime late = new DateTime(2026, 3, 1, 12, 0, 0);
            string why;
            Assert.Equal(CotVerdict.Block, CotPolicy.Evaluate(snap, TradeSide.Long, CotMode.Hard, StaleCotBehavior.Block, late, 10, out why));
            Assert.Equal(CotVerdict.Pass, CotPolicy.Evaluate(snap, TradeSide.Long, CotMode.Hard, StaleCotBehavior.Ignore, late, 10, out why));
            Assert.Equal(CotVerdict.Pass, CotPolicy.Evaluate(null, TradeSide.Long, CotMode.Hard, StaleCotBehavior.Ignore, late, 10, out why));
        }
    }

    public class BiasAndPresetTests
    {
        [Fact]
        public void ExpiryParsingAndBoundary()
        {
            DateTime exp;
            Assert.True(BiasPolicy.TryParseExpiry("2026-12-31", out exp));
            Assert.Equal(new DateTime(2026, 12, 31), exp);
            Assert.False(BiasPolicy.TryParseExpiry("31/12/2026", out exp));
            Assert.False(BiasPolicy.TryParseExpiry("", out exp));

            DateTime? e = new DateTime(2026, 12, 31);
            Assert.False(BiasPolicy.IsExpired(e, new DateTime(2026, 12, 31, 23, 0, 0)));
            Assert.True(BiasPolicy.IsExpired(e, new DateTime(2027, 1, 1, 0, 0, 1)));
            Assert.False(BiasPolicy.IsExpired(null, new DateTime(2030, 1, 1)));
        }

        [Fact]
        public void ConvictionScale()
        {
            Assert.Equal(0.5, BiasPolicy.ConvictionScale(1), 9);
            Assert.Equal(1.0, BiasPolicy.ConvictionScale(2), 9);
            Assert.Equal(1.5, BiasPolicy.ConvictionScale(3), 9);
            Assert.Equal(0.5, BiasPolicy.ConvictionScale(0), 9);
        }

        [Fact]
        public void ProxyScoresTrendAndInverts()
        {
            var up = new List<double>();
            for (int i = 1; i <= 300; i++) up.Add(100 + i);

            string detail;
            Assert.Equal(BiasDirection.Long, BiasPolicy.ScoreProxy(up, 50, 200, 5, false, out detail));
            Assert.Equal(BiasDirection.Short, BiasPolicy.ScoreProxy(up, 50, 200, 5, true, out detail));

            var flat = new List<double>();
            for (int i = 0; i < 300; i++) flat.Add(100.0);
            Assert.Equal(BiasDirection.Neutral, BiasPolicy.ScoreProxy(flat, 50, 200, 5, false, out detail));

            var shortSeries = new List<double>();
            for (int i = 1; i <= 300; i++) shortSeries.Add(500 - i);
            Assert.Equal(BiasDirection.Short, BiasPolicy.ScoreProxy(shortSeries, 50, 200, 5, false, out detail));
        }

        [Fact]
        public void GoldPresetRules()
        {
            Assert.True(PresetPolicy.Evaluate(MarketPreset.Gold, SignalModel.FailedAuction, TradeSide.Long,
                MarketRegime.TrendUp, BiasDirection.Long, true, true).Allowed);
            Assert.False(PresetPolicy.Evaluate(MarketPreset.Gold, SignalModel.FailedAuction, TradeSide.Long,
                MarketRegime.Range, BiasDirection.Long, true, true).Allowed);
            Assert.False(PresetPolicy.Evaluate(MarketPreset.Gold, SignalModel.MeanReversion, TradeSide.Long,
                MarketRegime.Range, BiasDirection.Long, true, true).Allowed);
            Assert.True(PresetPolicy.Evaluate(MarketPreset.Gold, SignalModel.MeanReversion, TradeSide.Long,
                MarketRegime.Range, BiasDirection.Neutral, true, true).Allowed);
        }

        [Fact]
        public void IndexPresetIsLongOnlyAndNeedsPullback()
        {
            Assert.False(PresetPolicy.Evaluate(MarketPreset.Index, SignalModel.FailedAuction, TradeSide.Short,
                MarketRegime.TrendDown, BiasDirection.Short, true, true).Allowed);
            Assert.False(PresetPolicy.Evaluate(MarketPreset.Index, SignalModel.MeanReversion, TradeSide.Long,
                MarketRegime.TrendUp, BiasDirection.Long, true, true).Allowed);
            Assert.False(PresetPolicy.Evaluate(MarketPreset.Index, SignalModel.BreakoutRetest, TradeSide.Long,
                MarketRegime.TrendUp, BiasDirection.Long, false, true).Allowed);
            Assert.True(PresetPolicy.Evaluate(MarketPreset.Index, SignalModel.BreakoutRetest, TradeSide.Long,
                MarketRegime.TrendUp, BiasDirection.Long, true, true).Allowed);
            Assert.False(PresetPolicy.Evaluate(MarketPreset.VixExperimental, SignalModel.BreakoutRetest, TradeSide.Long,
                MarketRegime.TrendUp, BiasDirection.Long, true, false).Allowed);
        }

        [Fact]
        public void ForexPresetAddsRangeFade()
        {
            Assert.True(PresetPolicy.Evaluate(MarketPreset.Forex, SignalModel.MeanReversion, TradeSide.Short,
                MarketRegime.Range, BiasDirection.Neutral, false, false).Allowed);
            Assert.False(PresetPolicy.Evaluate(MarketPreset.Forex, SignalModel.MeanReversion, TradeSide.Short,
                MarketRegime.Range, BiasDirection.Long, false, false).Allowed);
            Assert.True(PresetPolicy.Evaluate(MarketPreset.Forex, SignalModel.FailedAuction, TradeSide.Long,
                MarketRegime.TrendUp, BiasDirection.Long, false, false).Allowed);
        }

        [Fact]
        public void IndexPullbackMath()
        {
            Assert.True(AuctionSignals.IndexPullbackOk(100, 97.5, 2.0));
            Assert.False(AuctionSignals.IndexPullbackOk(100, 99.0, 2.0));
            Assert.False(AuctionSignals.IndexPullbackOk(0, 99, 2.0));
        }
    }

    public class SignalTests
    {
        static ProfileBar B(double o, double h, double l, double c, double v = 100)
        {
            return new ProfileBar(new DateTime(2026, 1, 5), o, h, l, c, v);
        }

        static AuctionContext Ctx()
        {
            return new AuctionContext
            {
                Poc = 105, Vah = 110, Val = 100, Atr = 4,
                AllowLong = true, AllowShort = true,
                ExcursionMinAtr = 0.25, ExcursionLookback = 5,
                CloseInsideBufferAtr = 0.25, SlBufferAtr = 0.20, MinRr = 1.5,
                BreakBufferAtr = 0.10, RetestZoneAtr = 0.25, RetestSlBufferAtr = 0.30,
                RetestWindowBars = 20,
                RegimeTrendUp = true, RegimeRange = false
            };
        }

        static List<ProfileBar> FailedAuctionLongBars()
        {
            return new List<ProfileBar>
            {
                B(104, 107, 103, 105),
                B(104, 105, 102, 103),
                B(101, 102, 98.5, 99.5),
                B(100, 102, 99, 101.5)
            };
        }

        [Fact]
        public void Model1_LongDetectsFailedAuction()
        {
            var bars = FailedAuctionLongBars();
            var r = AuctionSignals.FailedAuction(bars, 3, TradeSide.Long, Ctx());
            Assert.True(r.Valid, r.Reject);
            Assert.Equal(101.5, r.EntryReference, 9);
            Assert.Equal(97.7, r.Stop, 9);
            Assert.Equal(105, r.Tp1, 9);
            Assert.Equal(110, r.Tp2, 9);
            Assert.Equal(8.5 / 3.8, r.Rr, 9);
        }

        [Fact]
        public void Model1_RejectsWhenRewardRiskTooLow()
        {
            var bars = FailedAuctionLongBars();
            var ctx = Ctx();
            ctx.MinRr = 3.0;
            var r = AuctionSignals.FailedAuction(bars, 3, TradeSide.Long, ctx);
            Assert.False(r.Valid);
            Assert.True(r.Precondition);
            Assert.Contains("RR", r.Reject);
        }

        [Fact]
        public void Model1_RejectsWithoutExcursion()
        {
            var bars = new List<ProfileBar>
            {
                B(104, 107, 103, 105),
                B(104, 105, 102, 103),
                B(102, 104, 101, 102.5),
                B(102, 103, 101, 102.8)
            };
            var r = AuctionSignals.FailedAuction(bars, 3, TradeSide.Long, Ctx());
            Assert.False(r.Valid);
            Assert.False(r.Precondition);
        }

        [Fact]
        public void Model1_ShortMirror()
        {
            var bars = new List<ProfileBar>
            {
                B(104, 106, 103, 105),
                B(105, 108, 104, 107),
                B(110, 111.8, 109.5, 110.5),
                B(110.5, 111.0, 108.2, 108.5)
            };
            var r = AuctionSignals.FailedAuction(bars, 3, TradeSide.Short, Ctx());
            Assert.True(r.Valid, r.Reject);
            Assert.Equal(112.6, r.Stop, 9);
            Assert.Equal(100, r.Tp2, 9);
        }

        [Fact]
        public void Model2_LongAcceptanceRetest()
        {
            var bars = new List<ProfileBar>
            {
                B(104, 106, 103, 105),
                B(105, 112, 104, 111.5),
                B(111.5, 113, 110.8, 112),
                B(111.5, 112.2, 110.5, 111.6)
            };
            var r = AuctionSignals.BreakoutRetest(bars, 3, TradeSide.Long, Ctx());
            Assert.True(r.Valid, r.Reject);
            Assert.Equal(111.6, r.EntryReference, 9);
            Assert.Equal(109.3, r.Stop, 9);
            Assert.Equal(120, r.Tp1, 9);
            Assert.Equal(130, r.Tp2, 9);
        }

        [Fact]
        public void Model2_ShortMirror()
        {
            var bars = new List<ProfileBar>
            {
                B(105, 106, 103, 104),
                B(104, 104.2, 98.8, 99.0),
                B(99.0, 99.2, 97.5, 98.0),
                B(98.5, 99.3, 97.2, 98.0)
            };
            var r = AuctionSignals.BreakoutRetest(bars, 3, TradeSide.Short, Ctx());
            Assert.True(r.Valid, r.Reject);
            Assert.Equal(100.5, r.Stop, 9);
            Assert.Equal(80, r.Tp2, 9);
        }

        [Fact]
        public void Model2_RejectsWithoutAcceptance()
        {
            var bars = new List<ProfileBar>
            {
                B(104, 106, 103, 105),
                B(105, 108, 104, 107),
                B(107, 108, 106, 107.5),
                B(107, 108, 106.5, 107.8)
            };
            var r = AuctionSignals.BreakoutRetest(bars, 3, TradeSide.Long, Ctx());
            Assert.False(r.Valid);
            Assert.Contains("acceptance", r.Reject);
        }

        [Fact]
        public void Model3_NeedsRangeRegime()
        {
            var bars = FailedAuctionLongBars();
            var ctx = Ctx();
            ctx.RegimeRange = false;
            var r = AuctionSignals.MeanReversion(bars, 3, TradeSide.Long, ctx);
            Assert.False(r.Valid);

            ctx.RegimeRange = true;
            r = AuctionSignals.MeanReversion(bars, 3, TradeSide.Long, ctx);
            Assert.True(r.Valid, r.Reject);
        }
    }

    public class RiskAndGateTests
    {
        [Fact]
        public void RiskPercentRespectsCap()
        {
            Assert.Equal(0.25, Sizing.RiskPercent(0.5, 1, 2), 9);
            Assert.Equal(0.75, Sizing.RiskPercent(0.5, 3, 2), 9);
            Assert.Equal(2.0, Sizing.RiskPercent(2.0, 3, 2), 9);
        }

        [Fact]
        public void UnitsFromRiskAndValuePerUnit()
        {
            Assert.Equal(1.0, Sizing.ValuePerPricePerUnit(1.0, 0.01, 100), 9);
            Assert.Equal(1.0395, Sizing.UnitsFromRisk(5, 4.81, 1.0), 6);
            Assert.Equal(0, Sizing.UnitsFromRisk(0, 4.81, 1.0), 9);
            Assert.Equal(0, Sizing.UnitsFromRisk(5, 0, 1.0), 9);
        }

        [Fact]
        public void TotalRiskGuard()
        {
            string why;
            Assert.True(Sizing.TotalRiskOk(1.0, 0.5, 3.0, out why));
            Assert.False(Sizing.TotalRiskOk(2.8, 0.5, 3.0, out why));
            Assert.Contains("total risk", why);
        }

        [Fact]
        public void CorrelationGroupsParseAndMatch()
        {
            List<List<string>> groups; string err;
            Assert.True(Sizing.TryParseGroups("EURCHF,USDCHF;AUDCAD,AUDUSD", out groups, out err), err);
            Assert.Equal(2, groups.Count);
            Assert.Equal("EURCHF+USDCHF", Sizing.GroupOf("EURCHF.a", groups));
            Assert.Equal("EURCHF+USDCHF", Sizing.GroupOf("USDCHF", groups));
            Assert.Equal("AUDCAD+AUDUSD", Sizing.GroupOf("AUDUSD", groups));
            Assert.Equal("", Sizing.GroupOf("XAUUSD", groups));

            Assert.False(Sizing.TryParseGroups("EURCHF;EURCHF,USDCHF", out groups, out err));
            Assert.False(Sizing.TryParseGroups("EURCHF", out groups, out err));
        }

        [Fact]
        public void SignalGateResetsOnNewProfileAndOppositeEdge()
        {
            var gate = new SignalGate();
            gate.OnNewProfile("A");
            gate.Block(TradeSide.Long);
            Assert.True(gate.IsBlocked(TradeSide.Long));

            gate.ObserveClose(111, 110, 100);
            Assert.False(gate.IsBlocked(TradeSide.Long));

            gate.Block(TradeSide.Short);
            gate.ObserveClose(99, 110, 100);
            Assert.False(gate.IsBlocked(TradeSide.Short));

            gate.Block(TradeSide.Short);
            gate.OnNewProfile("B");
            Assert.False(gate.IsBlocked(TradeSide.Short));
        }

        [Fact]
        public void FridayCutoffOnlyAfterConfiguredHour()
        {
            string why;
            Assert.True(TimeGuards.FridayCutoff(new DateTime(2026, 1, 2, 20, 30, 0), 20, out why));
            Assert.False(TimeGuards.FridayCutoff(new DateTime(2026, 1, 2, 19, 59, 0), 20, out why));
            Assert.False(TimeGuards.FridayCutoff(new DateTime(2026, 1, 1, 22, 0, 0), 20, out why));
            Assert.False(TimeGuards.FridayCutoff(new DateTime(2026, 1, 2, 22, 0, 0), -1, out why));
            Assert.False(TimeGuards.FridayCutoff(new DateTime(2026, 1, 2, 22, 0, 0), 23, out why));
        }
    }
}
