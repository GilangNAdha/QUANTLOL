using System;
using System.Collections.Generic;
using QuantLOL.Core;

namespace QuantLOL.Tests
{
    public static class TestData
    {
        /// <summary>Builds bars from (high, low, close) triples; open = previous close (or high-low mid for the first).</summary>
        public static BarDto[] Bars(double[][] hlc, DateTime? start = null, double volume = 100)
        {
            var t = start ?? new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
            var bars = new BarDto[hlc.Length];
            double prevClose = (hlc[0][0] + hlc[0][1]) / 2;
            for (int i = 0; i < hlc.Length; i++)
            {
                double h = hlc[i][0], l = hlc[i][1], c = hlc[i][2];
                bars[i] = new BarDto(t.AddMinutes(5 * i), prevClose, h, l, c, volume);
                prevClose = c;
            }
            return bars;
        }

        public static BarDto[] Flat(int count, double price, double amplitude = 0.2, double volume = 100)
        {
            var a = new double[count][];
            for (int i = 0; i < count; i++)
            {
                double h = price + (i % 2 == 0 ? amplitude : amplitude * 0.5);
                double l = price - (i % 2 == 0 ? amplitude : amplitude * 0.5);
                a[i] = new[] { h, l, price };
            }
            return Bars(a, volume: volume);
        }

        public static double[] Sum(IEnumerable<double> xs)
        {
            var l = new List<double>(xs);
            double s = 0; foreach (var x in l) s += x;
            return new[] { s, l.Count };
        }
    }

    public static class AssertEx
    {
        public static void True(bool cond, string message)
        {
            if (!cond) throw new Exception("expected true: " + message);
        }

        public static void False(bool cond, string message) => True(!cond, message);

        public static void Near(double expected, double actual, double tol, string message)
        {
            if (double.IsNaN(actual) || Math.Abs(expected - actual) > tol)
                throw new Exception($"{message}: expected {expected} +/- {tol}, got {actual}");
        }

        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception($"{message}: expected {expected}, got {actual}");
        }
    }
}
