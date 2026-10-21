using System;
using System.Collections.Generic;
using System.Globalization;

namespace QuantLOL.Core
{
    /// <summary>
    /// Provenance/quality of an external input. The bot reports this verbatim in logs and
    /// in the trade journal so a reader can tell a manual level from a live feed.
    /// </summary>
    public enum DataQuality
    {
        Unavailable = 0,
        Manual = 1,
        PublicDelayed = 2,
        Live = 3,
        Proxy = 4,        // e.g. GLD-derived exposure presented as a gold proxy
        Invalid = 5
    }

    public enum StalePolicy { BlockEntries = 0, WarnAndIgnore = 1 }

    public abstract class ExternalInput
    {
        public string Name = "";
        public string Source = "";
        public string InstrumentTag = "";
        public DateTime AsOfUtc = DateTime.MinValue;
        public DateTime ReceivedUtc = DateTime.MinValue;
        public DataQuality Quality = DataQuality.Unavailable;
        public bool Valid;
        public string Error = "";

        public double AgeMinutes(DateTime nowUtc)
            => AsOfUtc == DateTime.MinValue ? double.PositiveInfinity : (nowUtc - AsOfUtc).TotalMinutes;

        public bool IsFresh(DateTime nowUtc, int maxAgeMinutes)
            => Valid && AsOfUtc != DateTime.MinValue && AgeMinutes(nowUtc) <= maxAgeMinutes && AgeMinutes(nowUtc) >= -5;

        public string Status(DateTime nowUtc, int maxAgeMinutes)
        {
            if (!Valid) return Name + ":INVALID(" + (string.IsNullOrEmpty(Error) ? "no data" : Error) + ")";
            if (IsFresh(nowUtc, maxAgeMinutes)) return Name + ":FRESH(" + Quality + ",age=" + Numeric.F(AgeMinutes(nowUtc), 1) + "m)";
            return Name + ":STALE(age=" + Numeric.F(AgeMinutes(nowUtc), 1) + "m>" + maxAgeMinutes + "m," + Quality + ")";
        }
    }

    /// <summary>Options-derived exposure context. Levels are USD/oz price levels.</summary>
    public sealed class GexSnapshot : ExternalInput
    {
        public double Spot;
        public double GammaFlip;          // NaN when absent
        public double CallWall;
        public double PutWall;
        public double NetGex;             // NaN when absent
        public bool NetGexSupplied;
        public double DeltaExposure = double.NaN;
        public double Vanna = double.NaN;
        public string GreeksNote = "";
        public string UnitsNote = "";
        public string ExpiryNote = "";
        public readonly List<LevelRef> Levels = new List<LevelRef>();

        public bool GammaRegimePositive => NetGexSupplied && NetGex > 0;
        public bool GammaRegimeNegative => NetGexSupplied && NetGex < 0;

        public string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("flip=").Append(Format(GammaFlip)).Append(" callWall=").Append(Format(CallWall)).Append(" putWall=").Append(Format(PutWall));
            sb.Append(" netGex=").Append(NetGexSupplied ? Numeric.F(NetGex, 0) : "n/a");
            sb.Append(" delta=").Append(Format(DeltaExposure)).Append(" vanna=").Append(Format(Vanna));
            sb.Append(" levels=").Append(Levels.Count).Append(" src=").Append(Source).Append(" quality=").Append(Quality);
            if (!string.IsNullOrEmpty(ExpiryNote)) sb.Append(" expiry=").Append(ExpiryNote);
            return sb.ToString();
        }

