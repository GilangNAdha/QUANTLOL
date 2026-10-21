using System;
using System.Collections.Generic;
using System.Text;

namespace QuantLOL.Core
{
    public sealed class TradeRecord
    {
        public int Id;
        public string Label = "";
        public string Direction = "";        // LONG / SHORT
        public string SetupType = "";
        public string Session = "";
        public string Regime = "";
        public string ExitReason = "";
        public DateTime SignalTimeUtc, EntryTimeUtc, ExitTimeUtc;
        public double EntryPrice, StopPrice, TargetPrice, ExitPrice;
        public double VolumeUnits;
        public double GrossPnl, CommissionUsd, SwapUsd, NetPnl;
        public double RiskUsd;
        public double RMultiple;
        public double MaeUsd, MfeUsd, MaeTicks, MfeTicks;
        public double SpreadEntryTicks, SpreadExitTicks, SlippageUsd;
        public double Score;
        public string ExternalStatus = "";
        public string Filters = "";
        public double LatencyDecisionMs, LatencyAckMs, LatencyFillMs;
        public int BarsHeld;
        public bool SyntheticTarget;
        public string Note = "";
    }

    public sealed class RejectedSetupRecord
    {
        public DateTime TimeUtc;
        public RejectReason Reason;
        public string Detail = "";
        public string Direction = "";
        public double Price;
        public double PlannedRiskUsd;
        public string Session = "";
        public double Score;
    }

    public sealed class BucketStats
    {
        public string Key = "";
        public int Count;
        public double Net, WinRate, Expectancy;
        public override string ToString() => $"{Key}: n={Count} net={Net:0.00} win={WinRate:0.0}% exp={Expectancy:0.000}";
    }

