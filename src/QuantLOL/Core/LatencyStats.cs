using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace QuantLOL.Core
{
    /// <summary>
    /// Monotonic, allocation-free-at-record latency measurement.
    /// Uses Stopwatch timestamps (high-resolution monotonic clock), never wall-clock
    /// differences, so measurements cannot be distorted by clock adjustments.
    /// </summary>
    public static class MonotonicClock
    {
        public static long Timestamp => Stopwatch.GetTimestamp();
        public static double Frequency => Stopwatch.Frequency;
        public static double MsSince(long startTimestamp)
            => (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        public static double MsBetween(long startTimestamp, long endTimestamp)
            => (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        public static double UsBetween(long a, long b) => (b - a) * 1_000_000.0 / Stopwatch.Frequency;
    }

    public sealed class LatencySummary
    {
        public string Name = "";
        public int Count;
        public double P50Ms, P95Ms, P99Ms, MaxMs, MeanMs, MinMs;

        public string Row()
            => Name.PadRight(28) + " n=" + Count.ToString().PadLeft(6) +
               "  p50=" + Numeric.F(P50Ms, 3).PadLeft(9) +
               "  p95=" + Numeric.F(P95Ms, 3).PadLeft(9) +
               "  p99=" + Numeric.F(P99Ms, 3).PadLeft(9) +
               "  max=" + Numeric.F(MaxMs, 3).PadLeft(9) +
               "  mean=" + Numeric.F(MeanMs, 3).PadLeft(9);
    }

    /// <summary>Fixed-capacity ring buffer of samples (microseconds). No unbounded growth.</summary>
    public sealed class LatencySeries
    {
        readonly double[] _ms;
        int _count, _next;
        public string Name { get; }
        public bool Enabled = true;

        public LatencySeries(string name, int capacity = 4096)
        {
            Name = name;
            _ms = new double[Math.Max(16, capacity)];
        }

        public int Count => _count;

        public void RecordMs(double ms)
        {
            if (!Enabled || double.IsNaN(ms)) return;
            _ms[_next] = ms;
            _next = (_next + 1) % _ms.Length;
            if (_count < _ms.Length) _count++;
        }

        public void Record(long startTs, long endTs) => RecordMs(MonotonicClock.MsBetween(startTs, endTs));

        public LatencySummary Snapshot()
        {
            var s = new LatencySummary { Name = Name, Count = _count };
            if (_count == 0) return s;
            var copy = new double[_count];
            Array.Copy(_ms, copy, _count);
            Array.Sort(copy);
            s.MinMs = copy[0];
            s.MaxMs = copy[copy.Length - 1];
            s.P50Ms = Quantile(copy, 0.50);
            s.P95Ms = Quantile(copy, 0.95);
            s.P99Ms = Quantile(copy, 0.99);
            double sum = 0; foreach (var v in copy) sum += v;
            s.MeanMs = sum / copy.Length;
            return s;
        }

        static double Quantile(double[] sorted, double p)
        {
            if (sorted.Length == 1) return sorted[0];
            double pos = p * (sorted.Length - 1);
            int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
            if (lo == hi) return sorted[lo];
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
        }
    }

    /// <summary>Named stage timers sharing one trace id, for a single decision.</summary>
    public sealed class LatencyTrace
    {
        public long T0, T1, T2, T3, T4, T5;
        public string Key = "";
        public bool Completed;

        public void Reset() { T0 = T1 = T2 = T3 = T4 = T5 = 0; Key = ""; Completed = false; }
    }

    public sealed class LatencyRegistry
    {
        public readonly LatencySeries TickToSignal = new LatencySeries("tick->signal_eval");
        public readonly LatencySeries SignalToRisk = new LatencySeries("signal->risk_approval");
        public readonly LatencySeries RiskToSubmit = new LatencySeries("risk->order_submit");
        public readonly LatencySeries SubmitToAck = new LatencySeries("submit->broker_ack");
        public readonly LatencySeries SubmitToFill = new LatencySeries("submit->confirmed_fill");
        public readonly LatencySeries DecisionToFill = new LatencySeries("decision->fill_total");
        public readonly LatencySeries SlowPathTotal = new LatencySeries("slow_path_total");
        public readonly LatencySeries OnTickTotal = new LatencySeries("on_tick_total");
        public readonly LatencySeries ExternalRefresh = new LatencySeries("external_data_refresh");

        public IEnumerable<LatencySeries> All => new[]
        {
            TickToSignal, SignalToRisk, RiskToSubmit, SubmitToAck, SubmitToFill,
            DecisionToFill, SlowPathTotal, OnTickTotal, ExternalRefresh
        };

        public string RenderTable()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Latency (monotonic Stopwatch, milliseconds; local processing only, excludes network/broker server time except ack/fill rows)");
            int n = 0;
            foreach (var s in All)
            {
                var snap = s.Snapshot();
                if (snap.Count == 0) continue;
                sb.AppendLine("  " + snap.Row());
                n++;
            }
            if (n == 0) sb.AppendLine("  no samples collected");
            else
            {
                sb.AppendLine("  NOTE: submit->broker_ack and submit->confirmed_fill include broker/network round-trip and");
                sb.AppendLine("        server-side processing; they are NOT internal computation time.");
            }
            return sb.ToString();
        }
    }
}
