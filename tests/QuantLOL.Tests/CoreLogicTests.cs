using System;
using System.Collections.Generic;
using System.Linq;
using QuantLOL.Core;
using Xunit;

namespace QuantLOL.Tests
{
    public class NumericTests
    {
        [Fact]
        public void PercentilesUseLinearInterpolation()
        {
            var v = new[] { 1.0, 2, 3, 4, 5 };
            Assert.Equal(3.0, Numeric.Percentile(v, 0.5), 9);
            Assert.Equal(1.0, Numeric.Percentile(v, 0), 9);
            Assert.Equal(5.0, Numeric.Percentile(v, 1), 9);
            Assert.Equal(4.8, Numeric.Percentile(v, 0.95), 9);
            Assert.True(double.IsNaN(Numeric.Percentile(new double[0], 0.5)));
        }

        [Fact]
        public void RoundToTickIsStableAtMidpoints()
        {
            Assert.Equal(1.23, Numeric.RoundToTick(1.2349, 0.01), 9);
            Assert.Equal(1.24, Numeric.RoundToTick(1.235, 0.01), 9);
            Assert.Equal(3301.5, Numeric.RoundToTick(3301.5, 0.01), 9);
        }

        [Fact]
        public void EpochHeuristicHandlesSecondsAndMilliseconds()
        {
            Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), Numeric.EpochToUtc(1767323045));
            Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), Numeric.EpochToUtc(1767323045000));
        }
    }

    public class JsonTests
    {
        [Fact]
        public void ParsesObjectsArraysAndNumbers()
        {
            var j = JsonValue.Parse("{\"a\":1,\"b\":[1,2,3],\"c\":\"x\",\"d\":true,\"e\":null,\"f\":1.5e2}");
            Assert.Equal(1.0, j.Num("a").Value, 9);
            Assert.Equal(3, j.Get("b").Array.Count);
            Assert.Equal("x", j.Str("c"));
            Assert.True(j.Flag("d").Value);
            Assert.True(j.Get("e").IsNull);
            Assert.Equal(150.0, j.Num("f").Value, 9);
        }

        [Fact]
        public void KeysAreCaseInsensitive()
        {
            var j = JsonValue.Parse("{\"Instrument\":\"XAUUSD\",\"As_Of\":\"2026-01-01T00:00:00Z\"}");
            Assert.Equal("XAUUSD", j.Str("instrument"));
            Assert.Equal("2026-01-01T00:00:00Z", j.Str("as_of"));
        }

        [Fact]
        public void RejectsMalformedDocuments()
        {
            Assert.Throws<JsonException>(() => JsonValue.Parse("{"));
            Assert.Throws<JsonException>(() => JsonValue.Parse("{\"a\":}"));
            Assert.Throws<JsonException>(() => JsonValue.Parse("{\"a\":1} trailing"));
            Assert.Throws<JsonException>(() => JsonValue.Parse(""));
        }
    }

    public class MarketProfileTests
    {
        static BarDto[] BarsWithVolumeConcentration()
        {
            var t = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
            var list = new List<BarDto>();
            for (int i = 0; i < 100; i++)
            {
                // 80% of the volume concentrates around 105; the remaining bars sit at 100.5-101.4 and 108.5-109.4
                double price = i < 80 ? 105.0 : (i < 90 ? 100.5 + (i - 80) * 0.1 : 108.5 + (i - 90) * 0.1);
                double vol = i < 80 ? 1000 : 100;
                list.Add(new BarDto(t.AddMinutes(i), price, price + 0.2, price - 0.2, price, vol));
            }
            return list.ToArray();
        }

        [Fact]
        public void PocValueAreaAndSessionExtremes()
        {
            var bars = BarsWithVolumeConcentration();
            var p = MarketProfileBuilder.Build(bars, 0, bars.Length - 1, binSize: 0.5, valueAreaPercent: 70, initialBalanceBars: 10);
            Assert.True(p.HasData);
            Assert.InRange(p.Poc, 104.5, 105.5);
            Assert.True(p.Val <= p.Poc && p.Poc <= p.Vah);
            Assert.Equal(109.6, p.SessionHigh, 6);
            Assert.Equal(100.3, p.SessionLow, 6);
            Assert.Equal(bars.Sum(b => b.Volume), p.TotalVolume, 6);
            Assert.True(p.InitialBalanceHigh >= p.InitialBalanceLow);
        }

        [Fact]
        public void ValueAreaContainsAtLeastTheRequestedShare()
        {
            var bars = BarsWithVolumeConcentration();
            var p = MarketProfileBuilder.Build(bars, 0, bars.Length - 1, 0.5, 70, 10);
            double inVa = 0;
            for (int i = 0; i < p.BinPrices.Length; i++)
                if (p.BinPrices[i] >= p.Val && p.BinPrices[i] <= p.Vah) inVa += p.BinVolumes[i];
            Assert.True(inVa / p.TotalVolume >= 0.69, "value area must cover >= 70% of volume, got " + inVa / p.TotalVolume);
        }

        [Fact]
        public void EmptyAndDegenerateInputsAreSafe()
        {
            Assert.False(MarketProfileBuilder.Build(new BarDto[0], 0, 0, 0.1, 70, 5).HasData);
            var flat = new[] { new BarDto(DateTime.UtcNow, 100, 100, 100, 100, 10) };
            Assert.False(MarketProfileBuilder.Build(flat, 0, 0, 0.1, 70, 1).HasData);
        }
    }

    public class OrderFlowTests
    {
        static BarDto[] Series(bool up)
        {
            var t = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
            var b = new BarDto[60];
            for (int i = 0; i < 60; i++)
            {
                double basePrice = 100 + (up ? i * 0.05 : -i * 0.05);
                b[i] = new BarDto(t.AddMinutes(i), basePrice, basePrice + 0.1, basePrice - 0.1, basePrice + (up ? 0.08 : -0.08), 100);
            }
            return b;
        }

        [Fact]
        public void RisingClosesProducePositiveDelta()
        {
            var r = OrderFlowAnalyzer.Compute(Series(true), 59, 60, 100);
            Assert.True(r.DeltaRatio > 0.5, "delta ratio " + r.DeltaRatio);
            Assert.Equal(60, r.BarsUsed);
        }

        [Fact]
        public void FallingClosesProduceNegativeDelta()
        {
            var r = OrderFlowAnalyzer.Compute(Series(false), 59, 60, 100);
            Assert.True(r.DeltaRatio < -0.5, "delta ratio " + r.DeltaRatio);
        }

        [Fact]
        public void ZeroRangeBarsDoNotDivideByZero()
        {
            var t = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
            var bars = new BarDto[10];
            for (int i = 0; i < 10; i++) bars[i] = new BarDto(t.AddMinutes(i), 100, 100, 100, 100, 50);
            var r = OrderFlowAnalyzer.Compute(bars, 9, 8, 50);
            Assert.Equal(0.0, r.DeltaRatio, 9);
        }
    }

    public class LatencyTests
    {
        [Fact]
        public void SummaryReportsPercentilesAndCount()
        {
            var s = new LatencySeries("t", 64);
            for (int i = 1; i <= 100; i++) s.RecordMs(i);
            var snap = s.Snapshot();
            Assert.Equal(64, snap.Count);          // ring buffer capacity
            Assert.True(snap.P50Ms > 0 && snap.P99Ms >= snap.P50Ms && snap.MaxMs >= snap.P99Ms);
            Assert.True(snap.MaxMs <= 100);
        }

        [Fact]
        public void EmptySeriesIsSafe()
        {
            var snap = new LatencySeries("empty").Snapshot();
            Assert.Equal(0, snap.Count);
        }

        [Fact]
        public void RegistryTableExplainsNetworkAttribution()
        {
            var reg = new LatencyRegistry();
            reg.TickToSignal.RecordMs(0.5);
            reg.SubmitToAck.RecordMs(12);
            string table = reg.RenderTable();
            Assert.Contains("tick->signal_eval", table);
            Assert.Contains("NOT internal computation time", table);
        }
    }
}