    public sealed class PerformanceMetrics
    {
        public int TradeCount;
        public double NetProfit, GrossProfit, GrossLoss;
        public double ProfitFactor, Expectancy, WinRate, AvgWin, AvgLoss, PayoffRatio;
        public double MaxDrawdown, RecoveryFactor;
        public double Sharpe, Sortino;
        public int TradingDays;
        public double AvgHoldingMinutes, MedianHoldingMinutes;
        public double TotalCommission, TotalSwap, TotalSpreadCost, TotalSlippage;
        public double AvgMaeUsd, AvgMfeUsd, P95MaeUsd, P95MfeUsd;
        public double AvgMaeTicks, AvgMfeTicks;
        public double LatencyP50, LatencyP95, LatencyP99;
        public double TradesPerDay;
        public double ExpectancyCiLow, ExpectancyCiHigh;
        public double ProfitFactorCiLow, ProfitFactorCiHigh;
        public bool BootstrapAvailable;
        public readonly List<BucketStats> BySession = new List<BucketStats>();
        public readonly List<BucketStats> BySetup = new List<BucketStats>();
        public readonly List<BucketStats> ByDirection = new List<BucketStats>();
        public readonly List<BucketStats> ByRegime = new List<BucketStats>();
        public readonly List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// Performance measurement. Deliberately reports sample sizes and bootstrap confidence
    /// intervals instead of point estimates alone, and never annualises metrics that the
    /// sample cannot support.
    /// </summary>
    public static class PerformanceAnalyzer
    {
        public static PerformanceMetrics Compute(IReadOnlyList<TradeRecord> trades)
        {
            var m = new PerformanceMetrics();
            if (trades == null || trades.Count == 0)
            {
                m.Warnings.Add("no closed trades: performance metrics are undefined");
                return m;
            }
            m.TradeCount = trades.Count;
            var pnls = new double[trades.Count];
            var dates = new List<DateTime>();
            double winSum = 0, lossSum = 0, maeSum = 0, mfeSum = 0, holdSum = 0;
            var holds = new List<double>();
            var maes = new List<double>();
            var mfes = new List<double>();
            var lat = new List<double>();
            var byDate = new Dictionary<DateTime, double>();
            int wins = 0;

            for (int i = 0; i < trades.Count; i++)
            {
                var t = trades[i];
                pnls[i] = t.NetPnl;
                m.NetProfit += t.NetPnl;
                if (t.NetPnl > 0) { wins++; winSum += t.NetPnl; }
                else lossSum += t.NetPnl;
                m.TotalCommission += t.CommissionUsd;
                m.TotalSwap += t.SwapUsd;
                m.TotalSlippage += t.SlippageUsd;
                m.TotalSpreadCost += t.SpreadEntryTicks;
                maeSum += Math.Abs(t.MaeUsd); mfeSum += Math.Abs(t.MfeUsd);
                maes.Add(Math.Abs(t.MaeUsd)); mfes.Add(Math.Abs(t.MfeUsd));
                double hold = t.ExitTimeUtc > t.EntryTimeUtc ? (t.ExitTimeUtc - t.EntryTimeUtc).TotalMinutes : 0;
                holds.Add(hold); holdSum += hold;
                if (t.LatencyDecisionMs > 0) lat.Add(t.LatencyDecisionMs);
                var d = t.ExitTimeUtc.Date;
                if (byDate.TryGetValue(d, out var cur)) byDate[d] = cur + t.NetPnl; else byDate[d] = t.NetPnl;
            }
            m.GrossProfit = winSum;
            m.GrossLoss = lossSum;
            m.ProfitFactor = lossSum < 0 ? winSum / Math.Abs(lossSum) : (winSum > 0 ? double.PositiveInfinity : 0);
            m.Expectancy = m.NetProfit / m.TradeCount;
            m.WinRate = 100.0 * wins / m.TradeCount;
            m.AvgWin = wins > 0 ? winSum / wins : 0;
            int losses = m.TradeCount - wins;
            m.AvgLoss = losses > 0 ? lossSum / losses : 0;   // negative
            m.PayoffRatio = m.AvgLoss < 0 ? m.AvgWin / Math.Abs(m.AvgLoss) : double.NaN;
            m.AvgMaeUsd = maeSum / m.TradeCount; m.AvgMfeUsd = mfeSum / m.TradeCount;
            m.P95MaeUsd = Numeric.Percentile(maes, 0.95); m.P95MfeUsd = Numeric.Percentile(mfes, 0.95);
            m.AvgHoldingMinutes = holdSum / m.TradeCount;
            m.MedianHoldingMinutes = Numeric.Median(holds);

            // Drawdown on the closed-trade equity curve.
            double cum = 0, peak = 0, maxDd = 0;
            foreach (var p in pnls) { cum += p; if (cum > peak) peak = cum; double dd = peak - cum; if (dd > maxDd) maxDd = dd; }
            m.MaxDrawdown = maxDd;
            m.RecoveryFactor = maxDd > 0 ? m.NetProfit / maxDd : double.NaN;

            // Daily returns -> risk-adjusted ratios (only with enough days).
            var daily = new List<double>();
            foreach (var kv in byDate) daily.Add(kv.Value);
            m.TradingDays = daily.Count;
            m.TradesPerDay = daily.Count > 0 ? (double)m.TradeCount / daily.Count : double.NaN;
            if (daily.Count >= 20)
            {
                var rets = new double[daily.Count];
                double startEquity = 1000.0; // ratios are scale free, but documented assumption
                double eq = startEquity;
                for (int i = 0; i < daily.Count; i++) { rets[i] = daily[i] / eq; eq += daily[i]; }
                double mean = Numeric.Mean(rets), sd = Numeric.StdDev(rets);
                m.Sharpe = sd > 0 ? mean / sd * Math.Sqrt(252) : double.NaN;
                var downside = new List<double>();
                foreach (var r in rets) if (r < 0) downside.Add(r * r);
                double dd = downside.Count > 0 ? Math.Sqrt(Numeric.Mean(downside)) : 0;
                m.Sortino = dd > 0 ? mean / dd * Math.Sqrt(252) : double.NaN;
                if (daily.Count < 60) m.Warnings.Add("Sharpe/Sortino computed from " + daily.Count + " days; treat as indicative only");
            }
            else m.Warnings.Add("fewer than 20 trading days: Sharpe/Sortino not reported");

            m.LatencyP50 = Numeric.Percentile(lat, 0.50);
            m.LatencyP95 = Numeric.Percentile(lat, 0.95);
            m.LatencyP99 = Numeric.Percentile(lat, 0.99);

            BuildBuckets(trades, m.BySession, t => t.Session);
            BuildBuckets(trades, m.BySetup, t => t.SetupType);
            BuildBuckets(trades, m.ByDirection, t => t.Direction);
            BuildBuckets(trades, m.ByRegime, t => t.Regime);

            Bootstrap(pnls, out m.ExpectancyCiLow, out m.ExpectancyCiHigh, out m.ProfitFactorCiLow, out m.ProfitFactorCiHigh);
            m.BootstrapAvailable = true;
            if (m.TradeCount < 30) m.Warnings.Add("sample size " + m.TradeCount + " < 30: confidence intervals are wide, do not infer an edge");
            return m;
        }

