using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using QuantLOL.Core;

namespace QuantLOL.Runtime
{
    /// <summary>
    /// Buffered CSV journal. Never performs IO inside the tick path: records are queued and
    /// flushed by the timer / on stop. All IO is guarded; a failure disables journalling
    /// with a single logged message instead of throwing into the trading loop.
    /// </summary>
    public sealed class Journal
    {
        readonly List<TradeRecord> _trades = new List<TradeRecord>(128);
        readonly List<RejectedSetupRecord> _rejects = new List<RejectedSetupRecord>(512);
        readonly object _lock = new object();
        public bool Enabled;
        public string Directory = ".";
        public string Prefix = "quantlol";
        public string LastError = "";
        public bool DisabledByError;
        public long TradesWritten { get; private set; }
        public long RejectsWritten { get; private set; }

        public IReadOnlyList<TradeRecord> Trades { get { lock (_lock) return _trades.ToArray(); } }
        public IReadOnlyList<RejectedSetupRecord> Rejects { get { lock (_lock) return _rejects.ToArray(); } }

        public void AddTrade(TradeRecord t) { lock (_lock) _trades.Add(t); }
        public void AddReject(RejectedSetupRecord r)
        {
            lock (_lock)
            {
                if (_rejects.Count < 50000) _rejects.Add(r);
            }
        }

        public string TradesPath => Path.Combine(Directory, Prefix + "_trades.csv");
        public string RejectsPath => Path.Combine(Directory, Prefix + "_rejections.csv");

        public void Flush()
        {
            if (!Enabled || DisabledByError) return;
            try
            {
                Directory = string.IsNullOrWhiteSpace(Directory) ? "." : Directory;
                if (!System.IO.Directory.Exists(Directory)) System.IO.Directory.CreateDirectory(Directory);
                WriteTrades();
                WriteRejects();
            }
            catch (Exception ex)
            {
                DisabledByError = true;
                LastError = ex.GetType().Name + ": " + ex.Message;
            }
        }

        void WriteTrades()
        {
            TradeRecord[] snapshot;
            lock (_lock) snapshot = _trades.ToArray();
            if (snapshot.Length == 0) return;
            bool exists = File.Exists(TradesPath);
            var sb = new StringBuilder();
            if (!exists) sb.AppendLine(TradeCsv.Header);
            foreach (var t in snapshot) sb.AppendLine(TradeCsv.Line(t));
            File.AppendAllText(TradesPath, sb.ToString(), Encoding.UTF8);
            lock (_lock) _trades.Clear();
            TradesWritten += snapshot.Length;
        }

        void WriteRejects()
        {
            RejectedSetupRecord[] snapshot;
            lock (_lock) snapshot = _rejects.ToArray();
            if (snapshot.Length == 0) return;
            bool exists = File.Exists(RejectsPath);
            var sb = new StringBuilder();
            if (!exists) sb.AppendLine("time_utc,reason,direction,price,planned_risk_usd,session,score,detail");
            foreach (var r in snapshot)
                sb.AppendLine(string.Join(",", new[]
                {
                    r.TimeUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    Csv(r.Reason.ToString()), Csv(r.Direction), Num(r.Price), Num(r.PlannedRiskUsd),
                    Csv(r.Session), Num(r.Score), Csv(r.Detail)
                }));
            File.AppendAllText(RejectsPath, sb.ToString(), Encoding.UTF8);
            lock (_lock) _rejects.Clear();
            RejectsWritten += snapshot.Length;
        }

        static string Num(double v) => double.IsNaN(v) ? "" : v.ToString("0.#####", CultureInfo.InvariantCulture);

        public static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            bool needs = s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            string esc = s.Replace("\"", "\"\"");
            return needs ? "\"" + esc + "\"" : esc;
        }
    }

    public static class TradeCsv
    {
        public const string Header =
            "id,label,direction,setup,session,regime,exit_reason,signal_time_utc,entry_time_utc,exit_time_utc," +
            "entry_price,stop_price,target_price,exit_price,volume_units,gross_pnl,commission,swap,net_pnl,risk_usd,r_multiple," +
            "mae_usd,mfe_usd,mae_ticks,mfe_ticks,spread_entry_ticks,spread_exit_ticks,slippage_usd,score,external_status,filters," +
            "latency_decision_ms,latency_ack_ms,latency_fill_ms,bars_held,synthetic_target,note";

        public static string Line(TradeRecord t)
        {
            return string.Join(",", new[]
            {
                t.Id.ToString(CultureInfo.InvariantCulture), Journal.Csv(t.Label), Journal.Csv(t.Direction), Journal.Csv(t.SetupType),
                Journal.Csv(t.Session), Journal.Csv(t.Regime), Journal.Csv(t.ExitReason),
                t.SignalTimeUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                t.EntryTimeUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                t.ExitTimeUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                F(t.EntryPrice), F(t.StopPrice), F(t.TargetPrice), F(t.ExitPrice), F(t.VolumeUnits),
                F(t.GrossPnl), F(t.CommissionUsd), F(t.SwapUsd), F(t.NetPnl), F(t.RiskUsd), F(t.RMultiple),
                F(t.MaeUsd), F(t.MfeUsd), F(t.MaeTicks), F(t.MfeTicks), F(t.SpreadEntryTicks), F(t.SpreadExitTicks),
                F(t.SlippageUsd), F(t.Score), Journal.Csv(t.ExternalStatus), Journal.Csv(t.Filters),
                F(t.LatencyDecisionMs), F(t.LatencyAckMs), F(t.LatencyFillMs), t.BarsHeld.ToString(CultureInfo.InvariantCulture),
                t.SyntheticTarget ? "1" : "0", Journal.Csv(t.Note)
            });
        }

        static string F(double v) => double.IsNaN(v) ? "" : v.ToString("0.#####", CultureInfo.InvariantCulture);
    }
}
