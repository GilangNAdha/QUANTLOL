using System;
using QuantLOL.Core;
using Xunit;

namespace QuantLOL.Tests
{
    public class ExternalDataTests
    {
        static readonly DateTime Now = new DateTime(2026, 10, 20, 18, 0, 0, DateTimeKind.Utc);

        const string ValidGex = @"{
          ""instrument"": ""XAUUSD"",
          ""as_of"": ""2026-10-20T17:30:00Z"",
          ""quote"": ""USD"",
          ""spot"": 4321.5,
          ""gamma_flip"": 4300.0,
          ""call_wall"": 4400.0,
          ""put_wall"": 4250.0,
          ""net_gex"": -1250000000,
          ""delta_exposure"": 987654,
          ""vanna"": -1234,
          ""levels"": [ { ""price"": 4350, ""kind"": ""call_wall"" }, { ""price"": 4290, ""kind"": ""gamma_flip"" } ],
          ""greeks_note"": ""dealer-long-gamma positive; provider units"",
          ""expiry"": ""2026-11-20""
        }";

        [Fact]
        public void ValidGexFileParsesWithProvenance()
        {
            var g = ExternalDataParser.ParseGex(ValidGex, "XAUUSD", Now, 200, 20000, out string err);
            Assert.Null(err);
            Assert.True(g.Valid);
            Assert.Equal(DataQuality.Manual, g.Quality);
            Assert.Equal(4300.0, g.GammaFlip, 6);
            Assert.Equal(4250.0, g.PutWall, 6);
            Assert.True(g.NetGexSupplied && g.GammaRegimeNegative);
            Assert.Equal(4, g.Levels.Count);   // two from the array + flip + call wall (duplicate price kept as separate refs)
            Assert.Equal(new DateTime(2026, 10, 20, 17, 30, 0, DateTimeKind.Utc), g.AsOfUtc);
        }

        [Fact]
        public void GldDerivedDataIsMarkedAsProxy()
        {
            string json = ValidGex.Replace("\"XAUUSD\"", "\"GLD\"").Replace("4321.5", "398.5")
                                  .Replace("\"4300.0\"", "\"395.0\"").Replace("4300.0", "395.0").Replace("4400.0", "405.0")
                                  .Replace("4250.0", "390.0").Replace("4350", "400").Replace("4290", "394");
            var g = ExternalDataParser.ParseGex(json, "XAUUSD", Now, 200, 20000, out string err);
            Assert.Null(err);
            Assert.Equal(DataQuality.Proxy, g.Quality);
        }

        [Fact]
        public void InstrumentMismatchIsRejected()
        {
            var g = ExternalDataParser.ParseGex(ValidGex.Replace("\"XAUUSD\"", "\"SPX\""), "XAUUSD", Now, 200, 20000, out string err);
            Assert.False(g.Valid);
            Assert.Contains("instrument mismatch", err);
        }

        [Fact]
        public void ImplausiblePriceScaleIsRejected()
        {
            // GLD-scale prices filed as XAUUSD: must fail the sanity band, not silently distort levels.
            var json = ValidGex.Replace("\"spot\": 4321.5", "\"spot\": 398.5")
                               .Replace("4300.0", "395.0").Replace("4400.0", "405.0").Replace("4250.0", "390.0")
                               .Replace("4350", "400").Replace("4290", "394");
            var g = ExternalDataParser.ParseGex(json, "XAUUSD", Now, 200, 20000, out string err);
            Assert.False(g.Valid);
            Assert.Contains("outside sane range", err);
        }

        [Fact]
        public void FutureTimestampsAreRejected()
        {
            var json = ValidGex.Replace("2026-10-20T17:30:00Z", "2026-10-21T17:30:00Z");
            var g = ExternalDataParser.ParseGex(json, "XAUUSD", Now, 200, 20000, out string err);
            Assert.False(g.Valid);
            Assert.Contains("future", err);
        }

        [Fact]
        public void EpochMillisecondTimestampsAreAccepted()
        {
            var json = ValidGex.Replace("\"2026-10-20T17:30:00Z\"", "1760981400000");
            var g = ExternalDataParser.ParseGex(json, "XAUUSD", Now, 200, 20000, out string err);
            Assert.Null(err);
            Assert.Equal(new DateTime(2026, 10, 20, 17, 30, 0, DateTimeKind.Utc), g.AsOfUtc);
        }

        [Fact]
        public void FreshnessAndStalePolicy()
        {
            var g = ExternalDataParser.ParseGex(ValidGex, "XAUUSD", Now, 200, 20000, out _);
            Assert.True(g.IsFresh(Now, 240));
            var later = Now.AddHours(6);
            Assert.False(g.IsFresh(later, 240));
            Assert.Contains("STALE", g.Status(later, 240));

            var block = ExternalDataParser.Assess(g, later, 240, StalePolicy.BlockEntries, required: false);
            Assert.True(block.BlocksEntry);
            var warn = ExternalDataParser.Assess(g, later, 240, StalePolicy.WarnAndIgnore, required: false);
            Assert.False(warn.BlocksEntry);
            Assert.True(warn.Fresh == false);
        }

        [Fact]
        public void MissingDataBlocksOnlyWhenRequired()
        {
            var a = ExternalDataParser.Assess(null, Now, 240, StalePolicy.BlockEntries, required: false);
            Assert.True(a.BlocksEntry, "enabled-but-missing data under BlockEntries policy must block");
            var b = ExternalDataParser.Assess(null, Now, 240, StalePolicy.WarnAndIgnore, required: false);
            Assert.False(b.BlocksEntry);
        }

        [Fact]
        public void VixParsingValidatesLevelAndTime()
        {
            var v = ExternalDataParser.ParseVix("{\"vix\":18.4,\"as_of\":\"2026-10-20T20:15:00Z\",\"quality\":\"live\"}", Now, out string err);
            Assert.Null(err);
            Assert.Equal(18.4, v.Level, 6);
            Assert.Equal(DataQuality.Live, v.Quality);

            var bad = ExternalDataParser.ParseVix("{\"vix\":0,\"as_of\":\"2026-10-20T20:15:00Z\"}", Now, out string err2);
            Assert.False(bad.Valid);
            Assert.Contains("implausible", err2);
        }
    }
}