        static void BuildBuckets(IReadOnlyList<TradeRecord> trades, List<BucketStats> target, Func<TradeRecord, string> key)
        {
            var map = new Dictionary<string, List<double>>();
            foreach (var t in trades)
            {
                string k = key(t) ?? "n/a";
                if (!map.TryGetValue(k, out var l)) { l = new List<double>(); map[k] = l; }
                l.Add(t.NetPnl);
            }
            foreach (var kv in map)
            {
                var b = new BucketStats { Key = kv.Key, Count = kv.Value.Count };
                int w = 0; double net = 0;
                foreach (var p in kv.Value) { net += p; if (p > 0) w++; }
                b.Net = net; b.WinRate = 100.0 * w / kv.Value.Count; b.Expectancy = net / kv.Value.Count;
                target.Add(b);
            }
            target.Sort((a, b) => b.Count.CompareTo(a.Count));
        }

        /// <summary>Deterministic bootstrap (fixed seed) of expectancy and profit factor.</summary>
        static void Bootstrap(double[] pnls, out double eLo, out double eHi, out double pfLo, out double pfHi,
                              int iterations = 2000, int seed = 12345)
        {
            eLo = eHi = pfLo = pfHi = double.NaN;
            if (pnls == null || pnls.Length < 5) return;
            var exps = new double[iterations];
            var pfs = new List<double>(iterations);
            var rng = new Random(seed);
            int n = pnls.Length;
            for (int it = 0; it < iterations; it++)
            {
                double sum = 0, gw = 0, gl = 0;
                for (int i = 0; i < n; i++)
                {
                    double p = pnls[rng.Next(n)];
                    sum += p;
                    if (p > 0) gw += p; else gl += -p;
                }
                exps[it] = sum / n;
                if (gl > 0) pfs.Add(gw / gl);
            }
            Array.Sort(exps);
            eLo = exps[(int)(iterations * 0.025)];
            eHi = exps[(int)(iterations * 0.975) - 1];
            pfs.Sort();
            if (pfs.Count > 10) { pfLo = pfs[(int)(pfs.Count * 0.025)]; pfHi = pfs[(int)(pfs.Count * 0.975) - 1]; }
        }