        static string Format(double v) => Numeric.IsFinite(v) ? Numeric.F(v, 2) : "n/a";
    }

    public sealed class VixSnapshot : ExternalInput
    {
        public double Level = double.NaN;
        public double? Change1D;
        public string Describe() => "VIX=" + (Numeric.IsFinite(Level) ? Numeric.F(Level, 2) : "n/a") + " asof=" + AsOfUtc.ToString("yyyy-MM-dd HH:mm") + "z quality=" + Quality;
    }

    public sealed class ExternalAssessment
    {
        public bool BlocksEntry;
        public string Status = "";
        public bool Fresh;
    }

    public static class ExternalDataParser
    {
        /// <summary>
        /// Parses a GEX file. Schema (all keys case-insensitive):
        /// {
        ///   "instrument": "XAUUSD" | "GLD",          // required; GLD is flagged as Proxy
        ///   "as_of": "2026-10-20T18:00:00Z" | 1760983200000,
        ///   "quote": "USD",                           // optional, must be USD when present
        ///   "spot": 4321.5,                           // optional sanity anchor
        ///   "gamma_flip": 4300, "call_wall": 4400, "put_wall": 4250,
        ///   "net_gex": -1.2e9,                        // optional; sign convention: dealer long gamma > 0
        ///   "delta_exposure": 123456, "vanna": -987,  // optional, provider defined units
        ///   "levels": [{ "price": 4350, "kind": "call_wall" }, ...],
        ///   "greeks_note": "provider methodology", "expiry": "2026-11-20"
        /// }
        /// </summary>
        public static GexSnapshot ParseGex(string json, string expectedInstrument, DateTime receivedUtc,
                                           double minPrice, double maxPrice, out string error)
        {
            error = null;
            var g = new GexSnapshot { Name = "GEX", ReceivedUtc = receivedUtc, Valid = false };
            if (string.IsNullOrWhiteSpace(json)) { error = "empty"; g.Error = error; return g; }
            JsonValue root;
            try { root = JsonValue.Parse(json); }
            catch (Exception ex) { error = "json:" + ex.Message; g.Error = error; return g; }

            string inst = root.Str("instrument");
            if (string.IsNullOrWhiteSpace(inst)) { error = "missing instrument"; g.Error = error; return g; }
            g.InstrumentTag = inst.Trim().ToUpperInvariant();

            string expected = (expectedInstrument ?? "").Trim().ToUpperInvariant();
            bool isExact = g.InstrumentTag == expected || g.InstrumentTag.Replace("/", "") == expected.Replace("/", "");
            bool isKnownProxy = g.InstrumentTag == "GLD" || g.InstrumentTag == "IAU" || g.InstrumentTag == "GLD_PROXY";
            if (!isExact && !isKnownProxy)
            {
                error = "instrument mismatch: got '" + inst + "', expected '" + expectedInstrument + "' (or a documented GLD/IAU proxy)";
                g.Error = error; return g;
            }
            g.Quality = isExact ? DataQuality.Manual : DataQuality.Proxy;
            g.Source = root.Str("source") ?? (isExact ? "manual-file" : "proxy-file");

            var asOf = ReadTime(root, "as_of");
            if (asOf == null) { error = "missing/invalid as_of"; g.Error = error; return g; }
            if (asOf.Value > receivedUtc.AddMinutes(5)) { error = "as_of in the future"; g.Error = error; return g; }
            g.AsOfUtc = asOf.Value;

            string quote = root.Str("quote");
            if (!string.IsNullOrEmpty(quote) && !quote.Equals("USD", StringComparison.OrdinalIgnoreCase))
            {
                error = "quote must be USD, got '" + quote + "'"; g.Error = error; return g;
            }

            g.Spot = root.Num("spot") ?? double.NaN;
            g.GammaFlip = root.Num("gamma_flip") ?? double.NaN;
            g.CallWall = root.Num("call_wall") ?? double.NaN;
            g.PutWall = root.Num("put_wall") ?? double.NaN;
            var netGex = root.Num("net_gex");
            if (netGex.HasValue) { g.NetGex = netGex.Value; g.NetGexSupplied = true; }
            g.DeltaExposure = root.Num("delta_exposure") ?? double.NaN;
            g.Vanna = root.Num("vanna") ?? double.NaN;
            g.GreeksNote = root.Str("greeks_note") ?? "";
            g.UnitsNote = root.Str("units") ?? "USD/oz price levels";
            g.ExpiryNote = root.Str("expiry") ?? "";

            var levels = root.Get("levels");
            if (levels != null && levels.Kind == JsonKind.Array)
            {
                foreach (var lv in levels.Array)
                {
                    var price = lv.Num("price");
                    string kind = lv.Str("kind");
                    if (!price.HasValue || string.IsNullOrEmpty(kind)) continue;
                    if (!Numeric.IsFinite(price.Value)) continue;
                    g.Levels.Add(new LevelRef(price.Value, MapKind(kind), kind));
                }
            }
            if (Numeric.IsFinite(g.GammaFlip)) g.Levels.Add(new LevelRef(g.GammaFlip, LevelKind.GammaFlip, "gamma_flip"));
            if (Numeric.IsFinite(g.CallWall)) g.Levels.Add(new LevelRef(g.CallWall, LevelKind.CallWall, "call_wall"));
            if (Numeric.IsFinite(g.PutWall)) g.Levels.Add(new LevelRef(g.PutWall, LevelKind.PutWall, "put_wall"));

            if (g.Levels.Count == 0 && !Numeric.IsFinite(g.Spot))
            {
                error = "no usable price levels"; g.Error = error; return g;
            }

            // Sanity: price scale must match gold (catches index/point mistakes, e.g. GLD vs XAUUSD).
            double anchor = Numeric.IsFinite(g.Spot) ? g.Spot : double.NaN;
            foreach (var lv in g.Levels)
            {
                if (lv.Price < minPrice || lv.Price > maxPrice)
                {
                    error = "level " + Numeric.F(lv.Price, 2) + " outside sane range [" + minPrice + "," + maxPrice + "]";
                    g.Error = error; return g;
                }
                if (Numeric.IsFinite(anchor) && Math.Abs(lv.Price - anchor) / anchor > 0.5)
                {
                    error = "level " + Numeric.F(lv.Price, 2) + " implausibly far from spot " + Numeric.F(anchor, 2);
                    g.Error = error; return g;
                }
            }
            g.Valid = true;
            return g;
        }

        public static VixSnapshot ParseVix(string json, DateTime receivedUtc, out string error)
        {
            error = null;
            var v = new VixSnapshot { Name = "VIX", ReceivedUtc = receivedUtc };
            if (string.IsNullOrWhiteSpace(json)) { error = "empty"; v.Error = error; return v; }
            JsonValue root;
            try { root = JsonValue.Parse(json); }
            catch (Exception ex) { error = "json:" + ex.Message; v.Error = error; return v; }

            var level = root.Num("vix") ?? root.Num("level") ?? root.Num("close");
            if (!level.HasValue) { error = "missing vix level"; v.Error = error; return v; }
            if (!Numeric.IsFinite(level.Value) || level.Value <= 0 || level.Value > 300) { error = "implausible vix"; v.Error = error; return v; }
            var asOf = ReadTime(root, "as_of");
            if (asOf == null) { error = "missing/invalid as_of"; v.Error = error; return v; }
            if (asOf.Value > receivedUtc.AddMinutes(5)) { error = "as_of in the future"; v.Error = error; return v; }
            v.Level = level.Value;
            v.AsOfUtc = asOf.Value;
            v.Source = root.Str("source") ?? "manual-file";
            string quality = root.Str("quality");
            v.Quality = quality != null && quality.Equals("live", StringComparison.OrdinalIgnoreCase) ? DataQuality.Live : DataQuality.Manual;
            v.Change1D = root.Num("change_1d");
            v.Valid = true;
            return v;
        }

        static DateTime? ReadTime(JsonValue root, string key)
        {
            var n = root.Num(key);
            if (n.HasValue) return Numeric.EpochToUtc(n.Value);
            var s = root.Str(key);
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                return dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
            return null;
        }

        static LevelKind MapKind(string kind)
        {
            switch ((kind ?? "").Trim().ToLowerInvariant())
            {
                case "gamma_flip": case "flip": return LevelKind.GammaFlip;
                case "call_wall": return LevelKind.CallWall;
                case "put_wall": return LevelKind.PutWall;
                default: return LevelKind.GexLevel;
            }
        }

        /// <summary>Freshness policy applied to optional external inputs.</summary>
        public static ExternalAssessment Assess(ExternalInput input, DateTime nowUtc, int maxAgeMinutes, StalePolicy policy, bool required)
        {
            var a = new ExternalAssessment();
            if (input == null || !input.Valid)
            {
                a.Status = input == null ? "absent" : (input.Name + ":invalid (" + input.Error + ")");
                a.BlocksEntry = required || policy == StalePolicy.BlockEntries;
                a.Fresh = false;
                return a;
            }
            a.Fresh = input.IsFresh(nowUtc, maxAgeMinutes);
            a.Status = input.Status(nowUtc, maxAgeMinutes);
            a.BlocksEntry = !a.Fresh && (required || policy == StalePolicy.BlockEntries);
            return a;
        }
    }
}
