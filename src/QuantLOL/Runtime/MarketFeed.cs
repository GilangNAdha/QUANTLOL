using System;
using System.Diagnostics;
using cAlgo.API;
using cAlgo.API.Internals;
using QuantLOL.Core;

namespace QuantLOL.Runtime
{
    /// <summary>
    /// Owns the closed-bar caches. Bars are copied into pre-allocated arrays so the tick
    /// path never allocates. The forming bar (Bars.Count-1) is never read by any signal
    /// calculation: LastClosedIndex is always Bars.Count-2.
    /// Copies are skipped when no new bar has closed, so an idle tick costs two integer
    /// compares.
    /// </summary>
    public sealed class MarketFeed
    {
        readonly Algo _algo;
        public Bars M1 { get; private set; }
        public Bars M3 { get; private set; }
        public Bars M5 { get; private set; }
        public Bars M15 { get; private set; }
        public Bars D1 { get; private set; }

        public readonly BarDto[] M1Buf;
        public readonly BarDto[] M3Buf;
        public readonly BarDto[] M5Buf;
        public readonly BarDto[] M15Buf;
        public readonly BarDto[] D1Buf;

        public int M1Count, M3Count, M5Count, M15Count, D1Count;
        public DateTime LastM1CloseUtc, LastM3CloseUtc, LastM5CloseUtc, LastM15CloseUtc, LastD1CloseUtc;
        public bool HistoryLoaded { get; private set; }
        public int LoadMoreCalls { get; private set; }
        public string LastError = "";
        public int ContextCount => M15Count;

        int _seenM1 = -1, _seenM3 = -1, _seenM5 = -1, _seenM15 = -1, _seenD1 = -1;

        public MarketFeed(Algo algo, int m1Bars = 3000, int m3Bars = 600, int m5Bars = 600, int m15Bars = 600, int d1Bars = 60)
        {
            _algo = algo;
            M1Buf = new BarDto[Math.Max(60, m1Bars)];
            M3Buf = new BarDto[Math.Max(60, m3Bars)];
            M5Buf = new BarDto[Math.Max(60, m5Bars)];
            M15Buf = new BarDto[Math.Max(60, m15Bars)];
            D1Buf = new BarDto[Math.Max(5, d1Bars)];
        }

        public bool Init()
        {
            try
            {
                M1 = _algo.MarketData.GetBars(TimeFrame.Minute);
                M3 = _algo.MarketData.GetBars(TimeFrame.Minute3);
                M5 = _algo.MarketData.GetBars(TimeFrame.Minute5);
                M15 = _algo.MarketData.GetBars(TimeFrame.Minute15);
                D1 = _algo.MarketData.GetBars(TimeFrame.Daily);
                if (M1 == null || M5 == null || M15 == null)
                {
                    LastError = "MarketData.GetBars returned null for M1/M5/M15";
                    return false;
                }

                // Bounded, time-boxed history warm-up (startup only, never on tick).
                var sw = Stopwatch.StartNew();
                Warmup(M15, M15Buf.Length, 320);
                Warmup(M5, M5Buf.Length, 320);
                Warmup(M3, M3Buf.Length, 200);
                Warmup(M1, M1Buf.Length, 600);
                Warmup(D1, D1Buf.Length, 10);

                RefreshAll(force: true);
                HistoryLoaded = M15Count >= 50 && M5Count >= 50 && M1Count >= 20;
                if (!HistoryLoaded)
                    LastError = "insufficient closed history: m1=" + M1Count + " m3=" + M3Count + " m5=" + M5Count + " m15=" + M15Count;
                return HistoryLoaded;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        void Warmup(Bars bars, int bufferCapacity, int desired)
        {
            if (bars == null) return;
            int target = Math.Min(bufferCapacity, desired);
            for (int i = 0; i < 6; i++)
            {
                if (bars.Count >= target) break;
                int added = bars.LoadMoreHistory();
                LoadMoreCalls++;
                if (added <= 0) break;
            }
        }

        public void RefreshAll(bool force = false) => Refresh(force);

        /// <summary>Refreshes only the series that gained a closed bar (or all when forced).</summary>
        public void Refresh(bool force = false)
        {
            M1Count = MaybeCopy(M1, M1Buf, ref _seenM1, ref LastM1CloseUtc, M1Count, force);
            M3Count = MaybeCopy(M3, M3Buf, ref _seenM3, ref LastM3CloseUtc, M3Count, force);
            M5Count = MaybeCopy(M5, M5Buf, ref _seenM5, ref LastM5CloseUtc, M5Count, force);
            M15Count = MaybeCopy(M15, M15Buf, ref _seenM15, ref LastM15CloseUtc, M15Count, force);
            D1Count = MaybeCopy(D1, D1Buf, ref _seenD1, ref LastD1CloseUtc, D1Count, force);
        }

        /// <summary> Returns the current number of closed bars in the buffer; copies only when a new bar closed. </summary>
        static int MaybeCopy(Bars bars, BarDto[] buffer, ref int seenClosedIndex, ref DateTime lastClosedUtc, int previousCount, bool force)
        {
            if (bars == null) return 0;
            int count = bars.Count;
            if (count < 2) return 0;
            int lastClosed = count - 2;
            if (!force && lastClosed == seenClosedIndex && previousCount > 0) return previousCount;
            seenClosedIndex = lastClosed;
            return CopyClosed(bars, buffer, ref lastClosedUtc);
        }

        static int CopyClosed(Bars bars, BarDto[] buffer, ref DateTime lastClosedUtc)
        {
            int lastClosed = bars.Count - 2;
            int take = Math.Min(buffer.Length, lastClosed + 1);
            int startBar = lastClosed - take + 1;
            for (int k = 0; k < take; k++)
            {
                var b = bars[startBar + k];
                buffer[k] = new BarDto(b.OpenTime.ToUniversalTime(), b.Open, b.High, b.Low, b.Close, b.TickVolume);
            }
            if (take > 0) lastClosedUtc = buffer[take - 1].TimeUtc;
            return take;
        }

        public Bars BarsFor(int minutes)
        {
            switch (minutes)
            {
                case 1: return M1;
                case 3: return M3;
                case 5: return M5;
                case 15: return M15;
                default: return M5;
            }
        }

        public BarDto[] EntryBuffer(int minutes)
        {
            switch (minutes)
            {
                case 1: return M1Buf;
                case 3: return M3Buf;
                case 5: return M5Buf;
                case 15: return M15Buf;
                default: return M5Buf;
            }
        }

        public int EntryCountFor(int minutes)
        {
            switch (minutes)
            {
                case 1: return M1Count;
                case 3: return M3Count;
                case 5: return M5Count;
                case 15: return M15Count;
                default: return M5Count;
            }
        }
    }
}