        public static string RenderMarkdown(PerformanceMetrics m, string title, string provenance)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## " + title);
            sb.AppendLine();
            if (!string.IsNullOrEmpty(provenance)) { sb.AppendLine("> " + provenance); sb.AppendLine(); }
            sb.AppendLine("| Metric | Value |");
            sb.AppendLine("|---|---|");
            Row(sb, "Closed trades", m.TradeCount.ToString());
            Row(sb, "Net profit (USD)", Numeric.F(m.NetProfit, 2));
            Row(sb, "Profit factor", Numeric.IsFinite(m.ProfitFactor) ? Numeric.F(m.ProfitFactor, 2) : "n/a");
            Row(sb, "PF 95% bootstrap CI", Numeric.IsFinite(m.ProfitFactorCiLow) ? Numeric.F(m.ProfitFactorCiLow, 2) + " - " + Numeric.F(m.ProfitFactorCiHigh, 2) : "n/a");
            Row(sb, "Expectancy / trade (USD)", Numeric.F(m.Expectancy, 3));
            Row(sb, "Expectancy 95% CI", Numeric.IsFinite(m.ExpectancyCiLow) ? Numeric.F(m.ExpectancyCiLow, 3) + " - " + Numeric.F(m.ExpectancyCiHigh, 3) : "n/a");
            Row(sb, "Win rate", Numeric.F(m.WinRate, 1) + "%");
            Row(sb, "Average win / loss", Numeric.F(m.AvgWin, 2) + " / " + Numeric.F(m.AvgLoss, 2));
            Row(sb, "Max closed-trade drawdown", Numeric.F(m.MaxDrawdown, 2));
            Row(sb, "Recovery factor", Numeric.IsFinite(m.RecoveryFactor) ? Numeric.F(m.RecoveryFactor, 2) : "n/a");
            Row(sb, "Sharpe (daily, ann.)", Numeric.IsFinite(m.Sharpe) ? Numeric.F(m.Sharpe, 2) : "n/a");
            Row(sb, "Sortino (daily, ann.)", Numeric.IsFinite(m.Sortino) ? Numeric.F(m.Sortino, 2) : "n/a");
            Row(sb, "Holding time avg / median (min)", Numeric.F(m.AvgHoldingMinutes, 0) + " / " + Numeric.F(m.MedianHoldingMinutes, 0));
            Row(sb, "Commission total", Numeric.F(m.TotalCommission, 2));
            Row(sb, "Swap total", Numeric.F(m.TotalSwap, 2));
            Row(sb, "Modeled slippage total", Numeric.F(m.TotalSlippage, 2));
            Row(sb, "Avg MAE / MFE (USD)", Numeric.F(m.AvgMaeUsd, 2) + " / " + Numeric.F(m.AvgMfeUsd, 2));
            Row(sb, "P95 MAE / MFE (USD)", Numeric.F(m.P95MaeUsd, 2) + " / " + Numeric.F(m.P95MfeUsd, 2));
            Row(sb, "Decision latency p50/p95/p99 (ms)", Numeric.F(m.LatencyP50, 3) + " / " + Numeric.F(m.LatencyP95, 3) + " / " + Numeric.F(m.LatencyP99, 3));
            Row(sb, "Trades per trading day", Numeric.IsFinite(m.TradesPerDay) ? Numeric.F(m.TradesPerDay, 2) : "n/a");
            sb.AppendLine();
            AppendBuckets(sb, "By session", m.BySession);
            AppendBuckets(sb, "By setup type", m.BySetup);
            AppendBuckets(sb, "By direction", m.ByDirection);
            AppendBuckets(sb, "By regime", m.ByRegime);
            if (m.Warnings.Count > 0)
            {
                sb.AppendLine("**Caveats**");
                foreach (var w in m.Warnings) sb.AppendLine("- " + w);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        static void Row(StringBuilder sb, string k, string v) => sb.AppendLine("| " + k + " | " + v + " |");

        static void AppendBuckets(StringBuilder sb, string title, List<BucketStats> b)
        {
            if (b.Count == 0) return;
            sb.AppendLine("**" + title + "**");
            sb.AppendLine();
            sb.AppendLine("| Bucket | n | Net (USD) | Win rate | Expectancy |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var x in b)
                sb.AppendLine("| " + x.Key + " | " + x.Count + " | " + Numeric.F(x.Net, 2) + " | " + Numeric.F(x.WinRate, 1) + "% | " + Numeric.F(x.Expectancy, 3) + " |");
            sb.AppendLine();
        }
    }
}
