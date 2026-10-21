using System;
using System.Collections.Generic;
using System.Globalization;

namespace QuantLOL.Core
{
    /// <summary>Deterministic numeric helpers. No culture-dependent formatting anywhere in the bot.</summary>
    public static class Numeric
    {
        public static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        public static int ClampInt(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        public static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        /// <summary>Rounds a price to the instrument tick grid (away from zero on exact midpoints).</summary>
        public static double RoundToTick(double price, double tickSize)
        {
            if (tickSize <= 0 || !IsFinite(price)) return price;
            return Math.Round(price / tickSize, MidpointRounding.AwayFromZero) * tickSize;
        }

        public static double Ticks(double priceDistance, double tickSize)
            => tickSize <= 0 ? 0 : priceDistance / tickSize;

        public static double PriceFromTicks(double ticks, double tickSize) => ticks * tickSize;

        /// <summary>Linear-interpolated percentile of an unsorted copy (p in [0,1]). Returns NaN for empty input.</summary>
        public static double Percentile(IEnumerable<double> values, double p)
        {
            if (values == null) return double.NaN;
            var a = new List<double>();
            foreach (var v in values) if (IsFinite(v)) a.Add(v);
            if (a.Count == 0) return double.NaN;
            a.Sort();
            if (a.Count == 1) return a[0];
            p = Clamp(p, 0, 1);
            double pos = p * (a.Count - 1);
            int lo = (int)Math.Floor(pos);
            int hi = (int)Math.Ceiling(pos);
            if (lo == hi) return a[lo];
            double w = pos - lo;
            return a[lo] * (1 - w) + a[hi] * w;
        }

        public static double Median(IEnumerable<double> values) => Percentile(values, 0.5);

        public static double Mean(IEnumerable<double> values)
        {
            double s = 0; int n = 0;
            if (values == null) return double.NaN;
            foreach (var v in values) { if (!IsFinite(v)) continue; s += v; n++; }
            return n == 0 ? double.NaN : s / n;
        }

        public static double StdDev(IEnumerable<double> values, bool sample = true)
        {
            var a = new List<double>();
            if (values == null) return double.NaN;
            foreach (var v in values) if (IsFinite(v)) a.Add(v);
            if (a.Count < 2) return double.NaN;
            double m = 0; foreach (var v in a) m += v; m /= a.Count;
            double ss = 0; foreach (var v in a) { double d = v - m; ss += d * d; }
            int denom = sample ? a.Count - 1 : a.Count;
            return Math.Sqrt(ss / denom);
        }

        /// <summary>Population standard deviation of a sorted-free array segment.</summary>
        public static double StdDev(double[] values, int start, int end, bool sample = true)
        {
            int n = end - start;
            if (n < 2) return double.NaN;
            double m = 0; for (int i = start; i < end; i++) m += values[i]; m /= n;
            double ss = 0; for (int i = start; i < end; i++) { double d = values[i] - m; ss += d * d; }
            return Math.Sqrt(ss / (sample ? n - 1 : n));
        }

        public static string F(double v, int decimals)
            => v.ToString("F" + Math.Max(0, decimals).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        public static string Inv(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        public static double ParseDouble(string s, double fallback = double.NaN)
            => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        /// <summary>Safe ratio: returns 0 when denominator is not usable.</summary>
        public static double SafeDiv(double a, double b) => (b == 0 || !IsFinite(b)) ? 0 : a / b;

        /// <summary>Converts an epoch value in seconds or milliseconds to UTC DateTime (heuristic on magnitude).</summary>
        public static DateTime EpochToUtc(double epoch)
        {
            if (epoch > 1e11) epoch /= 1000.0;   // milliseconds
            if (epoch < 0 || epoch > 32503680000.0) return DateTime.MinValue;
            return DateTime.UnixEpoch.AddSeconds(epoch);
        }
    }
}

// build trigger: 2026-10-21T20:06:26Z
