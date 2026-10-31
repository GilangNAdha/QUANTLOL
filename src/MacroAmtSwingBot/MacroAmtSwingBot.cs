// =====================================================================================================
//  MacroAmtSwingBot  —  cBot swing trading: bias makro + COT + volume profile (auction market theory)
//                       + tiga model entry berbasis aturan.
//
//  Catatan desain:
//   * Satu file, dua bagian. Bagian "pure core" (enum + kelas statis di bawah) TIDAK memakai
//     cAlgo.API sama sekali, jadi bisa diuji unit test (compile dengan define MACROAMT_CORE_ONLY).
//     Bagian robot ([Robot]) ada di dalam #if !MACROAMT_CORE_ONLY.
//   * Semua sinyal dihitung dari bar yang SUDAH CLOSE (Bars.Last(1) dan sebelumnya). Tidak ada
//     look-ahead / repainting.
//   * Volume profile hanya dihitung ulang saat bar D1 baru dibuka, hasilnya di-cache.
//   * Tidak ada janji profit. Bot ini alat eksekusi disiplin, bukan prediktor.
// =====================================================================================================

#if !MACROAMT_CORE_ONLY
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
using System.IO;
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace cAlgo.Robots
{
    // =================================================================================================
    //  1. ENUM PARAMETER (di luar #if supaya test bisa melihatnya)
    // =================================================================================================

    public enum MarketPreset
    {
        Gold = 0,            // XAUUSD: model 1 & 2 saat trend, model 3 hanya saat bias netral
        Forex = 1,           // pasangan FX: model 3 saat range, 1 & 2 saat trend, correlation guard
        Index = 2,           // indeks: LONG ONLY + pullback minimal dari high 20 hari + filter VIX opsional
        VixExperimental = 3  // preset VIX: mati secara default (butuh input AllowVixPreset = true)
    }

    public enum BiasDirection { Long = 0, Short = 1, Neutral = 2 }

    public enum BiasSourceMode { Manual = 0, AutoProxy = 1 }

    public enum ConvictionLevel { Low = 1, Medium = 2, High = 3 }

    public enum RegimeOverrideMode { Auto = 0, ForceTrend = 1, ForceRange = 2 }

    public enum ProfileTimeframeOption { M30 = 30, H1 = 60, H4 = 240 }

    public enum EntryTimeframeOption { H4 = 240, D1 = 1440 }

    public enum BinSizeMode { Auto = 0, Fixed = 1 }

    public enum CotMode { Off = 0, Soft = 1, Hard = 2 }

    public enum StaleCotBehavior { Ignore = 0, Block = 1 }

    public enum TrailingMode { None = 0, AtrTrail = 1 }

    public enum LogVerbosity { Signals = 0, Normal = 1, Verbose = 2 }

    public enum MigrationDirection { Up = 0, Down = 1, Overlap = 2 }

    public enum MarketRegime { Unknown = 0, TrendUp = 1, TrendDown = 2, Range = 3 }

    public enum TradeSide { Long = 0, Short = 1 }

    public enum SignalModel { FailedAuction = 1, BreakoutRetest = 2, MeanReversion = 3 }

    // =================================================================================================
    //  2. PURE CORE — tanpa cAlgo.API (bisa ditest)
    // =================================================================================================

    /// <summary>Formating angka yang konsisten untuk log (selalu invariant culture).</summary>
    public static class Fmt
    {
        public static string N(double v, int digits)
        {
            if (double.IsNaN(v)) return "NaN";
            if (double.IsInfinity(v)) return v > 0 ? "+Inf" : "-Inf";
            return v.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }

        public static string Pct(double v, int digits) { return N(v, digits) + "%"; }
    }

    /// <summary>Satu bar yang sudah close, dipakai oleh seluruh kalkulasi profil dan sinyal.</summary>
    public readonly struct ProfileBar
    {
        public readonly DateTime TimeUtc;
        public readonly double Open;
        public readonly double High;
        public readonly double Low;
        public readonly double Close;
        public readonly double Volume;

        public ProfileBar(DateTime timeUtc, double open, double high, double low, double close, double volume)
        {
            TimeUtc = timeUtc; Open = open; High = high; Low = low; Close = close; Volume = volume;
        }

        public bool IsValid
        {
            get { return High >= Low && High > 0 && Low > 0 && !double.IsNaN(Close) && Volume >= 0; }
        }
    }

    /// <summary>
    /// Histogram volume per bin harga. Grid bin: bin ke-i mencakup [(BaseBin+i)*BinSize, (BaseBin+i+1)*BinSize).
    /// </summary>
    public sealed class ProfileHistogram
    {
        public readonly double BinSize;
        public readonly long BaseBin;
        public readonly double[] Bins;
        public readonly double TotalVolume;
        public readonly double LowPrice;
        public readonly double HighPrice;

        public ProfileHistogram(double binSize, long baseBin, double[] bins, double totalVolume, double lowPrice, double highPrice)
        {
            BinSize = binSize; BaseBin = baseBin; Bins = bins; TotalVolume = totalVolume;
            LowPrice = lowPrice; HighPrice = highPrice;
        }

        public int Count { get { return Bins == null ? 0 : Bins.Length; } }
        public double BinLow(int i) { return (BaseBin + i) * BinSize; }
        public double BinHigh(int i) { return (BaseBin + i + 1) * BinSize; }
        public double BinCentre(int i) { return (BaseBin + i + 0.5) * BinSize; }
    }

    /// <summary>Hasil sebuah profil: POC, VAH, VAL dan metrik pendukung.</summary>
    public sealed class ValueProfile
    {
        public double Poc;
        public double Vah;
        public double Val;
        public double TotalVolume;
        public double BinSize;
        public double RangeLow;
        public double RangeHigh;
        public int PocBin;
        public int BinCount;
        public double ValueAreaPercent;

        public double VaWidth { get { return Vah - Val; } }
        public double VaMid { get { return 0.5 * (Vah + Val); } }
        public double LowerThirdTop { get { return Val + (Vah - Val) / 3.0; } }
        public double UpperThirdBottom { get { return Vah - (Vah - Val) / 3.0; } }

        public bool Contains(double price) { return price >= Val && price <= Vah; }
        public bool IsValid { get { return Vah > Val && Poc > 0 && TotalVolume > 0; } }

        public string Short()
        {
            return "POC " + Fmt.N(Poc, 5) + " VAH " + Fmt.N(Vah, 5) + " VAL " + Fmt.N(Val, 5);
        }
    }

    /// <summary>Pembangun volume profile murni (tanpa broker).</summary>
    public static class VolumeProfile
    {
        public const int HardMaxBins = 20000;

        /// <summary>
        /// Distribusi volume tiap bar secara merata ke semua bin antara Low dan High.
        /// Mengembalikan false + error yang bisa dibaca kalau data tidak layak.
        /// </summary>
        public static bool TryBuild(IReadOnlyList<ProfileBar> bars, double binSize, int maxBins,
                                    out ProfileHistogram hist, out string error)
        {
            hist = null; error = null;
            if (bars == null || bars.Count == 0) { error = "tidak ada bar"; return false; }
            if (!(binSize > 0)) { error = "bin size harus > 0"; return false; }
            if (maxBins <= 0) maxBins = HardMaxBins;
            if (maxBins > HardMaxBins) maxBins = HardMaxBins;

            double low = double.MaxValue, high = double.MinValue, total = 0;
            for (int i = 0; i < bars.Count; i++)
            {
                var b = bars[i];
                if (!b.IsValid || b.Volume <= 0) continue;
                if (b.Low < low) low = b.Low;
                if (b.High > high) high = b.High;
                total += b.Volume;
            }
            if (!(total > 0) || low > high) { error = "tidak ada volume valid"; return false; }

            long loBin = (long)Math.Floor(low / binSize);
            long hiBin = (long)Math.Floor(high / binSize);
            long n = hiBin - loBin + 1;
            if (n <= 0) { error = "rentang harga tidak valid"; return false; }
            if (n > maxBins) { error = "bin terlalu banyak (" + n + " > " + maxBins + "), perbesar bin"; return false; }

            var bins = new double[(int)n];
            for (int i = 0; i < bars.Count; i++)
            {
                var b = bars[i];
                if (!b.IsValid || b.Volume <= 0) continue;
                long a = (long)Math.Floor(b.Low / binSize);
                long z = (long)Math.Floor(b.High / binSize);
                if (z < a) { long t = a; a = z; z = t; }
                long width = z - a + 1;
                if (width <= 0) continue;
                double per = b.Volume / width;
                for (long k = a; k <= z; k++)
                {
                    int idx = (int)(k - loBin);
                    if (idx >= 0 && idx < bins.Length) bins[idx] += per;
                }
            }

            hist = new ProfileHistogram(binSize, loBin, bins, total, low, high);
            return true;
        }

        /// <summary>Gabungkan beberapa histogram harian menjadi satu histogram komposit.</summary>
        public static bool TryMerge(IReadOnlyList<ProfileHistogram> parts, int maxBins,
                                    out ProfileHistogram merged, out string error)
        {
            merged = null; error = null;
            if (parts == null || parts.Count == 0) { error = "tidak ada histogram"; return false; }
            if (maxBins <= 0) maxBins = HardMaxBins;

            double binSize = 0;
            long loBin = long.MaxValue, hiBin = long.MinValue;
            double total = 0, low = double.MaxValue, high = double.MinValue;
            int used = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p == null || p.Count == 0) continue;
                if (used == 0) binSize = p.BinSize;
                if (Math.Abs(p.BinSize - binSize) > binSize * 1e-9) { error = "bin size histogram tidak sama"; return false; }
                used++;
                if (p.BaseBin < loBin) loBin = p.BaseBin;
                if (p.BaseBin + p.Count - 1 > hiBin) hiBin = p.BaseBin + p.Count - 1;
                total += p.TotalVolume;
                if (p.LowPrice < low) low = p.LowPrice;
                if (p.HighPrice > high) high = p.HighPrice;
            }
            if (used == 0) { error = "semua histogram kosong"; return false; }
            if (!(binSize > 0)) { error = "bin size tidak valid"; return false; }

            long n = hiBin - loBin + 1;
            if (n <= 0) { error = "rentang bin tidak valid"; return false; }
            if (n > (maxBins <= 0 ? HardMaxBins : maxBins)) { error = "bin gabungan terlalu banyak (" + n + ")"; return false; }

            var bins = new double[(int)n];
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p == null || p.Count == 0) continue;
                int offset = (int)(p.BaseBin - loBin);
                for (int k = 0; k < p.Count; k++)
                {
                    int idx = offset + k;
                    if (idx >= 0 && idx < bins.Length) bins[idx] += p.Bins[k];
                }
            }
            merged = new ProfileHistogram(binSize, loBin, bins, total, low, high);
            return true;
        }

        /// <summary>
        /// Value Area: mulai dari bin POC, lalu tambah bin berikutnya yang lebih besar
        /// (bandingkan satu bin di atas vs satu bin di bawah) sampai persentase target tercapai.
        /// </summary>
        public static ValueProfile ComputeValueArea(ProfileHistogram h, double valueAreaPercent)
        {
            if (h == null || h.Count == 0 || !(h.TotalVolume > 0)) return null;
            double pct = valueAreaPercent;
            if (!(pct > 0) || pct > 100) pct = 70;

            int poc = 0;
            double best = -1;
            double mid = 0.5 * (h.LowPrice + h.HighPrice);
            for (int i = 0; i < h.Count; i++)
            {
                double v = h.Bins[i];
                if (v > best + 1e-12)
                {
                    best = v; poc = i;
                }
                else if (Math.Abs(v - best) <= 1e-12 && v > 0)
                {
                    // tie: pilih bin yang paling dekat dengan titik tengah rentang
                    if (Math.Abs(h.BinCentre(i) - mid) < Math.Abs(h.BinCentre(poc) - mid)) poc = i;
                }
            }
            if (!(best > 0)) return null;

            double target = h.TotalVolume * pct / 100.0;
            double cum = h.Bins[poc];
            int lo = poc, hi = poc;
            int guard = 0;
            while (cum < target - 1e-12 && (lo > 0 || hi < h.Count - 1) && guard++ < h.Count + 5)
            {
                double above = hi < h.Count - 1 ? h.Bins[hi + 1] : -1.0;
                double below = lo > 0 ? h.Bins[lo - 1] : -1.0;
                if (above < 0 && below < 0) break;
                if (above >= below)
                {
                    hi++;
                    cum += h.Bins[hi];
                }
                else
                {
                    lo--;
                    cum += h.Bins[lo];
                }
            }

            var p = new ValueProfile
            {
                Poc = h.BinCentre(poc),
                Vah = h.BinHigh(hi),
                Val = h.BinLow(lo),
                TotalVolume = h.TotalVolume,
                BinSize = h.BinSize,
                RangeLow = h.LowPrice,
                RangeHigh = h.HighPrice,
                PocBin = poc,
                BinCount = h.Count,
                ValueAreaPercent = pct
            };
            if (!(p.Vah > p.Val)) return null;
            return p;
        }
    }

    /// <summary>Pelacak arah migrasi value area dari hari ke hari.</summary>
    public sealed class MigrationTracker
    {
        public int UpStreak { get; private set; }
        public int DownStreak { get; private set; }
        public int OverlapStreak { get; private set; }
        public MigrationDirection Last { get; private set; }
        public int Pushes { get; private set; }

        public MigrationTracker() { Last = MigrationDirection.Overlap; }

        public void Push(MigrationDirection d)
        {
            Pushes++;
            Last = d;
            switch (d)
            {
                case MigrationDirection.Up:
                    UpStreak++; DownStreak = 0; OverlapStreak = 0;
                    break;
                case MigrationDirection.Down:
                    DownStreak++; UpStreak = 0; OverlapStreak = 0;
                    break;
                default:
                    OverlapStreak++; UpStreak = 0; DownStreak = 0;
                    break;
            }
        }

        public int TrendStreak { get { return Math.Max(UpStreak, DownStreak); } }

        public string Short()
        {
            return Last + " (up " + UpStreak + ", down " + DownStreak + ", overlap " + OverlapStreak + ")";
        }
    }

    /// <summary>Klasifikasi regime dari migrasi + ADX.</summary>
    public static class RegimeClassifier
    {
        public static MigrationDirection Compare(ValueProfile current, ValueProfile previous)
        {
            if (current == null || previous == null || !current.IsValid || !previous.IsValid)
                return MigrationDirection.Overlap;
            double eps = Math.Max(1e-12, current.BinSize * 1e-6);
            bool up = current.Val > previous.Val + eps && current.Poc > previous.Poc + eps;
            bool down = current.Val < previous.Val - eps && current.Poc < previous.Poc - eps;
            if (up) return MigrationDirection.Up;
            if (down) return MigrationDirection.Down;
            return MigrationDirection.Overlap;
        }

        public static MarketRegime Classify(int upStreak, int downStreak, int overlapStreak, double adx,
                                            RegimeOverrideMode mode, int trendStreakMin, int overlapStreakMin,
                                            double adxThreshold)
        {
            if (trendStreakMin < 1) trendStreakMin = 1;
            if (overlapStreakMin < 1) overlapStreakMin = 1;

            if (mode == RegimeOverrideMode.ForceTrend)
            {
                if (upStreak > 0 && upStreak >= downStreak) return MarketRegime.TrendUp;
                if (downStreak > 0) return MarketRegime.TrendDown;
                return MarketRegime.Unknown;
            }
            if (mode == RegimeOverrideMode.ForceRange) return MarketRegime.Range;

            bool adxKnown = !double.IsNaN(adx) && !double.IsInfinity(adx);
            if (upStreak >= trendStreakMin && adxKnown && adx > adxThreshold) return MarketRegime.TrendUp;
            if (downStreak >= trendStreakMin && adxKnown && adx > adxThreshold) return MarketRegime.TrendDown;
            if (overlapStreak >= overlapStreakMin && adxKnown && adx < adxThreshold) return MarketRegime.Range;
            return MarketRegime.Unknown;
        }
    }

    /// <summary>
    /// Engine profil: menyimpan histogram per hari (cache), lalu membangun
    /// SessionProfile (hari terakhir) dan CompositeProfile (N hari terakhir).
    /// Murni: pemanggil yang menyuplai bar per hari.
    /// </summary>
    public sealed class ValueProfileEngine
    {
        readonly Dictionary<DateTime, ProfileHistogram> _daily = new Dictionary<DateTime, ProfileHistogram>();
        readonly List<DateTime> _keys = new List<DateTime>();

        public double BinSize { get; private set; }
        public double ValueAreaPercent { get; set; }
        public int CompositeDays { get; set; }
        public int MaxBins { get; set; }
        public int MinCompositeDays { get; set; }

        public ValueProfile Session { get; private set; }
        public ValueProfile Composite { get; private set; }
        public ValueProfile PreviousComposite { get; private set; }
        public MigrationTracker Migration { get; private set; }
        public DateTime LastBuiltDay { get; private set; }
        public string LastError { get; private set; }
        public int CompositeDayCount { get; private set; }
        public int IngestedDays { get { return _keys.Count; } }
        public IReadOnlyList<DateTime> Days { get { return _keys; } }

        public ValueProfileEngine(double valueAreaPercent, int compositeDays, int maxBins)
        {
            ValueAreaPercent = valueAreaPercent;
            CompositeDays = Math.Max(2, compositeDays);
            MaxBins = maxBins > 0 ? maxBins : VolumeProfile.HardMaxBins;
            MinCompositeDays = 2;
            Migration = new MigrationTracker();
            LastBuiltDay = DateTime.MinValue;
            LastError = "";
        }

        public void Reset(double binSize)
        {
            _daily.Clear();
            _keys.Clear();
            BinSize = binSize;
            Session = null; Composite = null; PreviousComposite = null;
            Migration = new MigrationTracker();
            LastBuiltDay = DateTime.MinValue;
            CompositeDayCount = 0;
            LastError = "";
        }

        /// <summary>Simpan histogram satu hari. Idempotent (hari yang sama tidak dihitung dua kali).</summary>
        public bool IngestDay(DateTime dayKeyUtc, IReadOnlyList<ProfileBar> bars, double binSize, out string error)
        {
            error = null;
            if (!(binSize > 0)) { error = "bin size tidak valid"; return false; }
            if (BinSize > 0 && Math.Abs(BinSize - binSize) > BinSize * 1e-9) Reset(binSize);
            if (!(BinSize > 0)) BinSize = binSize;

            DateTime key = dayKeyUtc.Date;
            if (_daily.ContainsKey(key)) return true;

            ProfileHistogram h;
            if (!VolumeProfile.TryBuild(bars, BinSize, MaxBins, out h, out error)) return false;
            _daily[key] = h;
            int pos = _keys.BinarySearch(key);
            if (pos < 0) _keys.Insert(~pos, key);

            // simpan hanya yang masih relevan (N hari + beberapa hari untuk perbandingan)
            int keep = CompositeDays + 3;
            while (_keys.Count > keep)
            {
                _daily.Remove(_keys[0]);
                _keys.RemoveAt(0);
            }
            return true;
        }

        ValueProfile BuildComposite(DateTime endDayKey)
        {
            int idx = _keys.BinarySearch(endDayKey.Date);
            if (idx < 0) return null;
            int from = Math.Max(0, idx - CompositeDays + 1);
            int count = idx - from + 1;
            if (count < MinCompositeDays) return null;

            var parts = new List<ProfileHistogram>(count);
            for (int i = from; i <= idx; i++)
            {
                ProfileHistogram h;
                if (_daily.TryGetValue(_keys[i], out h) && h != null) parts.Add(h);
            }
            ProfileHistogram merged;
            string err;
            if (!VolumeProfile.TryMerge(parts, MaxBins, out merged, out err))
            {
                LastError = "merge komposit gagal: " + err;
                return null;
            }
            CompositeDayCount = count;
            return VolumeProfile.ComputeValueArea(merged, ValueAreaPercent);
        }

        /// <summary>
        /// Bangun ulang Session/Composite/PreviousComposite untuk hari terakhir yang sudah close.
        /// Migrasi hanya di-push sekali per hari (aman dipanggil berulang pada hari yang sama).
        /// </summary>
        public bool Rebuild(DateTime asOfDayKeyUtc)
        {
            DateTime key = asOfDayKeyUtc.Date;
            if (!_daily.ContainsKey(key)) { LastError = "histogram hari " + key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " belum ada"; return false; }

            ProfileHistogram dayHist = _daily[key];
            Session = VolumeProfile.ComputeValueArea(dayHist, ValueAreaPercent);
            Composite = BuildComposite(key);

            int idx = _keys.BinarySearch(key);
            DateTime prevKey = idx > 0 ? _keys[idx - 1] : DateTime.MinValue;
            PreviousComposite = prevKey == DateTime.MinValue ? null : BuildComposite(prevKey);

            if (key > LastBuiltDay)
            {
                if (Composite != null && PreviousComposite != null)
                    Migration.Push(RegimeClassifier.Compare(Composite, PreviousComposite));
                LastBuiltDay = key;
            }
            return Composite != null && Composite.IsValid;
        }
    }

    // =================================================================================================
    //  3. PURE CORE — COT (Commitment of Traders)
    // =================================================================================================

    /// <summary>Satu baris data COT mingguan. Tanggal = tanggal laporan (Selasa).</summary>
    public sealed class CotRecord
    {
        public DateTime ReportDate;
        public string Market = "";
        public double NonCommLong;
        public double NonCommShort;
        public double Net { get { return NonCommLong - NonCommShort; } }
    }

    /// <summary>Snapshot COT yang sudah "boleh dipakai" pada waktu tertentu (tanpa look-ahead).</summary>
    public sealed class CotSnapshot
    {
        public bool Available;
        public string Reason = "";
        public string Market = "";
        public DateTime ReportDate;
        public DateTime ApplicableUtc;
        public double Net;
        public double Change4W;
        public int Weeks;
        public int AgeDays(DateTime nowUtc) { return (int)Math.Floor((nowUtc.Date - ReportDate.Date).TotalDays); }
        public bool IsStale(DateTime nowUtc, int maxAgeDays) { return AgeDays(nowUtc) > maxAgeDays; }
        public string Short()
        {
            if (!Available) return "COT " + (string.IsNullOrEmpty(Reason) ? "tidak tersedia" : Reason);
            return "COT net " + Fmt.N(Net, 0) + " 4w " + (double.IsNaN(Change4W) ? "n/a" : Fmt.N(Change4W, 0)) +
                   " (" + ReportDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ")";
        }
    }

    /// <summary>Parser CSV COT lokal: date, market, noncomm_long, noncomm_short.</summary>
    public static class CotCsv
    {
        /// <summary>
        /// URL publikasi CFTC adalah Jumat 15:30 waktu New York. Kita pakai 21:00 UTC yang konservatif,
        /// jadi data baru boleh dipakai pada sesi pertama setelah jam itu (tidak ada look-ahead).
        /// </summary>
        public static DateTime ReleaseUtc(DateTime reportDate)
        {
            return reportDate.Date.AddDays(3).AddHours(21);
        }

        public static bool TryParse(IEnumerable<string> lines, string marketFilter,
                                    out List<CotRecord> records, out string chosenMarket,
                                    out int skipped, out string error)
        {
            records = new List<CotRecord>();
            chosenMarket = "";
            skipped = 0;
            error = null;
            if (lines == null) { error = "tidak ada baris CSV"; return false; }

            var byDate = new Dictionary<DateTime, CotRecord>();
            string filter = (marketFilter ?? "").Trim();
            string filterNorm = NormalizeMarket(filter);
            int lineNo = 0;

            foreach (var raw in lines)
            {
                lineNo++;
                if (raw == null) continue;
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                var f = SplitFields(line);
                if (f.Count < 4) { skipped++; continue; }

                DateTime date;
                if (!TryParseDate(f[0], out date)) { skipped++; continue; }  // header / baris rusak

                string market = f[1];
                if (filterNorm.Length > 0 && !NormalizeMarket(market).Contains(filterNorm))
                {
                    continue;
                }

                double lo, sh;
                if (!TryParseNumber(f[2], out lo) || !TryParseNumber(f[3], out sh))
                {
                    skipped++;
                    continue;
                }

                var rec = new CotRecord { ReportDate = date.Date, Market = market, NonCommLong = lo, NonCommShort = sh };
                byDate[rec.ReportDate] = rec;
            }

            if (byDate.Count == 0)
            {
                error = filter.Length > 0
                    ? "tidak ada baris untuk market '" + filter + "'"
                    : "tidak ada baris COT valid";
                return false;
            }

            var list = new List<CotRecord>(byDate.Values);
            list.Sort((a, b) => a.ReportDate.CompareTo(b.ReportDate));

            if (filter.Length > 0)
            {
                chosenMarket = list[list.Count - 1].Market;
                records = list;
            }
            else
            {
                // tanpa filter: pakai satu market saja (market baris terakhir) supaya tidak tercampur
                chosenMarket = list[list.Count - 1].Market;
                string norm = NormalizeMarket(chosenMarket);
                for (int i = 0; i < list.Count; i++)
                    if (NormalizeMarket(list[i].Market) == norm) records.Add(list[i]);
            }
            return records.Count > 0;
        }

        /// <summary>
        /// Ambil data terakhir yang sudah dipublikasikan pada asOfUtc. Perubahan 4 minggu dihitung
        /// dari 4 observasi mingguan sebelumnya (bukan dari tanggal kalender).
        /// </summary>
        public static CotSnapshot SelectAsOf(List<CotRecord> records, DateTime asOfUtc, int maxAgeDays)
        {
            var s = new CotSnapshot();
            if (records == null || records.Count == 0) { s.Reason = "file kosong"; return s; }

            int chosen = -1;
            for (int i = 0; i < records.Count; i++)
            {
                if (ReleaseUtc(records[i].ReportDate) <= asOfUtc) chosen = i;
                else break;
            }
            if (chosen < 0) { s.Reason = "belum ada rilis sebelum " + asOfUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); return s; }

            var r = records[chosen];
            s.Available = true;
            s.Market = r.Market;
            s.ReportDate = r.ReportDate;
            s.ApplicableUtc = ReleaseUtc(r.ReportDate);
            s.Net = r.Net;
            s.Weeks = chosen + 1;
            s.Change4W = chosen >= 4 ? r.Net - records[chosen - 4].Net : double.NaN;
            if (maxAgeDays > 0 && s.IsStale(asOfUtc, maxAgeDays)) s.Reason = "data basi " + s.AgeDays(asOfUtc) + " hari";
            return s;
        }

        static List<string> SplitFields(string line)
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (ch == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = !inQuotes;
                }
                else if ((ch == ',' || ch == ';' || ch == '\t') && !inQuotes)
                {
                    fields.Add(sb.ToString().Trim());
                    sb.Length = 0;
                }
                else sb.Append(ch);
            }
            fields.Add(sb.ToString().Trim());
            return fields;
        }

        static bool TryParseDate(string text, out DateTime date)
        {
            date = DateTime.MinValue;
            if (string.IsNullOrEmpty(text)) return false;
            string t = text.Trim().Trim('"');
            string[] formats = { "yyyy-MM-dd", "yyyy/MM/dd", "yyyy-MM-dd HH:mm:ss", "yyyyMMdd" };
            if (DateTime.TryParseExact(t, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return true;
            return DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }

        static bool TryParseNumber(string text, out double value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text)) return false;
            string t = text.Trim().Trim('"').Replace(" ", "").Replace(",", "");
            return double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }

        public static string NormalizeMarket(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s.ToUpperInvariant())
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            }
            return sb.ToString();
        }
    }

    /// <summary>Keputusan filter COT untuk satu arah trade.</summary>
    public enum CotVerdict { Pass = 0, PassSoftCut = 1, Block = 2 }

    public static class CotPolicy
    {
        public static CotVerdict Evaluate(CotSnapshot snapshot, TradeSide side, CotMode mode,
                                          StaleCotBehavior staleBehavior, DateTime nowUtc,
                                          int maxAgeDays, out string reason)
        {
            reason = "";
            if (mode == CotMode.Off) { reason = "COT off"; return CotVerdict.Pass; }

            bool missing = snapshot == null || !snapshot.Available;
            bool stale = !missing && maxAgeDays > 0 && snapshot.IsStale(nowUtc, maxAgeDays);

            if (missing || stale)
            {
                string why = missing ? (snapshot != null && snapshot.Reason.Length > 0 ? snapshot.Reason : "COT tidak tersedia")
                                     : "COT basi (" + snapshot.AgeDays(nowUtc) + " hari)";
                if (staleBehavior == StaleCotBehavior.Block)
                {
                    reason = why + " -> blokir entry";
                    return CotVerdict.Block;
                }
                reason = why + " -> diabaikan";
                return CotVerdict.Pass;
            }

            bool agree = side == TradeSide.Long
                ? (snapshot.Net > 0 && (double.IsNaN(snapshot.Change4W) || snapshot.Change4W >= 0))
                : (snapshot.Net < 0 && (double.IsNaN(snapshot.Change4W) || snapshot.Change4W <= 0));

            if (agree)
            {
                reason = "COT setuju (net " + Fmt.N(snapshot.Net, 0) + ")";
                return CotVerdict.Pass;
            }

            reason = "COT tidak setuju (net " + Fmt.N(snapshot.Net, 0) + ", 4w " +
                     (double.IsNaN(snapshot.Change4W) ? "n/a" : Fmt.N(snapshot.Change4W, 0)) + ")";
            if (mode == CotMode.Hard)
            {
                reason += " -> blokir (hard)";
                return CotVerdict.Block;
            }
            reason += " -> risiko 50% (soft)";
            return CotVerdict.PassSoftCut;
        }
    }

    // =================================================================================================
    //  4. PURE CORE — bias makro
    // =================================================================================================

    public static class BiasPolicy
    {
        /// <summary>Kontrak input: tanggal kedaluwarsa format yyyy-MM-dd (kosong = tidak kedaluwarsa).</summary>
        public static bool TryParseExpiry(string text, out DateTime expiry)
        {
            expiry = DateTime.MinValue;
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return false;
            string[] formats = { "yyyy-MM-dd", "yyyy/MM/dd", "yyyyMMdd" };
            return DateTime.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture,
                                          DateTimeStyles.None, out expiry);
        }

        /// <summary>Bias dianggap kedaluwarsa bila hari ini sudah lewat dari tanggal expiry (expiry inklusif).</summary>
        public static bool IsExpired(DateTime? expiryUtc, DateTime nowUtc)
        {
            if (!expiryUtc.HasValue) return false;
            return nowUtc.Date > expiryUtc.Value.Date;
        }

        public static double ConvictionScale(int conviction)
        {
            if (conviction <= 1) return 0.5;
            if (conviction >= 3) return 1.5;
            return 1.0;
        }

        public static TradeSide? ToSide(BiasDirection d)
        {
            if (d == BiasDirection.Long) return TradeSide.Long;
            if (d == BiasDirection.Short) return TradeSide.Short;
            return null;
        }

        /// <summary>
        /// Proxy otomatis: tren D1 dari EMA cepat vs EMA lambat + slope EMA cepat.
        /// Ini PROXY, bukan data fundamental. Untuk XAUUSD dengan proxy USDX, set invert = true.
        /// </summary>
        public static BiasDirection ScoreProxy(IReadOnlyList<double> closes, int fastPeriod, int slowPeriod,
                                               int slopeBars, bool invert, out string detail)
        {
            detail = "";
            if (closes == null || closes.Count < slowPeriod + slopeBars + 2)
            {
                detail = "data proxy kurang (" + (closes == null ? 0 : closes.Count) + " bar)";
                return BiasDirection.Neutral;
            }
            int n = closes.Count - 1;
            double[] fast = Ema(closes, fastPeriod);
            double[] slow = Ema(closes, slowPeriod);
            double f = fast[n], s = slow[n], fPrev = fast[n - slopeBars];
            double gapPct = s > 0 ? (f - s) / s * 100.0 : 0;
            double slopePct = fPrev > 0 ? (f - fPrev) / fPrev * 100.0 : 0;
            detail = "EMA" + fastPeriod + " " + Fmt.N(f, 5) + " vs EMA" + slowPeriod + " " + Fmt.N(s, 5) +
                     " (gap " + Fmt.Pct(gapPct, 2) + ", slope " + Fmt.Pct(slopePct, 2) + ")";

            BiasDirection dir;
            if (f > s && f > fPrev) dir = BiasDirection.Long;
            else if (f < s && f < fPrev) dir = BiasDirection.Short;
            else dir = BiasDirection.Neutral;

            if (invert)
            {
                if (dir == BiasDirection.Long) dir = BiasDirection.Short;
                else if (dir == BiasDirection.Short) dir = BiasDirection.Long;
            }
            return dir;
        }

        static double[] Ema(IReadOnlyList<double> values, int period)
        {
            var ema = new double[values.Count];
            if (values.Count == 0) return ema;
            double k = 2.0 / (period + 1.0);
            ema[0] = values[0];
            for (int i = 1; i < values.Count; i++) ema[i] = values[i] * k + ema[i - 1] * (1 - k);
            return ema;
        }
    }

    // =================================================================================================
    //  5. PURE CORE — preset pasar & gate regime
    // =================================================================================================

    public sealed class PresetDecision
    {
        public bool Allowed;
        public string Reason = "";
        public static PresetDecision Yes() { return new PresetDecision { Allowed = true, Reason = "" }; }
        public static PresetDecision No(string why) { return new PresetDecision { Allowed = false, Reason = why }; }
    }

    public static class PresetPolicy
    {
        public static bool TrendMatches(MarketRegime regime, TradeSide side)
        {
            return side == TradeSide.Long ? regime == MarketRegime.TrendUp : regime == MarketRegime.TrendDown;
        }

        public static PresetDecision Evaluate(MarketPreset preset, SignalModel model, TradeSide side,
                                              MarketRegime regime, BiasDirection bias,
                                              bool indexPullbackOk, bool vixOk)
        {
            // --- indeks: long only, butuh trend, butuh pullback, VIX opsional
            if (preset == MarketPreset.Index || preset == MarketPreset.VixExperimental)
            {
                if (side == TradeSide.Short) return PresetDecision.No("preset index: long only");
                if (model == SignalModel.MeanReversion) return PresetDecision.No("preset index: model 3 nonaktif");
                if (bias != BiasDirection.Long) return PresetDecision.No("preset index: bias bukan long");
                if (regime != MarketRegime.TrendUp) return PresetDecision.No("preset index: butuh regime trend up");
                if (!indexPullbackOk) return PresetDecision.No("preset index: pullback dari high 20D belum cukup");
                if (preset == MarketPreset.VixExperimental && !vixOk)
                    return PresetDecision.No("preset VIX: VIX < ambang / data VIX tidak ada");
                return PresetDecision.Yes();
            }

            // --- emas: 1 & 2 saat trend, 3 hanya saat bias netral
            if (preset == MarketPreset.Gold)
            {
                if (model == SignalModel.MeanReversion)
                {
                    if (bias != BiasDirection.Neutral) return PresetDecision.No("emas: model 3 hanya saat bias netral");
                    if (regime != MarketRegime.Range) return PresetDecision.No("emas: model 3 butuh regime range");
                    return PresetDecision.Yes();
                }
                if (bias == BiasDirection.Neutral) return PresetDecision.No("emas: bias netral, model arah nonaktif");
                if (!TrendMatches(regime, side)) return PresetDecision.No("emas: model " + (int)model + " butuh regime trend searah");
                if (BiasPolicy.ToSide(bias) != side) return PresetDecision.No("emas: arah berlawanan dengan bias");
                return PresetDecision.Yes();
            }

            // --- forex: 1 & 2 saat trend, 3 saat range (searah atau netral)
            if (model == SignalModel.MeanReversion)
            {
                if (regime != MarketRegime.Range) return PresetDecision.No("forex: model 3 butuh regime range");
                var biasSide = BiasPolicy.ToSide(bias);
                if (biasSide.HasValue && biasSide.Value != side)
                    return PresetDecision.No("forex: fade berlawanan dengan bias");
                return PresetDecision.Yes();
            }
            if (bias == BiasDirection.Neutral) return PresetDecision.No("forex: bias netral, model arah nonaktif");
            if (!TrendMatches(regime, side)) return PresetDecision.No("forex: model " + (int)model + " butuh regime trend searah");
            if (BiasPolicy.ToSide(bias) != side) return PresetDecision.No("forex: arah berlawanan dengan bias");
            return PresetDecision.Yes();
        }
    }

    // =================================================================================================
    //  6. PURE CORE — tiga model entry (hanya memakai bar yang sudah close)
    // =================================================================================================

    public sealed class AuctionContext
    {
        public double Poc, Vah, Val, Atr;
        public bool AllowLong, AllowShort;
        public double ExcursionMinAtr = 0.25;
        public int ExcursionLookback = 5;
        public double CloseInsideBufferAtr = 0.25;
        public double SlBufferAtr = 0.20;
        public double MinRr = 1.5;
        public double BreakBufferAtr = 0.10;
        public double RetestZoneAtr = 0.25;
        public double RetestSlBufferAtr = 0.30;
        public int RetestWindowBars = 20;
        public bool RegimeTrendUp;
        public bool RegimeTrendDown;
        public bool RegimeRange;
    }

    public sealed class SignalResult
    {
        public SignalModel Model;
        public TradeSide Side;
        public bool Valid;
        public bool Precondition;     // bentuk setup ada, tapi gagal di filter berikutnya
        public string Reject = "";
        public string Note = "";
        public double EntryReference;
        public double Stop;
        public double Tp1;
        public double Tp2;
        public double Rr;
        public double Extreme;
        public int ExtremeBar;

        public string Tag()
        {
            return "M" + (int)Model + " " + Side;
        }

        public string Short()
        {
            if (!Valid) return Tag() + " rejected: " + Reject;
            return Tag() + " entry~" + Fmt.N(EntryReference, 5) + " SL " + Fmt.N(Stop, 5) +
                   " TP1 " + Fmt.N(Tp1, 5) + " TP2 " + Fmt.N(Tp2, 5) + " RR " + Fmt.N(Rr, 2);
        }
    }

    public static class AuctionSignals
    {
        public static bool TryRewardRisk(TradeSide side, double entry, double stop, double target, out double rr)
        {
            rr = 0;
            if (side == TradeSide.Long)
            {
                double risk = entry - stop;
                if (!(risk > 0) || !(target > entry)) return false;
                rr = (target - entry) / risk;
                return rr > 0;
            }
            else
            {
                double risk = stop - entry;
                if (!(risk > 0) || !(target < entry)) return false;
                rr = (entry - target) / risk;
                return rr > 0;
            }
        }

        public static SignalResult FailedAuction(IReadOnlyList<ProfileBar> bars, int idx, TradeSide side, AuctionContext ctx)
        {
            return FailedAuctionCore(bars, idx, side, ctx, SignalModel.FailedAuction);
        }

        public static SignalResult MeanReversion(IReadOnlyList<ProfileBar> bars, int idx, TradeSide side, AuctionContext ctx)
        {
            var r = FailedAuctionCore(bars, idx, side, ctx, SignalModel.MeanReversion);
            if (!r.Valid && r.Reject.Length == 0) return r;
            if (r.Valid && !ctx.RegimeRange)
            {
                r.Valid = false;
                r.Reject = "model 3 hanya saat regime range";
            }
            return r;
        }

        static SignalResult FailedAuctionCore(IReadOnlyList<ProfileBar> bars, int idx, TradeSide side,
                                             AuctionContext ctx, SignalModel model)
        {
            var r = new SignalResult { Model = model, Side = side };
            if (bars == null || idx < 2 || idx >= bars.Count) { r.Reject = "bar belum cukup"; return r; }
            if (!(ctx.Atr > 0)) { r.Reject = "ATR tidak valid"; return r; }
            if (!(ctx.Vah > ctx.Val) || !(ctx.Poc > 0)) { r.Reject = "profil tidak valid"; return r; }
            if (ctx.Vah - ctx.Val < 0.5 * ctx.Atr) { r.Reject = "value area terlalu sempit"; return r; }

            bool isLong = side == TradeSide.Long;
            if (isLong && !ctx.AllowLong) { r.Reject = "sisi long tidak diizinkan"; return r; }
            if (!isLong && !ctx.AllowShort) { r.Reject = "sisi short tidak diizinkan"; return r; }

            int look = Math.Max(1, ctx.ExcursionLookback);
            int from = Math.Max(0, idx - look + 1);
            double excursionNeeded = ctx.ExcursionMinAtr * ctx.Atr;
            double extreme = isLong ? double.MaxValue : double.MinValue;
            int extremeBar = idx;
            for (int i = from; i <= idx; i++)
            {
                double p = isLong ? bars[i].Low : bars[i].High;
                if (isLong ? p < extreme : p > extreme) { extreme = p; extremeBar = i; }
            }

            double beyond = isLong ? (ctx.Val - extreme) : (extreme - ctx.Vah);
            if (!(beyond >= excursionNeeded))
            {
                r.Reject = "tidak ada excursion >= " + Fmt.N(ctx.ExcursionMinAtr, 2) + " ATR di luar VA";
                return r;
            }
            r.Precondition = true;
            r.Extreme = extreme;
            r.ExtremeBar = extremeBar;

            var sig = bars[idx];
            double innerLimit = isLong ? ctx.Vah - ctx.CloseInsideBufferAtr * ctx.Atr
                                       : ctx.Val + ctx.CloseInsideBufferAtr * ctx.Atr;
            bool inside = isLong ? (sig.Close > ctx.Val && sig.Close < innerLimit)
                                 : (sig.Close < ctx.Vah && sig.Close > innerLimit);
            if (!inside)
            {
                r.Reject = "close belum kembali ke dalam value area";
                return r;
            }

            var prev = bars[idx - 1];
            bool prevOutside = isLong ? (prev.Close < ctx.Val || prev.Low < ctx.Val)
                                      : (prev.Close > ctx.Vah || prev.High > ctx.Vah);
            if (!prevOutside) { r.Reject = "bar sebelumnya tidak di luar value area"; return r; }

            double stop = isLong ? extreme - ctx.SlBufferAtr * ctx.Atr
                                 : extreme + ctx.SlBufferAtr * ctx.Atr;
            double entry = sig.Close;
            double tp1 = ctx.Poc;
            double tp2 = isLong ? ctx.Vah : ctx.Val;

            if (isLong ? !(entry > stop) : !(entry < stop))
            {
                r.Reject = "entry dan stop tidak konsisten";
                return r;
            }
            if (isLong ? !(tp1 > entry) : !(tp1 < entry))
            {
                r.Reject = "TP1 (POC) tidak memberi reward dari entry";
                return r;
            }
            if (isLong ? !(tp2 > entry) : !(tp2 < entry))
            {
                r.Reject = "TP2 tidak memberi reward dari entry";
                return r;
            }

            double rr;
            if (!TryRewardRisk(side, entry, stop, tp2, out rr)) { r.Reject = "RR tidak valid"; return r; }
            if (rr < ctx.MinRr)
            {
                r.Reject = "RR " + Fmt.N(rr, 2) + " < " + Fmt.N(ctx.MinRr, 2);
                return r;
            }

            r.Valid = true;
            r.EntryReference = entry;
            r.Stop = stop;
            r.Tp1 = tp1;
            r.Tp2 = tp2;
            r.Rr = rr;
            return r;
        }

        public static SignalResult BreakoutRetest(IReadOnlyList<ProfileBar> bars, int idx, TradeSide side, AuctionContext ctx)
        {
            var r = new SignalResult { Model = SignalModel.BreakoutRetest, Side = side };
            if (bars == null || idx < 3 || idx >= bars.Count) { r.Reject = "bar belum cukup"; return r; }
            if (!(ctx.Atr > 0)) { r.Reject = "ATR tidak valid"; return r; }
            if (!(ctx.Vah > ctx.Val) || !(ctx.Poc > 0)) { r.Reject = "profil tidak valid"; return r; }
            if (ctx.Vah - ctx.Val < 0.5 * ctx.Atr) { r.Reject = "value area terlalu sempit"; return r; }

            bool isLong = side == TradeSide.Long;
            if (isLong && !ctx.AllowLong) { r.Reject = "sisi long tidak diizinkan"; return r; }
            if (!isLong && !ctx.AllowShort) { r.Reject = "sisi short tidak diizinkan"; return r; }

            double buffer = ctx.BreakBufferAtr * ctx.Atr;
            double zone = ctx.RetestZoneAtr * ctx.Atr;
            double accept = isLong ? ctx.Vah + buffer : ctx.Val - buffer;
            double zoneEdge = isLong ? ctx.Vah + zone : ctx.Val - zone;
            double invalidation = isLong ? ctx.Vah - 2 * zone : ctx.Val + 2 * zone;
            int window = Math.Max(3, ctx.RetestWindowBars);

            int acceptanceBar = -1;
            int oldest = Math.Max(1, idx - window);
            for (int i = idx - 1; i >= oldest; i--)
            {
                bool a = isLong ? (bars[i].Close > accept && bars[i - 1].Close > accept)
                                : (bars[i].Close < accept && bars[i - 1].Close < accept);
                if (a) { acceptanceBar = i; break; }
            }
            if (acceptanceBar < 0)
            {
                r.Reject = "belum ada acceptance 2 close di luar VAH/VAL + buffer";
                return r;
            }
            r.Precondition = true;

            bool touched = false;
            double retestExtreme = isLong ? double.MaxValue : double.MinValue;
            for (int i = acceptanceBar; i <= idx; i++)
            {
                double p = isLong ? bars[i].Low : bars[i].High;
                if (isLong ? p <= zoneEdge : p >= zoneEdge) touched = true;
                if (isLong ? p < retestExtreme : p > retestExtreme) retestExtreme = p;
            }
            if (!touched)
            {
                r.Reject = "belum ada pullback ke zona retest";
                return r;
            }

            for (int i = acceptanceBar; i < idx; i++)
            {
                bool invalid = isLong ? bars[i].Close < invalidation : bars[i].Close > invalidation;
                if (invalid) { r.Reject = "retest gagal (close menembus zona)"; return r; }
            }

            var sig = bars[idx];
            bool rejection = isLong ? (sig.Close > ctx.Vah && sig.Close > sig.Open)
                                    : (sig.Close < ctx.Val && sig.Close < sig.Open);
            if (!rejection)
            {
                r.Reject = "bar sinyal bukan rejection yang close kembali di luar VA";
                return r;
            }
            if (isLong ? sig.Low > zoneEdge : sig.High < zoneEdge)
            {
                r.Reject = "bar sinyal tidak menyentuh zona retest";
                return r;
            }

            double stop = isLong ? retestExtreme - ctx.RetestSlBufferAtr * ctx.Atr
                                 : retestExtreme + ctx.RetestSlBufferAtr * ctx.Atr;
            double entry = sig.Close;
            double vaHeight = ctx.Vah - ctx.Val;
            double tp1 = isLong ? ctx.Vah + vaHeight : ctx.Val - vaHeight;
            double tp2 = isLong ? ctx.Vah + 2 * vaHeight : ctx.Val - 2 * vaHeight;

            if (isLong ? !(entry > stop) : !(entry < stop)) { r.Reject = "entry dan stop tidak konsisten"; return r; }
            if (isLong ? !(tp1 > entry) : !(tp1 < entry)) { r.Reject = "TP1 tidak memberi reward dari entry"; return r; }
            if (isLong ? !(tp2 > entry) : !(tp2 < entry)) { r.Reject = "TP2 tidak memberi reward dari entry"; return r; }

            double rr;
            if (!TryRewardRisk(side, entry, stop, tp2, out rr)) { r.Reject = "RR tidak valid"; return r; }
            if (rr < ctx.MinRr)
            {
                r.Reject = "RR " + Fmt.N(rr, 2) + " < " + Fmt.N(ctx.MinRr, 2);
                return r;
            }

            r.Valid = true;
            r.EntryReference = entry;
            r.Stop = stop;
            r.Tp1 = tp1;
            r.Tp2 = tp2;
            r.Rr = rr;
            r.Extreme = retestExtreme;
            r.ExtremeBar = idx;
            return r;
        }

        /// <summary>Filter index: pullback minimal dari high 20 hari (persen).</summary>
        public static bool IndexPullbackOk(double high20, double price, double minPullbackPercent)
        {
            if (!(high20 > 0) || !(price > 0) || !(minPullbackPercent > 0)) return false;
            double dd = (high20 - price) / high20 * 100.0;
            return dd >= minPullbackPercent;
        }
    }

    // =================================================================================================
    //  7. PURE CORE — gate satu sinyal per value area, sizing, waktu
    // =================================================================================================

    /// <summary>Satu sinyal per value area per arah sampai profil baru atau close menembus tepi lawan.</summary>
    public sealed class SignalGate
    {
        public string VaKey = "";
        public bool BlockedLong;
        public bool BlockedShort;

        public void OnNewProfile(string vaKey)
        {
            if (vaKey == null) vaKey = "";
            if (!string.Equals(vaKey, VaKey, StringComparison.Ordinal))
            {
                VaKey = vaKey;
                BlockedLong = false;
                BlockedShort = false;
            }
        }

        public void ObserveClose(double close, double vah, double val)
        {
            if (close > vah) BlockedLong = false;
            if (close < val) BlockedShort = false;
        }

        public bool IsBlocked(TradeSide s) { return s == TradeSide.Long ? BlockedLong : BlockedShort; }
        public void Block(TradeSide s) { if (s == TradeSide.Long) BlockedLong = true; else BlockedShort = true; }
        public void Reset() { BlockedLong = false; BlockedShort = false; }
    }

    public static class Sizing
    {
        public static double RiskPercent(double basePercent, int conviction, double maxPercent)
        {
            double pct = basePercent * BiasPolicy.ConvictionScale(conviction);
            if (maxPercent > 0 && pct > maxPercent) pct = maxPercent;
            return pct;
        }

        /// <summary>Nilai satu unit price movement untuk 1 unit volume, dalam mata uang akun.</summary>
        public static double ValuePerPricePerUnit(double tickValue, double tickSize, long lotSize)
        {
            if (!(tickValue > 0) || !(tickSize > 0) || lotSize <= 0) return 0;
            return (tickValue / tickSize) / lotSize;
        }

        public static double UnitsFromRisk(double riskUsd, double stopDistance, double valuePerPricePerUnit)
        {
            if (!(riskUsd > 0) || !(stopDistance > 0) || !(valuePerPricePerUnit > 0)) return 0;
            return riskUsd / (stopDistance * valuePerPricePerUnit);
        }

        public static bool TotalRiskOk(double openRiskPercent, double newRiskPercent, double maxTotalPercent, out string why)
        {
            why = "";
            if (maxTotalPercent <= 0) return true;
            double total = openRiskPercent + newRiskPercent;
            if (total > maxTotalPercent + 1e-9)
            {
                why = "total risk " + Fmt.Pct(total, 2) + " > " + Fmt.Pct(maxTotalPercent, 2);
                return false;
            }
            return true;
        }

        public static string Normalize(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return "";
            var sb = new StringBuilder(symbol.Length);
            foreach (char c in symbol.ToUpperInvariant())
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>Parse "EURCHF,USDCHF;AUDCAD,AUDUSD" menjadi grup.</summary>
        public static bool TryParseGroups(string spec, out List<List<string>> groups, out string error)
        {
            groups = new List<List<string>>();
            error = null;
            if (string.IsNullOrEmpty(spec) || spec.Trim().Length == 0) return true;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rawGroup in spec.Split(';'))
            {
                if (rawGroup.Trim().Length == 0) continue;
                var group = new List<string>();
                foreach (var rawTok in rawGroup.Split(','))
                {
                    string tok = Normalize(rawTok);
                    if (tok.Length == 0) continue;
                    if (!seen.Add(tok)) { error = "simbol '" + rawTok.Trim() + "' muncul di lebih dari satu grup"; return false; }
                    group.Add(tok);
                }
                if (group.Count < 2) { error = "grup '" + rawGroup.Trim() + "' harus punya minimal 2 simbol"; return false; }
                groups.Add(group);
            }
            return true;
        }

        /// <summary>Nama grup tempat simbol ini berada ("" kalau tidak ada).</summary>
        public static string GroupOf(string symbol, List<List<string>> groups)
        {
            if (groups == null || groups.Count == 0) return "";
            string s = Normalize(symbol);
            if (s.Length == 0) return "";
            for (int i = 0; i < groups.Count; i++)
            {
                for (int k = 0; k < groups[i].Count; k++)
                {
                    if (s.Contains(groups[i][k])) return string.Join("+", groups[i].ToArray());
                }
            }
            return "";
        }
    }

    public static class TimeGuards
    {
        /// <summary>Filter Jumat: tidak ada entry baru setelah jam tertentu (UTC). -1 = mati.</summary>
        public static bool FridayCutoff(DateTime nowUtc, int cutoffHourUtc, out string reason)
        {
            reason = "";
            if (cutoffHourUtc < 0 || cutoffHourUtc > 23) return false;
            if (nowUtc.DayOfWeek == DayOfWeek.Friday && nowUtc.Hour >= cutoffHourUtc)
            {
                reason = "filter Jumat: setelah " + cutoffHourUtc + ":00 UTC";
                return true;
            }
            return false;
        }
    }

#if !MACROAMT_CORE_ONLY
    // =================================================================================================
    //  8. STATE POSISI YANG DIKELOLA BOT
    // =================================================================================================
    public sealed class ManagedTrade
    {
        public int PositionId;
        public TradeSide Side;
        public SignalModel Model;
        public double Entry;
        public double Stop;
        public double Tp1;
        public double Tp2;
        public double RiskUsd;
        public double VolumeUnits;
        public DateTime EntryUtc;
        public string Label = "";
        public bool PartialDone;
        public bool BreakevenDone;
        public bool Trailing;
        public bool Adopted;
        public int ScaleIns;
        public double AtrAtEntry;
    }

    // =================================================================================================
    //  9. ROBOT
    // =================================================================================================
#pragma warning disable 618 // ctor RobotAttribute(string) obsolete tapi Name tidak punya setter di versi ini
    [Robot("MacroAmtSwingBot", TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess,
           DefaultSymbolName = "XAUUSD", AddIndicators = false)]
#pragma warning restore 618
    public class MacroAmtSwingBot : Robot
    {
        const string Version = "1.0.0";

        // ---------------------------------------------------------------- parameter
        // 1. PRESET
        [Parameter("Market preset", DefaultValue = MarketPreset.Gold, Group = "1. Preset",
            Description = "Gold / Forex / Index. Preset menentukan model mana yang boleh jalan.")]
        public MarketPreset Preset { get; set; }

        [Parameter("Entry timeframe", DefaultValue = EntryTimeframeOption.H4, Group = "1. Preset",
            Description = "Timeframe sinyal (hanya bar yang sudah close yang dipakai).")]
        public EntryTimeframeOption EntryTf { get; set; }

        [Parameter("Expected symbol (opsional)", DefaultValue = "", Group = "1. Preset",
            Description = "Kalau diisi, bot menolak jalan bila simbol chart tidak mengandung teks ini. Kosong = bebas.")]
        public string ExpectedSymbol { get; set; }

        [Parameter("Index min pullback % (20D high)", DefaultValue = 2.0, MinValue = 0.1, MaxValue = 20, Step = 0.1, Group = "1. Preset")]
        public double IndexMinPullbackPercent { get; set; }

        [Parameter("VIX symbol (opsional)", DefaultValue = "", Group = "1. Preset",
            Description = "Kosongkan kalau broker tidak punya VIX. Dipakai hanya oleh preset VixExperimental.")]
        public string VixSymbol { get; set; }

        [Parameter("VIX minimum", DefaultValue = 20.0, MinValue = 0, MaxValue = 100, Step = 0.5, Group = "1. Preset")]
        public double VixMinLevel { get; set; }

        [Parameter("Allow experimental VIX preset", DefaultValue = false, Group = "1. Preset",
            Description = "Preset VIX dimatikan secara default karena banyak broker tidak menyediakan VIX.")]
        public bool AllowVixPreset { get; set; }

        // 2. BIAS
        [Parameter("Bias source", DefaultValue = BiasSourceMode.Manual, Group = "2. Bias",
            Description = "Manual = trader mengisi bias. AutoProxy = skor tren D1 simbol proxy (proxy, bukan fundamental).")]
        public BiasSourceMode BiasSource { get; set; }

        [Parameter("Bias direction", DefaultValue = BiasDirection.Neutral, Group = "2. Bias")]
        public BiasDirection MacroBias { get; set; }

        [Parameter("Bias conviction (1-3)", DefaultValue = ConvictionLevel.Medium, Group = "2. Bias",
            Description = "1 = risiko 0.5x, 2 = 1x, 3 = 1.5x (tetap dibatasi MaxRiskPerTradePercent).")]
        public ConvictionLevel BiasConviction { get; set; }

        [Parameter("Bias expiry date (yyyy-MM-dd)", DefaultValue = "", Group = "2. Bias",
            Description = "Setelah tanggal ini bot TIDAK membuka posisi baru. Kosong = tanpa expiry.")]
        public string BiasExpiryDate { get; set; }

        [Parameter("Proxy symbol", DefaultValue = "USDX", Group = "2. Bias")]
        public string ProxySymbol { get; set; }

        [Parameter("Proxy invert", DefaultValue = true, Group = "2. Bias",
            Description = "Untuk XAUUSD dengan proxy USDX: true (USD kuat = emas lemah).")]
        public bool ProxyInvert { get; set; }

        [Parameter("Proxy EMA cepat", DefaultValue = 50, MinValue = 5, MaxValue = 200, Group = "2. Bias")]
        public int ProxyFastEma { get; set; }

        [Parameter("Proxy EMA lambat", DefaultValue = 200, MinValue = 20, MaxValue = 400, Group = "2. Bias")]
        public int ProxySlowEma { get; set; }

        [Parameter("Proxy slope bars", DefaultValue = 5, MinValue = 1, MaxValue = 60, Group = "2. Bias")]
        public int ProxySlopeBars { get; set; }

        // 3. COT
        [Parameter("COT mode", DefaultValue = CotMode.Off, Group = "3. COT",
            Description = "Off / Soft (risiko 50% bila tidak setuju) / Hard (blokir).")]
        public CotMode CotFilter { get; set; }

        [Parameter("COT CSV path", DefaultValue = "", Group = "3. COT",
            Description = "File lokal: date,market,noncomm_long,noncomm_short. Butuh AccessRights.FullAccess.")]
        public string CotCsvPath { get; set; }

        [Parameter("COT market filter", DefaultValue = "GOLD", Group = "3. COT",
            Description = "Teks yang dicocokkan ke kolom market (case-insensitive, substring).")]
        public string CotMarketFilter { get; set; }

        [Parameter("COT max age (hari)", DefaultValue = 10, MinValue = 3, MaxValue = 60, Group = "3. COT")]
        public int CotMaxAgeDays { get; set; }

        [Parameter("COT basi", DefaultValue = StaleCotBehavior.Ignore, Group = "3. COT")]
        public StaleCotBehavior StaleCot { get; set; }

        // 4. PROFILE
        [Parameter("Profile timeframe", DefaultValue = ProfileTimeframeOption.M30, Group = "4. Profile")]
        public ProfileTimeframeOption ProfileTf { get; set; }

        [Parameter("Bin size mode", DefaultValue = BinSizeMode.Auto, Group = "4. Profile")]
        public BinSizeMode BinMode { get; set; }

        [Parameter("Bin size fixed", DefaultValue = 1.0, MinValue = 0.0000001, MaxValue = 100000, Step = 0.01, Group = "4. Profile")]
        public double FixedBinSize { get; set; }

        [Parameter("Auto bin = ATR(D1) x", DefaultValue = 0.05, MinValue = 0.005, MaxValue = 0.5, Step = 0.005, Group = "4. Profile")]
        public double AutoBinAtrFraction { get; set; }

        [Parameter("Value area %", DefaultValue = 70.0, MinValue = 50, MaxValue = 95, Step = 1, Group = "4. Profile")]
        public double ValueAreaPercent { get; set; }

        [Parameter("Composite days", DefaultValue = 20, MinValue = 3, MaxValue = 120, Group = "4. Profile")]
        public int CompositeDays { get; set; }

        [Parameter("Max profile bins", DefaultValue = 4000, MinValue = 100, MaxValue = 20000, Group = "4. Profile")]
        public int MaxProfileBins { get; set; }

        [Parameter("Warmup days", DefaultValue = 6, MinValue = 0, MaxValue = 60, Group = "4. Profile",
            Description = "Hari tambahan yang dibaca untuk menghitung histori migrasi di awal.")]
        public int WarmupDays { get; set; }

        // 5. REGIME
        [Parameter("Regime override", DefaultValue = RegimeOverrideMode.Auto, Group = "5. Regime")]
        public RegimeOverrideMode RegimeMode { get; set; }

        [Parameter("Trend: migrasi minimum", DefaultValue = 2, MinValue = 1, MaxValue = 10, Group = "5. Regime")]
        public int TrendMigrationMin { get; set; }

        [Parameter("Range: overlap minimum", DefaultValue = 3, MinValue = 1, MaxValue = 20, Group = "5. Regime")]
        public int RangeOverlapMin { get; set; }

        [Parameter("ADX trend threshold", DefaultValue = 20.0, MinValue = 5, MaxValue = 60, Step = 0.5, Group = "5. Regime")]
        public double AdxTrendThreshold { get; set; }

        [Parameter("ADX period", DefaultValue = 14, MinValue = 5, MaxValue = 60, Group = "5. Regime")]
        public int AdxPeriod { get; set; }

        [Parameter("ATR period", DefaultValue = 14, MinValue = 5, MaxValue = 60, Group = "5. Regime")]
        public int AtrPeriod { get; set; }

        // 6. MODELS
        [Parameter("Model 1 failed auction", DefaultValue = true, Group = "6. Models")]
        public bool EnableModel1 { get; set; }

        [Parameter("Model 2 breakout retest", DefaultValue = true, Group = "6. Models")]
        public bool EnableModel2 { get; set; }

        [Parameter("Model 3 mean reversion", DefaultValue = true, Group = "6. Models")]
        public bool EnableModel3 { get; set; }

        [Parameter("Excursion lookback (bar)", DefaultValue = 5, MinValue = 1, MaxValue = 50, Group = "6. Models")]
        public int ExcursionLookback { get; set; }

        [Parameter("Excursion minimum (ATR)", DefaultValue = 0.25, MinValue = 0.05, MaxValue = 3.0, Step = 0.05, Group = "6. Models")]
        public double ExcursionMinAtr { get; set; }

        [Parameter("Close-inside buffer (ATR)", DefaultValue = 0.25, MinValue = 0.0, MaxValue = 2.0, Step = 0.05, Group = "6. Models")]
        public double CloseInsideBufferAtr { get; set; }

        [Parameter("SL buffer model 1 & 3 (ATR)", DefaultValue = 0.20, MinValue = 0.0, MaxValue = 2.0, Step = 0.05, Group = "6. Models")]
        public double SlBufferAtr1 { get; set; }

        [Parameter("Breakout buffer (ATR)", DefaultValue = 0.10, MinValue = 0.0, MaxValue = 1.0, Step = 0.02, Group = "6. Models")]
        public double BreakBufferAtr { get; set; }

        [Parameter("Retest zone (ATR)", DefaultValue = 0.25, MinValue = 0.05, MaxValue = 2.0, Step = 0.05, Group = "6. Models")]
        public double RetestZoneAtr { get; set; }

        [Parameter("SL buffer model 2 (ATR)", DefaultValue = 0.30, MinValue = 0.0, MaxValue = 2.0, Step = 0.05, Group = "6. Models")]
        public double RetestSlBufferAtr { get; set; }

        [Parameter("Retest window (bar)", DefaultValue = 20, MinValue = 3, MaxValue = 200, Group = "6. Models")]
        public int RetestWindowBars { get; set; }

        [Parameter("Min reward:risk (TP2)", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 10, Step = 0.1, Group = "6. Models")]
        public double MinRr { get; set; }

        [Parameter("Entry model 1 pakai limit retest", DefaultValue = false, Group = "6. Models",
            Description = "true = limit di VAL/VAH (retest), false = market di open bar berikutnya.")]
        public bool UseLimitAtRetest { get; set; }

        [Parameter("Limit expiry (bar entry TF)", DefaultValue = 3, MinValue = 1, MaxValue = 50, Group = "6. Models")]
        public int LimitExpiryBars { get; set; }

        [Parameter("Model 2 trailing setelah TP1", DefaultValue = false, Group = "6. Models",
            Description = "true = TP2 dilepas dan SL di-trail ATR setelah TP1. false = pakai TP2 (2x tinggi VA).")]
        public bool Model2UseTrailing { get; set; }

        // 7. RISK
        [Parameter("Risk per trade %", DefaultValue = 0.5, MinValue = 0.01, MaxValue = 20, Step = 0.01, Group = "7. Risk")]
        public double RiskPerTradePercent { get; set; }

        [Parameter("Max risk per trade % (cap)", DefaultValue = 2.0, MinValue = 0.05, MaxValue = 50, Step = 0.05, Group = "7. Risk")]
        public double MaxRiskPerTradePercent { get; set; }

        [Parameter("Max total open risk %", DefaultValue = 3.0, MinValue = 0.1, MaxValue = 100, Step = 0.1, Group = "7. Risk")]
        public double MaxTotalOpenRiskPercent { get; set; }

        [Parameter("Daily loss limit %", DefaultValue = 2.0, MinValue = 0.1, MaxValue = 50, Step = 0.1, Group = "7. Risk")]
        public double DailyLossLimitPercent { get; set; }

        [Parameter("Max equity drawdown % (halt)", DefaultValue = 10.0, MinValue = 1, MaxValue = 90, Step = 0.5, Group = "7. Risk")]
        public double MaxEquityDrawdownPercent { get; set; }

        [Parameter("Max open positions", DefaultValue = 3, MinValue = 1, MaxValue = 20, Group = "7. Risk")]
        public int MaxOpenPositions { get; set; }

        [Parameter("Max spread (x ATR D1)", DefaultValue = 0.02, MinValue = 0.001, MaxValue = 0.5, Step = 0.001, Group = "7. Risk",
            Description = "0 = filter mati. Default 2% dari ATR harian.")]
        public double MaxSpreadAtrFraction { get; set; }

        [Parameter("Correlation groups", DefaultValue = "", Group = "7. Risk",
            Description = "Contoh: EURCHF,USDCHF;AUDCAD,AUDUSD. Maksimal 1 posisi per grup.")]
        public string CorrelationGroups { get; set; }

        [Parameter("Max risk per grup %", DefaultValue = 0.0, MinValue = 0, MaxValue = 100, Step = 0.1, Group = "7. Risk",
            Description = "0 = hanya aturan 1 posisi per grup.")]
        public double MaxGroupRiskPercent { get; set; }

        [Parameter("Scale-in maksimum (0-3)", DefaultValue = 0, MinValue = 0, MaxValue = 3, Group = "7. Risk")]
        public int ScaleInMax { get; set; }

        [Parameter("Izinkan scale-in di akun netting", DefaultValue = false, Group = "7. Risk")]
        public bool AllowScaleInOnNetting { get; set; }

        // 8. MANAGEMENT
        [Parameter("TP1 close %", DefaultValue = 50.0, MinValue = 0, MaxValue = 90, Step = 1, Group = "8. Management")]
        public double Tp1ClosePercent { get; set; }

        [Parameter("Trailing mode", DefaultValue = TrailingMode.None, Group = "8. Management")]
        public TrailingMode TrailMode { get; set; }

        [Parameter("Trail ATR multiple", DefaultValue = 0.5, MinValue = 0.1, MaxValue = 5, Step = 0.1, Group = "8. Management")]
        public double TrailAtrMultiple { get; set; }

        [Parameter("Breakeven setelah TP1", DefaultValue = true, Group = "8. Management")]
        public bool BreakevenAfterTp1 { get; set; }

        [Parameter("Max bar tanpa TP1 (time stop)", DefaultValue = 60, MinValue = 0, MaxValue = 2000, Group = "8. Management",
            Description = "0 = time stop mati.")]
        public int MaxBarsInTrade { get; set; }

        [Parameter("Jumat: stop entry setelah jam UTC", DefaultValue = 20, MinValue = -1, MaxValue = 23, Group = "8. Management",
            Description = "-1 = filter mati.")]
        public int FridayCutoffHourUtc { get; set; }

        [Parameter("Max swap negatif per lot / hari", DefaultValue = 0.0, MinValue = 0, MaxValue = 1000, Step = 0.1, Group = "8. Management",
            Description = "0 = filter mati. Contoh -15 berarti swap lebih buruk dari -15 mata uang akun per lot per hari akan diblokir.")]
        public double MaxNegativeSwapPerLotDay { get; set; }

        // 9. VISUAL
        [Parameter("Gambar garis profil", DefaultValue = true, Group = "9. Visual & log")]
        public bool ShowProfileLines { get; set; }

        [Parameter("Tampilkan info panel", DefaultValue = true, Group = "9. Visual & log")]
        public bool ShowInfoPanel { get; set; }

        [Parameter("Tandai sinyal di chart", DefaultValue = true, Group = "9. Visual & log")]
        public bool ShowSignalMarkers { get; set; }

        [Parameter("Log level", DefaultValue = LogVerbosity.Normal, Group = "9. Visual & log")]
        public LogVerbosity LogLevel { get; set; }

        [Parameter("CSV trade log", DefaultValue = false, Group = "9. Visual & log")]
        public bool EnableCsvTradeLog { get; set; }

        [Parameter("CSV log directory", DefaultValue = "", Group = "9. Visual & log",
            Description = "Kosong = Documents/MacroAmtSwingBot.")]
        public string CsvLogDirectory { get; set; }

        // 10. EXECUTION
        [Parameter("Dry run (tidak kirim order)", DefaultValue = false, Group = "10. Execution")]
        public bool DryRun { get; set; }

        [Parameter("Label prefix", DefaultValue = "MAS", Group = "10. Execution")]
        public string LabelPrefix { get; set; }

        // ---------------------------------------------------------------- state
        Bars _profileBars, _entryBars, _d1Bars;
        AverageTrueRange _atrD1;
        DirectionalMovementSystem _dmsD1;
        ValueProfileEngine _engine;
        readonly Dictionary<int, ManagedTrade> _states = new Dictionary<int, ManagedTrade>();
        readonly SignalGate _gate = new SignalGate();
        readonly Dictionary<string, DateTime> _logThrottle = new Dictionary<string, DateTime>(32);
        readonly List<CotRecord> _cotRecords = new List<CotRecord>();
        readonly List<List<string>> _groups = new List<List<string>>();

        CotSnapshot _cot;
        MarketRegime _regime = MarketRegime.Unknown;
        BiasDirection _effectiveBias = BiasDirection.Neutral;
        DateTime? _biasExpiry;
        string _biasDetail = "";
        string _cotStatus = "";
        string _cotMarket = "";
        DateTime _cotFileStamp = DateTime.MinValue;
        DateTime _lastCotRefresh = DateTime.MinValue;

        bool _ready, _monitorOnly;
        string _monitorReason = "";
        bool _ddHalted, _dailyHalted;
        double _peakEquity, _dayStartEquity;
        DateTime _dayUtc = DateTime.MinValue;
        double _atr, _adx, _binSize, _vixLevel = double.NaN;
        DateTime _lastVixCheck = DateTime.MinValue;
        DateTime _lastD1Day = DateTime.MinValue;
        DateTime _lastEntryBarTime = DateTime.MinValue;
        DateTime _lastPanelUpdate = DateTime.MinValue;
        int _entryTfMinutes = 240, _profileTfMinutes = 30;
        int _seq;
        string _labelPrefix = "MAS";
        int _signalCount, _rejectCount, _markerSeq;

        // ---------------------------------------------------------------- lifecycle
        protected override void OnStart()
        {
            _labelPrefix = string.IsNullOrWhiteSpace(LabelPrefix) ? "MAS" : Sizing.Normalize(LabelPrefix);
            if (_labelPrefix.Length > 12) _labelPrefix = _labelPrefix.Substring(0, 12);

            _entryTfMinutes = EntryTf == EntryTimeframeOption.D1 ? 1440 : 240;
            _profileTfMinutes = (int)ProfileTf;

            ValidateParameters();

            try
            {
                Positions.Opened += OnPositionOpened;
                Positions.Closed += OnPositionClosed;
                Timer.Start(5);
            }
            catch (Exception ex) { LogOnce("hook", "gagal pasang event: " + ex.Message, 60); }

            if (_monitorOnly)
            {
                PrintBanner();
                Log("MONITOR ONLY: " + _monitorReason);
                return;
            }

            try
            {
                if (!InitGroups()) { PrintBanner(); Log("MONITOR ONLY: " + _monitorReason); return; }
                if (!LoadMarketData()) { PrintBanner(); Log("MONITOR ONLY: " + _monitorReason); return; }
                if (!InitProfiles()) { PrintBanner(); Log("MONITOR ONLY: " + _monitorReason); return; }

                LoadCot(true);
                ResolveBias(Server.TimeInUtc);
                MaybeHandleNewD1(Server.TimeInUtc, true);

                _peakEquity = Account.Equity;
                _dayStartEquity = Account.Equity;
                _dayUtc = Server.TimeInUtc.Date;
                _ready = true;

                UpdatePanel();
                PrintBanner();
                Log("siap | " + DescribeState());
                MaybeProcessEntryBar(Server.TimeInUtc);
            }
            catch (Exception ex)
            {
                _monitorOnly = true;
                _monitorReason = "startup exception: " + ex.Message;
                Log("STARTUP GAGAL: " + ex);
                PrintBanner();
            }
        }

        protected override void OnStop()
        {
            try { Log("stop | sinyal " + _signalCount + ", ditolak " + _rejectCount + ", posisi dikelola " + _states.Count); }
            catch { }
        }

        protected override void OnTick()
        {
            if (!_ready) return;
            try
            {
                DateTime now = Server.TimeInUtc;
                UpdateEquityGuards(now);
                ManagePositions(now);
                MaybeHandleNewD1(now, false);
                MaybeProcessEntryBar(now);
            }
            catch (Exception ex)
            {
                LogOnce("tick_ex", "OnTick exception: " + ex.GetType().Name + ": " + ex.Message, 30);
            }
        }

        protected override void OnBar()
        {
            if (!_ready) return;
            try
            {
                DateTime now = Server.TimeInUtc;
                UpdateEquityGuards(now);
                MaybeProcessEntryBar(now);
            }
            catch (Exception ex) { LogOnce("bar_ex", "OnBar exception: " + ex.Message, 30); }
        }

        protected override void OnTimer()
        {
            if (!_ready) return;
            try
            {
                DateTime now = Server.TimeInUtc;
                UpdateEquityGuards(now);
                ManagePositions(now);
                MaybeHandleNewD1(now, false);
                if ((now - _lastPanelUpdate).TotalSeconds >= 5) UpdatePanel();
            }
            catch (Exception ex) { LogOnce("timer_ex", "OnTimer exception: " + ex.Message, 30); }
        }

        void OnPositionOpened(PositionOpenedEventArgs args)
        {
            try
            {
                if (args == null || args.Position == null || !IsOurs(args.Position)) return;
                AdoptOrphanPositions();
                UpdatePanel();
            }
            catch (Exception ex) { LogOnce("open_ex", "OnPositionOpened: " + ex.Message, 30); }
        }

        void OnPositionClosed(PositionClosedEventArgs args)
        {
            try
            {
                if (args == null || args.Position == null) return;
                Position position = args.Position;
                ManagedTrade st;
                bool had = _states.TryGetValue(position.Id, out st);
                if (!had && !IsOurs(position)) return;
                if (had) _states.Remove(position.Id);

                string reason = args.Reason.ToString();
                string modelTxt = st != null ? "M" + (int)st.Model : "adopted";
                Log("TUTUP " + position.SymbolName + " " + (position.TradeType == TradeType.Buy ? "LONG" : "SHORT") + " " + modelTxt +
                    " label=" + (position.Label ?? "-") + " net " + Fmt.N(position.NetProfit, 2) +
                    " swap " + Fmt.N(position.Swap, 2) + " comm " + Fmt.N(position.Commissions, 2) + " (" + reason + ")");

                WriteTradeCsv(position, st, reason);
                UpdatePanel();
            }
            catch (Exception ex) { LogOnce("close_ex", "OnPositionClosed: " + ex.Message, 30); }
        }

        // ---------------------------------------------------------------- validasi
        void ValidateParameters()
        {
            var errs = new List<string>();

            if (!(RiskPerTradePercent > 0)) errs.Add("RiskPerTradePercent harus > 0");
            if (MaxRiskPerTradePercent < RiskPerTradePercent)
                errs.Add("MaxRiskPerTradePercent (" + Fmt.N(MaxRiskPerTradePercent, 2) + ") harus >= RiskPerTradePercent");
            if (MaxTotalOpenRiskPercent < RiskPerTradePercent)
                errs.Add("MaxTotalOpenRiskPercent harus >= RiskPerTradePercent");
            if (ValueAreaPercent < 50 || ValueAreaPercent > 95) errs.Add("ValueAreaPercent harus 50-95");
            if (CompositeDays < 3) errs.Add("CompositeDays minimal 3");
            if (BinMode == BinSizeMode.Fixed && !(FixedBinSize > 0)) errs.Add("FixedBinSize harus > 0");
            if (!(AutoBinAtrFraction > 0)) errs.Add("AutoBinAtrFraction harus > 0");
            if (ExcursionLookback < 1) errs.Add("ExcursionLookback minimal 1");
            if (!(MinRr >= 0.5)) errs.Add("MinRr minimal 0.5");
            if (Tp1ClosePercent < 0 || Tp1ClosePercent > 90) errs.Add("Tp1ClosePercent harus 0-90");
            if (MaxBarsInTrade < 0) errs.Add("MaxBarsInTrade tidak boleh negatif");
            if (FridayCutoffHourUtc < -1 || FridayCutoffHourUtc > 23) errs.Add("FridayCutoffHourUtc harus -1..23");
            if (MaxOpenPositions < 1) errs.Add("MaxOpenPositions minimal 1");
            if (ProxyFastEma >= ProxySlowEma) errs.Add("ProxyFastEma harus < ProxySlowEma");
            if (TrendMigrationMin < 1) errs.Add("TrendMigrationMin minimal 1");
            if (RangeOverlapMin < 1) errs.Add("RangeOverlapMin minimal 1");
            if (AtrPeriod < 2 || AdxPeriod < 2) errs.Add("AtrPeriod/AdxPeriod minimal 2");

            if (CotFilter != CotMode.Off && string.IsNullOrWhiteSpace(CotCsvPath))
                errs.Add("CotFilter aktif tapi CotCsvPath kosong");

            DateTime exp;
            if (!string.IsNullOrWhiteSpace(BiasExpiryDate) && !BiasPolicy.TryParseExpiry(BiasExpiryDate, out exp))
                errs.Add("BiasExpiryDate tidak valid (pakai yyyy-MM-dd)");

            if (Preset == MarketPreset.VixExperimental && !AllowVixPreset)
                errs.Add("preset VIX dimatikan; set AllowVixPreset=true atau pilih preset lain");

            if (!string.IsNullOrWhiteSpace(ExpectedSymbol))
            {
                string a = Sizing.Normalize(SymbolName), b = Sizing.Normalize(ExpectedSymbol);
                if (a.Length == 0 || !a.Contains(b))
                    errs.Add("simbol chart '" + SymbolName + "' tidak cocok dengan ExpectedSymbol '" + ExpectedSymbol + "'");
            }

            if (ScaleInMax > 0 && Account.AccountType != AccountType.Hedged && !AllowScaleInOnNetting)
            {
                Log("Scale-in dimatikan otomatis: akun netting tanpa AllowScaleInOnNetting.");
                ScaleInMax = 0;
            }

            List<List<string>> g; string gerr;
            if (!Sizing.TryParseGroups(CorrelationGroups, out g, out gerr))
                errs.Add("CorrelationGroups: " + gerr);

            if (errs.Count > 0)
            {
                _monitorOnly = true;
                _monitorReason = string.Join("; ", errs.ToArray());
            }
        }

        // ---------------------------------------------------------------- data
        bool LoadMarketData()
        {
            _profileBars = MarketData.GetBars(TfOf(_profileTfMinutes));
            _entryBars = _entryTfMinutes >= 1440 ? MarketData.GetBars(TimeFrame.Daily) : MarketData.GetBars(TimeFrame.Hour4);
            _d1Bars = MarketData.GetBars(TimeFrame.Daily);

            if (_profileBars == null || _entryBars == null || _d1Bars == null)
            {
                _monitorReason = "MarketData.GetBars mengembalikan null";
                return false;
            }

            int barsPerDay = Math.Max(1, 1440 / _profileTfMinutes);
            int needProfile = barsPerDay * (CompositeDays + WarmupDays + 4);
            int gotProfile = Warmup(_profileBars, needProfile);
            int gotEntry = Warmup(_entryBars, 320);

            int needD1 = Math.Max(CompositeDays + WarmupDays + 8, AtrPeriod + 20);
            if (BiasSource == BiasSourceMode.AutoProxy) needD1 = Math.Max(needD1, ProxySlowEma + ProxySlopeBars + 20);
            int gotD1 = Warmup(_d1Bars, needD1);

            Log("data: profil(" + _profileTfMinutes + "m)=" + gotProfile + "/" + needProfile +
                " entry=" + gotEntry + " D1=" + gotD1 + "/" + needD1);

            if (_d1Bars.Count < AtrPeriod + 5)
            {
                _monitorReason = "riwayat D1 kurang (" + _d1Bars.Count + " bar, butuh " + (AtrPeriod + 5) + ")";
                return false;
            }
            if (_entryBars.Count < 10)
            {
                _monitorReason = "riwayat entry timeframe kurang (" + _entryBars.Count + " bar)";
                return false;
            }

            _atrD1 = Indicators.AverageTrueRange(_d1Bars, AtrPeriod, MovingAverageType.WilderSmoothing);
            _dmsD1 = Indicators.DirectionalMovementSystem(_d1Bars, AdxPeriod);
            return true;
        }

        int Warmup(Bars bars, int target)
        {
            if (bars == null) return 0;
            int guard = 0;
            while (bars.Count < target && guard < 20)
            {
                int added;
                try { added = bars.LoadMoreHistory(); }
                catch (Exception ex) { LogOnce("warm_" + bars.TimeFrame, "LoadMoreHistory gagal: " + ex.Message, 600); break; }
                guard++;
                if (added <= 0) break;
            }
            return bars.Count;
        }

        static TimeFrame TfOf(int minutes)
        {
            switch (minutes)
            {
                case 60: return TimeFrame.Hour;
                case 240: return TimeFrame.Hour4;
                default: return TimeFrame.Minute30;
            }
        }

        bool InitProfiles()
        {
            if (_d1Bars.Count < 3) { _monitorReason = "D1 kurang dari 3 bar"; return false; }
            _engine = new ValueProfileEngine(ValueAreaPercent, CompositeDays, MaxProfileBins);
            _engine.MinCompositeDays = Math.Min(5, Math.Max(2, CompositeDays));
            _atr = AtrValue();
            _binSize = ComputeBinSize();
            if (!(_binSize > 0)) { _monitorReason = "bin size tidak valid (ATR belum siap)"; return false; }
            RebuildProfiles(_d1Bars.OpenTimes[_d1Bars.Count - 2], true);
            return true;
        }

        bool InitGroups()
        {
            _groups.Clear();
            List<List<string>> g; string err;
            if (!Sizing.TryParseGroups(CorrelationGroups, out g, out err))
            {
                _monitorOnly = true;
                _monitorReason = "CorrelationGroups: " + err;
                return false;
            }
            if (g != null) _groups.AddRange(g);
            return true;
        }

        double AtrValue()
        {
            if (_atrD1 == null || _d1Bars == null) return 0;
            int i = _d1Bars.Count - 2;
            if (i < AtrPeriod + 1) return 0;
            double v = _atrD1.Result[i];
            return (double.IsNaN(v) || v <= 0) ? 0 : v;
        }

        double AdxValue()
        {
            if (_dmsD1 == null || _d1Bars == null) return double.NaN;
            int i = _d1Bars.Count - 2;
            if (i < AdxPeriod + 1) return double.NaN;
            return _dmsD1.ADX[i];
        }

        double ComputeBinSize()
        {
            if (BinMode == BinSizeMode.Fixed) return FixedBinSize;
            double atr = _atr > 0 ? _atr : AtrValue();
            if (!(atr > 0)) return 0;
            double bin = atr * AutoBinAtrFraction;
            if (Symbol.TickSize > 0 && bin < Symbol.TickSize * 2) bin = Symbol.TickSize * 2;
            return bin;
        }

        static int LowerBound(Bars bars, DateTime t)
        {
            int lo = 0, hi = bars.Count;
            var times = bars.OpenTimes;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (times[mid] < t) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        List<ProfileBar> ExtractBars(Bars bars, DateTime start, DateTime end)
        {
            var list = new List<ProfileBar>(128);
            if (bars == null || bars.Count == 0) return list;
            int i = LowerBound(bars, start);
            for (; i < bars.Count; i++)
            {
                DateTime t = bars.OpenTimes[i];
                if (t >= end) break;
                Bar b = bars[i];
                list.Add(new ProfileBar(t, b.Open, b.High, b.Low, b.Close, b.TickVolume));
            }
            return list;
        }

        void RebuildProfiles(DateTime lastClosedDayKey, bool forceReset)
        {
            if (_engine == null || _d1Bars == null || _profileBars == null) return;

            double bin = ComputeBinSize();
            if (!(bin > 0)) { LogOnce("bin0", "bin size belum bisa dihitung (ATR belum siap)", 900); return; }

            bool reset = forceReset || !(_binSize > 0) || Math.Abs(bin - _binSize) > _binSize * 0.10;
            _binSize = bin;
            if (reset)
            {
                _engine.Reset(bin);
                _engine.ValueAreaPercent = ValueAreaPercent;
                _engine.CompositeDays = CompositeDays;
                _engine.MaxBins = MaxProfileBins;
            }

            int lastClosed = _d1Bars.Count - 2;
            if (lastClosed < 1) return;
            int first = Math.Max(1, lastClosed - (CompositeDays + WarmupDays + 2));
            int ingested = 0;
            for (int i = first; i <= lastClosed; i++)
            {
                DateTime s = _d1Bars.OpenTimes[i];
                DateTime e = i + 1 < _d1Bars.Count ? _d1Bars.OpenTimes[i + 1] : s.AddDays(1);
                var bars = ExtractBars(_profileBars, s, e);
                if (bars.Count == 0) continue;
                string err;
                if (_engine.IngestDay(s, bars, _binSize, out err)) ingested++;
                else LogOnce("ingest" + i, "ingest profil " + s.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " gagal: " + err, 3600);
            }

            bool ok;
            if (reset)
            {
                // replay histori supaya streak migrasi tidak mulai dari nol
                ok = false;
                var days = _engine.Days;
                for (int d = 0; d < days.Count; d++)
                {
                    if (_engine.Rebuild(days[d])) ok = true;
                }
            }
            else
            {
                ok = _engine.Rebuild(lastClosedDayKey);
            }

            if (!ok)
            {
                LogOnce("rebuild", "profil komposit belum lengkap: " + _engine.LastError, 900);
            }
            UpdateRegime();
            UpdateVisuals();
            LogOnce("profile_ok", "profil dibangun: hari " + lastClosedDayKey.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                     " ingested " + ingested + " bin " + Fmt.N(_binSize, 5), 60);
        }

        void UpdateRegime()
        {
            _atr = AtrValue();
            _adx = AdxValue();
            if (_engine == null || _engine.Migration == null) { _regime = MarketRegime.Unknown; return; }
            var m = _engine.Migration;
            _regime = RegimeClassifier.Classify(m.UpStreak, m.DownStreak, m.OverlapStreak, _adx, RegimeMode,
                                                TrendMigrationMin, RangeOverlapMin, AdxTrendThreshold);
        }

        void MaybeHandleNewD1(DateTime now, bool force)
        {
            if (_d1Bars == null || _d1Bars.Count < 3) return;
            DateTime dayKey = _d1Bars.OpenTimes[_d1Bars.Count - 2];
            if (!force && dayKey == _lastD1Day) return;
            _lastD1Day = dayKey;

            RebuildProfiles(dayKey, false);
            LoadCot(false);
            ResolveBias(now);
            UpdatePanel();
        }

        // ---------------------------------------------------------------- COT
        void LoadCot(bool force)
        {
            if (CotFilter == CotMode.Off) { _cot = null; _cotStatus = "off"; return; }

            DateTime now = Server.TimeInUtc;
            if (!force && (now - _lastCotRefresh).TotalMinutes < 60) { RefreshCot(now); return; }
            _lastCotRefresh = now;

            if (string.IsNullOrWhiteSpace(CotCsvPath)) { _cot = null; _cotStatus = "path kosong"; return; }

            try
            {
                if (!System.IO.File.Exists(CotCsvPath))
                {
                    _cot = null; _cotStatus = "file tidak ditemukan";
                    LogOnce("cot_missing", "COT: file tidak ditemukan di '" + CotCsvPath + "'", 3600);
                    return;
                }
                DateTime stamp = System.IO.File.GetLastWriteTimeUtc(CotCsvPath);
                if (force || stamp != _cotFileStamp)
                {
                    _cotFileStamp = stamp;
                    string[] lines = System.IO.File.ReadAllLines(CotCsvPath);
                    List<CotRecord> recs; string market; int skipped; string err;
                    if (!CotCsv.TryParse(lines, CotMarketFilter, out recs, out market, out skipped, out err))
                    {
                        _cotRecords.Clear(); _cot = null; _cotStatus = "parse gagal: " + err;
                        Log("COT: " + err + " (" + CotCsvPath + ")");
                        return;
                    }
                    _cotRecords.Clear();
                    _cotRecords.AddRange(recs);
                    _cotMarket = market;
                    Log("COT dimuat: " + recs.Count + " baris, market '" + market + "', dilewati " + skipped);
                }
                RefreshCot(now);
            }
            catch (Exception ex)
            {
                _cot = null;
                _cotStatus = "exception: " + ex.Message;
                LogOnce("cot_ex", "COT exception: " + ex.GetType().Name + " " + ex.Message, 3600);
            }
        }

        void RefreshCot(DateTime now)
        {
            _cot = CotCsv.SelectAsOf(_cotRecords, now, CotMaxAgeDays);
            _cotStatus = _cot == null ? "tidak ada" : (_cot.Available ? _cot.Short() : _cot.Reason);
        }

        // ---------------------------------------------------------------- bias
        void ResolveBias(DateTime now)
        {
            DateTime exp;
            _biasExpiry = BiasPolicy.TryParseExpiry(BiasExpiryDate, out exp) ? (DateTime?)exp : (DateTime?)null;

            if (BiasSource == BiasSourceMode.AutoProxy && !string.IsNullOrWhiteSpace(ProxySymbol))
            {
                Bars pb = TryGetProxyBars(ProxySymbol);
                if (pb != null && pb.Count > ProxySlowEma + ProxySlopeBars + 2)
                {
                    int n = pb.Count - 1; // jangan pakai bar yang masih forming
                    var closes = new List<double>(n);
                    for (int i = 0; i < n; i++) closes.Add(pb.ClosePrices[i]);
                    string detail;
                    _effectiveBias = BiasPolicy.ScoreProxy(closes, ProxyFastEma, ProxySlowEma, ProxySlopeBars, ProxyInvert, out detail);
                    _biasDetail = "proxy " + ProxySymbol + ": " + detail;
                    LogOnce("biasproxy", "bias proxy " + _effectiveBias + " | " + detail, 3600);
                    return;
                }
                _effectiveBias = MacroBias;
                _biasDetail = "proxy tidak tersedia, pakai manual";
                LogOnce("biasfail", "proxy '" + ProxySymbol + "' tidak terbaca; fallback ke bias manual", 3600);
                return;
            }

            _effectiveBias = MacroBias;
            _biasDetail = "manual";
        }

        Bars TryGetProxyBars(string name)
        {
            try
            {
                if (Symbols != null && !Symbols.Exists(name)) return null;
                Bars b = MarketData.GetBars(TimeFrame.Daily, name);
                if (b == null) return null;
                Warmup(b, Math.Max(ProxySlowEma + ProxySlopeBars + 20, 260));
                return b;
            }
            catch (Exception ex)
            {
                LogOnce("proxy_ex", "proxy '" + name + "': " + ex.Message, 3600);
                return null;
            }
        }

        bool BiasExpired(DateTime now)
        {
            return BiasPolicy.IsExpired(_biasExpiry, now);
        }

        // ---------------------------------------------------------------- visual
        void UpdateVisuals()
        {
            try
            {
                if (!ShowProfileLines)
                {
                    string[] names = { "MAS_COMP_POC", "MAS_COMP_VAH", "MAS_COMP_VAL", "MAS_SESS_POC", "MAS_SESS_VAH", "MAS_SESS_VAL" };
                    for (int i = 0; i < names.Length; i++) Chart.RemoveObject(names[i]);
                }
                else if (_engine != null)
                {
                    var c = _engine.Composite;
                    if (c != null && c.IsValid)
                    {
                        DrawLevel("MAS_COMP_POC", c.Poc, Color.Gold, 2, LineStyle.Solid);
                        DrawLevel("MAS_COMP_VAH", c.Vah, Color.OrangeRed, 1, LineStyle.Dots);
                        DrawLevel("MAS_COMP_VAL", c.Val, Color.LimeGreen, 1, LineStyle.Dots);
                    }
                    var s = _engine.Session;
                    if (s != null && s.IsValid)
                    {
                        DrawLevel("MAS_SESS_POC", s.Poc, Color.Silver, 1, LineStyle.DotsVeryRare);
                        DrawLevel("MAS_SESS_VAH", s.Vah, Color.DimGray, 1, LineStyle.DotsVeryRare);
                        DrawLevel("MAS_SESS_VAL", s.Val, Color.DimGray, 1, LineStyle.DotsVeryRare);
                    }
                }
                UpdatePanel();
            }
            catch (Exception ex) { LogOnce("visual", "gambar profil gagal: " + ex.Message, 600); }
        }

        void DrawLevel(string name, double price, Color color, int thickness, LineStyle style)
        {
            if (!(price > 0)) return;
            Chart.DrawHorizontalLine(name, Math.Round(price, Symbol.Digits), color, thickness, style);
        }

        void UpdatePanel()
        {
            try
            {
                if (!ShowInfoPanel) { Chart.RemoveObject("MAS_PANEL"); return; }
                if (_engine == null) return;
                DateTime now = Server.TimeInUtc;
                if ((now - _lastPanelUpdate).TotalSeconds < 2) return;
                _lastPanelUpdate = now;

                var sb = new StringBuilder(512);
                sb.AppendLine("MacroAmtSwingBot " + Version + "  " + SymbolName + "  preset " + Preset + (DryRun ? "  [DRY RUN]" : "  [LIVE]"));
                sb.AppendLine("Regime " + _regime + " | migrasi " + (_engine.Migration != null ? _engine.Migration.Short() : "-") +
                              " | ADX D1 " + (double.IsNaN(_adx) ? "n/a" : Fmt.N(_adx, 1)));
                sb.AppendLine("Bias " + _effectiveBias + " conv " + (int)BiasConviction + " (" + _biasDetail + ")" +
                              (_biasExpiry.HasValue ? " exp " + _biasExpiry.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : ""));
                sb.AppendLine("COT " + (CotFilter == CotMode.Off ? "off" : _cotStatus));
                var c = _engine.Composite;
                sb.AppendLine("Composite(" + _engine.CompositeDayCount + "d): " + (c != null && c.IsValid ? c.Short() : "n/a") +
                              " | bin " + Fmt.N(_binSize, 4));
                var s = _engine.Session;
                sb.AppendLine("Session: " + (s != null && s.IsValid ? s.Short() : "n/a"));
                sb.AppendLine("ATR D1 " + Fmt.N(_atr, 5) + " | risk buka " + Fmt.N(OpenRiskPercent(), 2) + "% / " +
                              Fmt.N(MaxTotalOpenRiskPercent, 2) + "% | posisi " + _states.Count + "/" + MaxOpenPositions);
                if (_ddHalted) sb.AppendLine("HALT: equity drawdown limit tercapai");
                if (_dailyHalted) sb.AppendLine("HALT: daily loss limit tercapai");
                if (BiasExpired(now)) sb.AppendLine("HALT: bias expired, review makro");

                Chart.DrawStaticText("MAS_PANEL", sb.ToString(), VerticalAlignment.Top, HorizontalAlignment.Left, Color.White);
            }
            catch (Exception ex) { LogOnce("panel", "info panel gagal: " + ex.Message, 600); }
        }

        void PrintBanner()
        {
            Log("================ MacroAmtSwingBot " + Version + " ================");
            Log("simbol " + SymbolName + " | preset " + Preset + " | entry TF " + _entryTfMinutes + "m | profile TF " + _profileTfMinutes + "m");
            Log("bias " + MacroBias + " (conviction " + (int)BiasConviction + ", source " + BiasSource + ", expiry '" + (BiasExpiryDate ?? "") + "')");
            Log("COT " + CotFilter + (CotFilter == CotMode.Off ? "" : " market '" + CotMarketFilter + "' file '" + CotCsvPath + "'"));
            Log("risiko " + Fmt.N(RiskPerTradePercent, 2) + "% (cap " + Fmt.N(MaxRiskPerTradePercent, 2) + "%, total " + Fmt.N(MaxTotalOpenRiskPercent, 2) + "%) | max pos " + MaxOpenPositions);
            if (DryRun) Log("DRY RUN aktif: tidak ada order yang dikirim.");
            Log("================================================================");
        }

        string DescribeState()
        {
            var c = _engine != null ? _engine.Composite : null;
            return "regime=" + _regime + " bias=" + _effectiveBias + " atr=" + Fmt.N(_atr, 5) +
                   " bin=" + Fmt.N(_binSize, 5) + " composite=" + (c != null && c.IsValid ? c.Short() : "n/a");
        }

        // ---------------------------------------------------------------- log helper
        void Log(string message)
        {
            try
            {
                string stamp = Server != null ? Server.TimeInUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "--";
                Print((object)(stamp + " UTC | " + message));
            }
            catch { }
        }

        void LogOnce(string key, string message, int seconds)
        {
            DateTime last;
            DateTime now = Server != null ? Server.TimeInUtc : DateTime.UtcNow;
            if (_logThrottle.TryGetValue(key, out last) && (now - last).TotalSeconds < seconds) return;
            _logThrottle[key] = now;
            Log(message);
        }

        void LogSignal(string message)
        {
            if (LogLevel == LogVerbosity.Signals) return;
            Log(message);
        }

        void LogVerbose(string message)
        {
            if (LogLevel != LogVerbosity.Verbose) return;
            Log(message);
        }

        // ---------------------------------------------------------------- guard ekuitas
        void UpdateEquityGuards(DateTime now)
        {
            double eq = Account.Equity;
            if (eq <= 0) return;

            if (_dayUtc != now.Date)
            {
                _dayUtc = now.Date;
                _dayStartEquity = eq;
                _dailyHalted = false;
            }
            if (_peakEquity <= 0 || eq > _peakEquity) _peakEquity = eq;

            if (!_dailyHalted && DailyLossLimitPercent > 0 && _dayStartEquity > 0)
            {
                double limit = _dayStartEquity * (1 - DailyLossLimitPercent / 100.0);
                if (eq <= limit)
                {
                    _dailyHalted = true;
                    Log("DAILY LOSS LIMIT: equity " + Fmt.N(eq, 2) + " <= " + Fmt.N(limit, 2) + " -> tidak ada entry baru hari ini");
                }
            }
            if (!_ddHalted && MaxEquityDrawdownPercent > 0 && _peakEquity > 0)
            {
                double limit = _peakEquity * (1 - MaxEquityDrawdownPercent / 100.0);
                if (eq <= limit)
                {
                    _ddHalted = true;
                    Log("EQUITY DRAWDOWN HALT: equity " + Fmt.N(eq, 2) + " dari puncak " + Fmt.N(_peakEquity, 2) +
                        " -> tidak ada entry baru sampai bot di-restart");
                }
            }
        }

        // ---------------------------------------------------------------- jalur entry
        void MaybeProcessEntryBar(DateTime now)
        {
            if (!_ready || _entryBars == null) return;
            int count = _entryBars.Count;
            if (count < 5) return;
            DateTime lastClosedOpen = _entryBars.OpenTimes[count - 2];
            if (lastClosedOpen == _lastEntryBarTime) return;
            _lastEntryBarTime = lastClosedOpen;

            try { ProcessEntryBar(now, lastClosedOpen); }
            catch (Exception ex)
            {
                LogOnce("proc_ex", "ProcessEntryBar exception: " + ex.GetType().Name + ": " + ex.Message, 60);
            }
        }

        void ProcessEntryBar(DateTime now, DateTime barOpen)
        {
            UpdateRegime();
            if (_engine == null) return;

            ValueProfile comp = _engine.Composite;
            if (comp == null || !comp.IsValid)
            {
                LogOnce("nocomp", "composite profile belum siap; tidak ada entry", 900);
                UpdatePanel();
                return;
            }
            if (!(_atr > 0))
            {
                LogOnce("noatr", "ATR D1 tidak valid; tidak ada entry", 900);
                return;
            }

            DateTime barClose = barOpen.AddMinutes(_entryTfMinutes);
            double ageMinutes = (now - barClose).TotalMinutes;
            if (ageMinutes > _entryTfMinutes)
            {
                LogOnce("stale", "bar entry terakhir sudah lewat " + Fmt.N(ageMinutes, 0) +
                         " menit; sinyal basi tidak dieksekusi", 900);
                UpdatePanel();
                return;
            }

            _gate.OnNewProfile(_engine.LastBuiltDay.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "|" +
                               Fmt.N(comp.Val, 2) + "|" + Fmt.N(comp.Vah, 2));

            int lastIdx = _entryBars.Count - 2;
            _gate.ObserveClose(_entryBars[lastIdx].Close, comp.Vah, comp.Val);

            ManagePositions(now);
            TryScaleIn(now, comp);

            string block;
            if (!EntryAllowedNow(now, comp, out block))
            {
                LogSignal("entry ditahan: " + block);
                UpdatePanel();
                return;
            }

            EvaluateSignals(now, comp, barOpen);
            UpdatePanel();
        }

        bool EntryAllowedNow(DateTime now, ValueProfile comp, out string why)
        {
            why = "";
            if (BiasExpired(now)) { why = "Bias expired, please review macro"; return false; }
            if (TimeGuards.FridayCutoff(now, FridayCutoffHourUtc, out why)) return false;
            if (_dailyHalted) { why = "daily loss limit tercapai"; return false; }
            if (_ddHalted) { why = "equity drawdown halt"; return false; }
            if (_states.Count >= MaxOpenPositions) { why = "posisi maksimum " + _states.Count + "/" + MaxOpenPositions; return false; }
            if (HasOurPending()) { why = "masih ada limit order bot yang pending"; return false; }
            if (!Symbol.IsTradingEnabled) { why = "trading simbol dimatikan broker"; return false; }
            if (MaxSpreadAtrFraction > 0 && _atr > 0)
            {
                double maxSpread = _atr * MaxSpreadAtrFraction;
                if (Symbol.Spread > maxSpread)
                {
                    why = "spread " + Fmt.N(Symbol.Spread, Symbol.Digits) + " > " + Fmt.N(maxSpread, Symbol.Digits);
                    return false;
                }
            }
            return true;
        }

        void EvaluateSignals(DateTime now, ValueProfile comp, DateTime barOpen)
        {
            List<ProfileBar> bars = RecentBars();
            int idx = bars.Count - 1;
            if (idx < 4) return;

            double lastClose = bars[idx].Close;
            double high20 = HighOfLastNDays(20);
            bool pullbackOk = AuctionSignals.IndexPullbackOk(high20, lastClose, IndexMinPullbackPercent);
            bool vixOk = VixOk(now);

            var models = new List<SignalModel>(3);
            if (EnableModel1) models.Add(SignalModel.FailedAuction);
            if (EnableModel2) models.Add(SignalModel.BreakoutRetest);
            if (EnableModel3) models.Add(SignalModel.MeanReversion);
            if (models.Count == 0) { LogOnce("nomodel", "tidak ada model yang aktif", 3600); return; }

            for (int m = 0; m < models.Count; m++)
            {
                SignalModel model = models[m];
                for (int s = 0; s < 2; s++)
                {
                    TradeSide side = s == 0 ? TradeSide.Long : TradeSide.Short;
                    if (_gate.IsBlocked(side))
                    {
                        LogVerbose("M" + (int)model + " " + side + " dilewati: value area ini sudah dipakai");
                        continue;
                    }

                    PresetDecision pd = PresetPolicy.Evaluate(Preset, model, side, _regime, _effectiveBias, pullbackOk, vixOk);
                    if (!pd.Allowed)
                    {
                        LogVerbose("M" + (int)model + " " + side + " ditolak preset: " + pd.Reason);
                        continue;
                    }

                    if (MaxNegativeSwapPerLotDay > 0)
                    {
                        double swap = SwapCostPerLotPerDay(side);
                        if (swap < -MaxNegativeSwapPerLotDay)
                        {
                            LogSignal("M" + (int)model + " " + side + " ditolak: swap " + Fmt.N(swap, 2) + " per lot/hari");
                            continue;
                        }
                    }

                    string cotWhy;
                    CotVerdict cot = CotPolicy.Evaluate(_cot, side, CotFilter, StaleCot, now, CotMaxAgeDays, out cotWhy);
                    if (cot == CotVerdict.Block)
                    {
                        _rejectCount++;
                        LogSignal("M" + (int)model + " " + side + " ditolak COT: " + cotWhy);
                        continue;
                    }

                    AuctionContext ctx = BuildContext(comp, side, true);
                    SignalResult res;
                    try
                    {
                        if (model == SignalModel.FailedAuction) res = AuctionSignals.FailedAuction(bars, idx, side, ctx);
                        else if (model == SignalModel.BreakoutRetest) res = AuctionSignals.BreakoutRetest(bars, idx, side, ctx);
                        else res = AuctionSignals.MeanReversion(bars, idx, side, ctx);
                    }
                    catch (Exception ex)
                    {
                        LogOnce("sig_ex", "model exception: " + ex.Message, 300);
                        continue;
                    }

                    if (!res.Valid)
                    {
                        if (res.Precondition)
                        {
                            _rejectCount++;
                            LogSignal(res.Short() + " | " + _regime + " | " + comp.Short());
                        }
                        else LogVerbose(res.Short());
                        continue;
                    }

                    LogSignal("SINYAL " + res.Short() + " | regime " + _regime + " | " + cotWhy + " | " + comp.Short());
                    double cotMultiplier = cot == CotVerdict.PassSoftCut ? 0.5 : 1.0;
                    if (TryEnter(res, cotMultiplier, now, barOpen, comp, cotWhy)) return;
                }
            }
        }

        AuctionContext BuildContext(ValueProfile comp, TradeSide side, bool allowed)
        {
            return new AuctionContext
            {
                Poc = comp.Poc,
                Vah = comp.Vah,
                Val = comp.Val,
                Atr = _atr,
                AllowLong = allowed && side == TradeSide.Long,
                AllowShort = allowed && side == TradeSide.Short,
                ExcursionMinAtr = ExcursionMinAtr,
                ExcursionLookback = ExcursionLookback,
                CloseInsideBufferAtr = CloseInsideBufferAtr,
                SlBufferAtr = SlBufferAtr1,
                MinRr = MinRr,
                BreakBufferAtr = BreakBufferAtr,
                RetestZoneAtr = RetestZoneAtr,
                RetestSlBufferAtr = RetestSlBufferAtr,
                RetestWindowBars = RetestWindowBars,
                RegimeTrendUp = _regime == MarketRegime.TrendUp,
                RegimeTrendDown = _regime == MarketRegime.TrendDown,
                RegimeRange = _regime == MarketRegime.Range
            };
        }

        List<ProfileBar> RecentBars()
        {
            int need = Math.Max(Math.Max(RetestWindowBars, ExcursionLookback) + 6, 12);
            var list = new List<ProfileBar>(need);
            int count = _entryBars == null ? 0 : _entryBars.Count;
            if (count < 3) return list;
            int take = Math.Min(need, count - 1);
            int start = count - 1 - take;
            for (int i = start; i < count - 1; i++)
            {
                Bar b = _entryBars[i];
                list.Add(new ProfileBar(_entryBars.OpenTimes[i], b.Open, b.High, b.Low, b.Close, b.TickVolume));
            }
            return list;
        }

        double HighOfLastNDays(int n)
        {
            if (_d1Bars == null) return 0;
            int last = _d1Bars.Count - 2;
            if (last < 1) return 0;
            int from = Math.Max(0, last - n + 1);
            double high = double.MinValue;
            for (int i = from; i <= last; i++)
            {
                double h = _d1Bars[i].High;
                if (h > high) high = h;
            }
            return high == double.MinValue ? 0 : high;
        }

        bool VixOk(DateTime now)
        {
            if (Preset != MarketPreset.VixExperimental) return true;
            if (string.IsNullOrWhiteSpace(VixSymbol)) return true;
            if (!double.IsNaN(_vixLevel) && (now - _lastVixCheck).TotalMinutes < 30) return _vixLevel >= VixMinLevel;
            _lastVixCheck = now;
            Bars b = TryGetProxyBars(VixSymbol);
            if (b == null || b.Count < 2)
            {
                _vixLevel = double.NaN;
                LogOnce("vixna", "VIX tidak tersedia; kondisi VIX diabaikan", 3600);
                return true;
            }
            _vixLevel = b.ClosePrices[b.Count - 2];
            LogOnce("vix", "VIX " + Fmt.N(_vixLevel, 2) + " (minimum " + Fmt.N(VixMinLevel, 1) + ")", 1800);
            return _vixLevel >= VixMinLevel;
        }

        double SwapCostPerLotPerDay(TradeSide side)
        {
            try
            {
                double raw = side == TradeSide.Long ? Symbol.SwapLong : Symbol.SwapShort;
                if (Symbol.SwapCalculationType == SymbolSwapCalculationType.Pips) return raw * Symbol.PipValue;
                if (Symbol.SwapCalculationType == SymbolSwapCalculationType.Points) return raw * Symbol.TickValue;
                if (Symbol.SwapCalculationType == SymbolSwapCalculationType.Percentage)
                {
                    double price = side == TradeSide.Long ? Symbol.Ask : Symbol.Bid;
                    return raw / 100.0 * price * Symbol.LotSize;
                }
                return raw;
            }
            catch { return 0; }
        }

        // ---------------------------------------------------------------- submit order
        bool TryEnter(SignalResult res, double riskMultiplier, DateTime now, DateTime barOpen,
                      ValueProfile comp, string cotNote)
        {
            bool isLong = res.Side == TradeSide.Long;
            double price = isLong ? Symbol.Ask : Symbol.Bid;
            if (!(price > 0)) { LogSignal(res.Tag() + " ditolak: harga tidak valid"); return false; }

            bool useLimit = UseLimitAtRetest && res.Model == SignalModel.FailedAuction;
            double limitPrice = double.NaN;
            if (useLimit)
            {
                limitPrice = isLong ? comp.Val : comp.Vah;
                if (isLong ? !(limitPrice < price) : !(limitPrice > price))
                {
                    LogSignal(res.Tag() + " ditolak: harga sudah melewati level limit retest");
                    return false;
                }
                price = limitPrice;
            }

            double stop = Math.Round(res.Stop, Symbol.Digits);
            double tp2 = Math.Round(res.Tp2, Symbol.Digits);
            double tp1 = Math.Round(res.Tp1, Symbol.Digits);

            if (isLong ? !(stop < price) : !(stop > price))
            {
                LogSignal(res.Tag() + " ditolak: harga entry sudah melewati stop");
                return false;
            }
            if (isLong ? !(tp1 > price) : !(tp1 < price))
            {
                LogSignal(res.Tag() + " ditolak: TP1 sudah terlewati harga");
                return false;
            }

            double rr;
            if (!AuctionSignals.TryRewardRisk(res.Side, price, stop, tp2, out rr))
            {
                LogSignal(res.Tag() + " ditolak: geometri RR tidak valid");
                return false;
            }
            if (rr < MinRr)
            {
                LogSignal(res.Tag() + " ditolak: RR live " + Fmt.N(rr, 2) + " < " + Fmt.N(MinRr, 2));
                return false;
            }

            double equity = Account.Equity;
            if (!(equity > 0)) return false;

            double pct = RiskPerTradePercent * BiasPolicy.ConvictionScale((int)BiasConviction) * riskMultiplier;
            if (pct > MaxRiskPerTradePercent) pct = MaxRiskPerTradePercent;
            if (!(pct > 0)) return false;
            double riskUsd = equity * pct / 100.0;

            string totalWhy;
            if (!Sizing.TotalRiskOk(OpenRiskPercent(), pct, MaxTotalOpenRiskPercent, out totalWhy))
            {
                LogSignal(res.Tag() + " ditolak: " + totalWhy);
                return false;
            }

            string group = Sizing.GroupOf(SymbolName, _groups);
            if (group.Length > 0)
            {
                string groupWhy;
                if (GroupBlocks(group, pct, out groupWhy))
                {
                    LogSignal(res.Tag() + " ditolak: grup " + group + " " + groupWhy);
                    return false;
                }
            }

            if (Account.AccountType == AccountType.Netted)
            {
                foreach (var kv in _states)
                {
                    if (kv.Value.Side != res.Side)
                    {
                        LogSignal(res.Tag() + " ditolak: akun netting sudah punya arah lawan");
                        return false;
                    }
                }
            }

            double stopDistance = Math.Abs(price - stop);
            if (!(stopDistance > 0)) { LogSignal(res.Tag() + " ditolak: jarak stop nol"); return false; }
            if (Symbol.MinStopLossDistance > 0 && stopDistance < Symbol.MinStopLossDistance)
            {
                LogSignal(res.Tag() + " ditolak: stop " + Fmt.N(stopDistance, Symbol.Digits) +
                          " < minimum broker " + Fmt.N(Symbol.MinStopLossDistance, Symbol.Digits));
                return false;
            }
            if (Symbol.MinTakeProfitDistance > 0 && Math.Abs(tp2 - price) < Symbol.MinTakeProfitDistance)
            {
                LogSignal(res.Tag() + " ditolak: TP2 < minimum jarak broker");
                return false;
            }

            double valuePerUnit = Sizing.ValuePerPricePerUnit(Symbol.TickValue, Symbol.TickSize, Symbol.LotSize);
            if (!(valuePerUnit > 0))
            {
                LogSignal(res.Tag() + " ditolak: tick value simbol tidak valid");
                return false;
            }

            double slPips = stopDistance / Symbol.PipSize;
            double rawUnits = Sizing.UnitsFromRisk(riskUsd, stopDistance, valuePerUnit);
            double units = Symbol.NormalizeVolumeInUnits(rawUnits, RoundingMode.Down);
            if (units < Symbol.VolumeInUnitsMin)
            {
                double minRisk = Symbol.VolumeInUnitsMin * stopDistance * valuePerUnit;
                LogSignal(res.Tag() + " ditolak: volume minimum " + Fmt.N(Symbol.VolumeInUnitsMin, 2) +
                          " butuh risiko " + Fmt.N(minRisk, 2) + " > budget " + Fmt.N(riskUsd, 2));
                return false;
            }
            if (Symbol.VolumeInUnitsMax > 0 && units > Symbol.VolumeInUnitsMax)
                units = Symbol.NormalizeVolumeInUnits(Symbol.VolumeInUnitsMax, RoundingMode.Down);

            double capUsd = equity * MaxRiskPerTradePercent / 100.0;
            double stepVolume = Symbol.VolumeInUnitsStep > 0 ? Symbol.VolumeInUnitsStep : 1;
            double plannedRisk = 0;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                double brokerRisk = 0;
                try { brokerRisk = Symbol.AmountRisked(units, slPips); } catch { brokerRisk = 0; }
                double manualRisk = units * stopDistance * valuePerUnit;
                plannedRisk = Math.Max(brokerRisk, manualRisk);
                if (plannedRisk <= capUsd * 1.001 + 1e-9) break;
                units -= stepVolume;
                if (units < Symbol.VolumeInUnitsMin) break;
            }
            if (units < Symbol.VolumeInUnitsMin)
            {
                LogSignal(res.Tag() + " ditolak: setelah penyesuaian volume, ukuran < minimum broker");
                return false;
            }
            if (plannedRisk > capUsd * 1.001 + 1e-9)
            {
                LogSignal(res.Tag() + " ditolak: risiko " + Fmt.N(plannedRisk, 2) + " > cap " + Fmt.N(capUsd, 2));
                return false;
            }

            string label = _labelPrefix + "-M" + (int)res.Model + "-" + (++_seq).ToString("000", CultureInfo.InvariantCulture);
            string comment = "M" + (int)res.Model + " " + res.Side + " rr" + Fmt.N(rr, 2);

            if (DryRun)
            {
                _signalCount++;
                _gate.Block(res.Side);
                DrawMarker(res, barOpen, useLimit);
                Log("DRY RUN: " + res.Tag() + " " + (useLimit ? "limit " + Fmt.N(limitPrice, Symbol.Digits) : "market") +
                    " unit " + Fmt.N(units, 2) + " SL " + Fmt.N(stop, Symbol.Digits) + " TP2 " + Fmt.N(tp2, Symbol.Digits) +
                    " risiko " + Fmt.N(plannedRisk, 2) + " (" + Fmt.Pct(pct, 3) + ") | " + cotNote);
                return true;
            }

            TradeType tt = isLong ? TradeType.Buy : TradeType.Sell;

            if (useLimit)
            {
                DateTime expiry = now.AddMinutes(_entryTfMinutes * Math.Max(1, LimitExpiryBars));
                TradeResult pr;
                try
                {
                    pr = PlaceLimitOrder(tt, SymbolName, units, Math.Round(limitPrice, Symbol.Digits), label,
                                         (double?)stop, (double?)tp2, (ProtectionType?)ProtectionType.Absolute,
                                         (DateTime?)expiry, comment, false);
                }
                catch (Exception ex) { LogSignal("limit exception: " + ex.Message); return false; }

                if (pr == null || !pr.IsSuccessful)
                {
                    LogSignal(res.Tag() + " LIMIT ditolak broker: " + (pr != null && pr.Error.HasValue ? pr.Error.Value.ToString() : "null"));
                    return false;
                }
                _signalCount++;
                _gate.Block(res.Side);
                DrawMarker(res, barOpen, true);
                Log("ORDER LIMIT " + label + " " + res.Side + " " + Fmt.N(units, 2) + " unit @ " +
                    Fmt.N(limitPrice, Symbol.Digits) + " SL " + Fmt.N(stop, Symbol.Digits) + " TP2 " + Fmt.N(tp2, Symbol.Digits) +
                    " risiko " + Fmt.N(plannedRisk, 2) + " (" + Fmt.Pct(pct, 3) + ") | " + cotNote);
                return true;
            }

            TradeResult tr;
            try
            {
                tr = ExecuteMarketOrder(tt, SymbolName, units, label,
                                        (double?)(Math.Abs(price - stop) / Symbol.PipSize),
                                        (double?)(Math.Abs(tp2 - price) / Symbol.PipSize),
                                        comment, false);
            }
            catch (Exception ex) { LogSignal("market order exception: " + ex.Message); return false; }

            if (tr == null || !tr.IsSuccessful)
            {
                LogSignal(res.Tag() + " MARKET ditolak broker: " + (tr != null && tr.Error.HasValue ? tr.Error.Value.ToString() : "null"));
                return false;
            }

            Position pos = tr.Position;
            if (pos == null) pos = Positions.Find(label, SymbolName);
            if (pos == null)
            {
                LogSignal(res.Tag() + " ack tanpa posisi terlihat; akan direkonsiliasi label " + label);
                return false;
            }

            try
            {
                TradeResult mod = ModifyPosition(pos, (double?)stop, (double?)tp2, (ProtectionType?)ProtectionType.Absolute);
                if (mod == null || !mod.IsSuccessful)
                    LogOnce("prot_" + label, "gagal set SL/TP absolut (" + (mod != null && mod.Error.HasValue ? mod.Error.Value.ToString() : "null") + ")", 120);
            }
            catch (Exception ex) { LogOnce("protx_" + label, "set proteksi exception: " + ex.Message, 120); }

            _states[pos.Id] = new ManagedTrade
            {
                PositionId = pos.Id,
                Side = res.Side,
                Model = res.Model,
                Entry = pos.EntryPrice,
                Stop = stop,
                Tp1 = tp1,
                Tp2 = tp2,
                RiskUsd = plannedRisk,
                VolumeUnits = pos.VolumeInUnits,
                EntryUtc = pos.EntryTime,
                Label = label,
                AtrAtEntry = _atr
            };
            _signalCount++;
            _gate.Block(res.Side);
            DrawMarker(res, barOpen, false);

            Log("ORDER " + label + " " + res.Side + " " + Fmt.N(units, 2) + " unit @ " + Fmt.N(pos.EntryPrice, Symbol.Digits) +
                " SL " + Fmt.N(stop, Symbol.Digits) + " TP1 " + Fmt.N(tp1, Symbol.Digits) + " TP2 " + Fmt.N(tp2, Symbol.Digits) +
                " RR " + Fmt.N(rr, 2) + " risiko " + Fmt.N(plannedRisk, 2) + " (" + Fmt.Pct(pct, 3) + ") | " + cotNote);
            UpdatePanel();
            return true;
        }

        void DrawMarker(SignalResult res, DateTime barOpen, bool isLimit)
        {
            if (!ShowSignalMarkers) return;
            try
            {
                int n = ++_markerSeq;
                string name = "MAS_SIG_" + n.ToString(CultureInfo.InvariantCulture);
                bool isLong = res.Side == TradeSide.Long;
                double y = res.Extreme > 0 ? res.Extreme : res.EntryReference;
                Color color = isLong ? Color.LimeGreen : Color.OrangeRed;
                Chart.DrawIcon(name, isLong ? ChartIconType.UpArrow : ChartIconType.DownArrow, barOpen, y, color);
                Chart.DrawText(name + "t", "M" + (int)res.Model + (isLimit ? " limit" : ""), barOpen, y, color);
            }
            catch { }
        }

        // ---------------------------------------------------------------- scale-in
        void TryScaleIn(DateTime now, ValueProfile comp)
        {
            if (ScaleInMax <= 0 || _states.Count == 0) return;
            if (_dailyHalted || _ddHalted) return;
            if (Account.AccountType != AccountType.Hedged && !AllowScaleInOnNetting) return;
            if (!(comp != null && comp.IsValid)) return;

            var snapshot = new List<ManagedTrade>(_states.Values);
            double valuePerUnit = Sizing.ValuePerPricePerUnit(Symbol.TickValue, Symbol.TickSize, Symbol.LotSize);
            if (!(valuePerUnit > 0)) return;

            for (int i = 0; i < snapshot.Count; i++)
            {
                ManagedTrade st = snapshot[i];
                if (st.Adopted || st.PartialDone) continue;
                if (st.ScaleIns >= ScaleInMax) continue;
                if (double.IsNaN(st.Stop) || !(st.Stop > 0)) continue;
                Position pos = Positions.FindById(st.PositionId);
                if (pos == null) continue;

                bool isLong = st.Side == TradeSide.Long;
                double price = isLong ? Symbol.Bid : Symbol.Ask;
                bool inZone = isLong ? (price >= comp.Val && price <= comp.LowerThirdTop)
                                     : (price <= comp.Vah && price >= comp.UpperThirdBottom);
                if (!inZone) continue;
                if (isLong ? !(price > st.Stop) : !(price < st.Stop)) continue;

                double stopDistance = Math.Abs(price - st.Stop);
                if (!(stopDistance > 0)) continue;

                double maxTotal = Account.Equity * MaxRiskPerTradePercent / 100.0;
                double allowedRisk = maxTotal - OpenRiskUsd();
                double thirdRisk = Account.Equity * Sizing.RiskPercent(RiskPerTradePercent, (int)BiasConviction, MaxRiskPerTradePercent) / 100.0 / 3.0;
                double riskBudget = Math.Min(allowedRisk, thirdRisk);
                if (!(riskBudget > 0)) continue;

                double units = Symbol.NormalizeVolumeInUnits(Sizing.UnitsFromRisk(riskBudget, stopDistance, valuePerUnit), RoundingMode.Down);
                if (units < Symbol.VolumeInUnitsMin) continue;

                string label = _labelPrefix + "-S" + (int)st.Model + "-" + (++_seq).ToString("000", CultureInfo.InvariantCulture);
                double slPips = stopDistance / Symbol.PipSize;
                double tpPips = Math.Abs(st.Tp2 - price) / Symbol.PipSize;

                TradeResult r;
                try
                {
                    r = ExecuteMarketOrder(isLong ? TradeType.Buy : TradeType.Sell, SymbolName, units, label,
                                           (double?)slPips, (double?)tpPips, "scale-in", false);
                }
                catch (Exception ex) { LogOnce("scale_ex", "scale-in exception: " + ex.Message, 300); continue; }

                if (r == null || !r.IsSuccessful || r.Position == null)
                {
                    LogOnce("scale_fail", "scale-in ditolak broker", 300);
                    continue;
                }

                Position np = r.Position;
                try { ModifyPosition(np, (double?)st.Stop, (double?)st.Tp2, (ProtectionType?)ProtectionType.Absolute); } catch { }
                st.ScaleIns++;
                _states[np.Id] = new ManagedTrade
                {
                    PositionId = np.Id,
                    Side = st.Side,
                    Model = st.Model,
                    Entry = np.EntryPrice,
                    Stop = st.Stop,
                    Tp1 = st.Tp1,
                    Tp2 = st.Tp2,
                    RiskUsd = riskBudget,
                    VolumeUnits = np.VolumeInUnits,
                    EntryUtc = np.EntryTime,
                    Label = label,
                    AtrAtEntry = _atr,
                    ScaleIns = st.ScaleIns
                };
                Log("SCALE-IN " + label + " " + Fmt.N(units, 2) + " unit @ " + Fmt.N(np.EntryPrice, Symbol.Digits) +
                    " risiko tambahan " + Fmt.N(riskBudget, 2));
            }
        }

        bool GroupBlocks(string group, double newRiskPct, out string why)
        {
            why = "";
            if (MaxGroupRiskPercent > 0)
            {
                double usedPct = GroupRiskPercent(group);
                if (usedPct + newRiskPct > MaxGroupRiskPercent)
                {
                    why = "risiko gabungan " + Fmt.Pct(usedPct + newRiskPct, 2) + " > " + Fmt.Pct(MaxGroupRiskPercent, 2);
                    return true;
                }
                return false;
            }

            foreach (var p in Positions)
            {
                if (p == null) continue;
                if (Sizing.GroupOf(p.SymbolName, _groups) == group)
                {
                    why = "sudah ada posisi " + p.SymbolName;
                    return true;
                }
            }
            return false;
        }

        double GroupRiskPercent(string group)
        {
            double equity = Account.Equity;
            if (!(equity > 0)) return 0;
            double riskUsd = 0;
            foreach (var p in Positions)
            {
                if (p == null) continue;
                if (Sizing.GroupOf(p.SymbolName, _groups) != group) continue;
                double sl = p.StopLoss ?? p.EntryPrice;
                double dist = Math.Abs(p.EntryPrice - sl);
                double vpu = Sizing.ValuePerPricePerUnit(p.Symbol.TickValue, p.Symbol.TickSize, p.Symbol.LotSize);
                riskUsd += p.VolumeInUnits * dist * Math.Max(vpu, 0);
            }
            return riskUsd / equity * 100.0;
        }

        double OpenRiskUsd()
        {
            double equity = Account.Equity;
            double sum = 0;
            foreach (var kv in _states)
            {
                Position p = Positions.FindById(kv.Key);
                if (p == null) continue;
                double sl = p.StopLoss ?? kv.Value.Stop;
                if (double.IsNaN(sl) || !(sl > 0)) continue;
                double dist = Math.Abs(p.EntryPrice - sl);
                double vpu = Sizing.ValuePerPricePerUnit(p.Symbol.TickValue, p.Symbol.TickSize, p.Symbol.LotSize);
                sum += p.VolumeInUnits * dist * Math.Max(vpu, 0);
            }
            return Math.Max(0, sum);
        }

        double OpenRiskPercent()
        {
            double equity = Account.Equity;
            if (!(equity > 0)) return 0;
            return OpenRiskUsd() / equity * 100.0;
        }

        // ---------------------------------------------------------------- manajemen posisi
        void ManagePositions(DateTime now)
        {
            if (_states.Count == 0 && Positions.Count == 0) return;
            AdoptOrphanPositions();
            if (_states.Count == 0) return;

            List<int> dead = null;
            foreach (var kv in _states)
            {
                Position p = Positions.FindById(kv.Key);
                if (p == null)
                {
                    if (dead == null) dead = new List<int>(2);
                    dead.Add(kv.Key);
                    continue;
                }
                try { ManageOne(p, kv.Value, now); }
                catch (Exception ex) { LogOnce("man" + kv.Key, "manage " + kv.Key + " gagal: " + ex.Message, 60); }
            }
            if (dead != null)
            {
                for (int i = 0; i < dead.Count; i++) _states.Remove(dead[i]);
            }
        }

        void ManageOne(Position p, ManagedTrade st, DateTime now)
        {
            bool isLong = st.Side == TradeSide.Long;
            double atr = _atr > 0 ? _atr : st.AtrAtEntry;
            double quote = isLong ? Symbol.Bid : Symbol.Ask;
            bool hasStop = !double.IsNaN(st.Stop) && st.Stop > 0;

            if (!hasStop)
            {
                LogOnce("nostop" + p.Id, "posisi " + p.Label + " tanpa SL diketahui; bot tidak mengubah proteksi", 3600);
            }
            else
            {
                if (!st.PartialDone && Tp1ClosePercent >= 5 && st.Tp1 > 0 &&
                    (isLong ? quote >= st.Tp1 : quote <= st.Tp1))
                {
                    DoPartial(p, st, isLong);
                }

                if (st.PartialDone && BreakevenAfterTp1 && !st.BreakevenDone)
                    MoveStop(p, st.Entry, st, "breakeven TP1");

                if (st.Trailing && atr > 0)
                {
                    double newStop = isLong ? quote - TrailAtrMultiple * atr : quote + TrailAtrMultiple * atr;
                    TryTighten(p, st, newStop, isLong);
                }
            }

            if (MaxBarsInTrade > 0 && !st.PartialDone)
            {
                int bars = BarsSinceEntry(st.EntryUtc);
                if (bars >= MaxBarsInTrade)
                {
                    try
                    {
                        ClosePosition(p);
                        Log("TIME STOP " + (p.Label ?? "") + " setelah " + bars + " bar tanpa TP1");
                    }
                    catch (Exception ex) { LogOnce("tsx" + p.Id, "time stop gagal: " + ex.Message, 120); }
                }
            }
        }

        void DoPartial(Position p, ManagedTrade st, bool isLong)
        {
            try
            {
                double step = Symbol.VolumeInUnitsStep > 0 ? Symbol.VolumeInUnitsStep : 1;
                double closeVol = Math.Floor(p.VolumeInUnits * Tp1ClosePercent / 100.0 / step) * step;
                if (closeVol < step) closeVol = step;
                double remaining = p.VolumeInUnits - closeVol;
                if (remaining < Symbol.VolumeInUnitsMin)
                {
                    TradeResult fr = ClosePosition(p);
                    st.PartialDone = true;
                    Log("TP1 " + (p.Label ?? "") + ": tutup penuh (sisa < volume minimum) " +
                        (fr != null && fr.IsSuccessful ? "ok" : "gagal"));
                    return;
                }

                TradeResult r = ClosePosition(p, closeVol);
                if (r != null && r.IsSuccessful)
                {
                    st.PartialDone = true;
                    Log("TP1 " + (p.Label ?? "") + ": tutup " + Fmt.N(closeVol, 2) + " unit (" + Fmt.N(Tp1ClosePercent, 0) + "%)");
                    if (BreakevenAfterTp1) MoveStop(p, st.Entry, st, "breakeven TP1");

                    bool trail = TrailMode == TrailingMode.AtrTrail ||
                                 (Model2UseTrailing && st.Model == SignalModel.BreakoutRetest);
                    if (trail)
                    {
                        st.Trailing = true;
                        if (Model2UseTrailing && st.Model == SignalModel.BreakoutRetest)
                        {
                            try
                            {
                                TradeResult mr = ModifyPosition(p, p.StopLoss, null, ProtectionType.Absolute);
                                if (mr == null || !mr.IsSuccessful)
                                    LogOnce("tprem" + p.Id, "gagal melepas TP untuk trailing", 300);
                            }
                            catch (Exception ex) { LogOnce("tpremx" + p.Id, "lepas TP exception: " + ex.Message, 300); }
                        }
                        Log("TRAILING aktif untuk " + (p.Label ?? "") + " (ATR x " + Fmt.N(TrailAtrMultiple, 2) + ")");
                    }
                }
                else
                {
                    LogOnce("partfail" + p.Id, "partial close gagal: " +
                             (r != null && r.Error.HasValue ? r.Error.Value.ToString() : "null"), 120);
                }
            }
            catch (Exception ex) { LogOnce("partex" + p.Id, "partial exception: " + ex.Message, 120); }
        }

        bool MoveStop(Position p, double price, ManagedTrade st, string why)
        {
            try
            {
                double rounded = Math.Round(price, Symbol.Digits);
                TradeResult r = ModifyPosition(p, (double?)rounded, p.TakeProfit, (ProtectionType?)ProtectionType.Absolute);
                if (r != null && r.IsSuccessful)
                {
                    st.Stop = rounded;
                    st.BreakevenDone = true;
                    Log("SL " + why + " -> " + Fmt.N(rounded, Symbol.Digits) + " (" + (p.Label ?? "") + ")");
                    return true;
                }
                LogOnce("mod" + p.Id, "modify SL gagal: " +
                         (r != null && r.Error.HasValue ? r.Error.Value.ToString() : "null"), 180);
            }
            catch (Exception ex) { LogOnce("modx" + p.Id, "modify SL exception: " + ex.Message, 180); }
            return false;
        }

        void TryTighten(Position p, ManagedTrade st, double newStop, bool isLong)
        {
            double cur = p.StopLoss ?? (isLong ? double.MinValue : double.MaxValue);
            double minTick = Symbol.TickSize * 0.5;
            if (isLong)
            {
                if (newStop <= cur + minTick) return;
                if (newStop >= p.CurrentPrice - minTick) return;
            }
            else
            {
                if (newStop >= cur - minTick) return;
                if (newStop <= p.CurrentPrice + minTick) return;
            }
            MoveStop(p, newStop, st, "trail ATR");
        }

        int BarsSinceEntry(DateTime entryTime)
        {
            if (_entryBars == null || _entryBars.Count < 2) return 0;
            DateTime lastClosed = _entryBars.OpenTimes[_entryBars.Count - 2];
            double minutes = (lastClosed - entryTime).TotalMinutes;
            if (minutes <= 0) return 0;
            return (int)(minutes / _entryTfMinutes);
        }

        void AdoptOrphanPositions()
        {
            try
            {
                foreach (var p in Positions)
                {
                    if (p == null || !IsOurs(p)) continue;
                    if (_states.ContainsKey(p.Id)) continue;

                    bool isLong = p.TradeType == TradeType.Buy;
                    var st = new ManagedTrade
                    {
                        PositionId = p.Id,
                        Side = isLong ? TradeSide.Long : TradeSide.Short,
                        Model = GuessModel(p.Label),
                        Entry = p.EntryPrice,
                        Stop = p.StopLoss ?? double.NaN,
                        Tp2 = p.TakeProfit ?? 0,
                        EntryUtc = p.EntryTime,
                        VolumeUnits = p.VolumeInUnits,
                        Label = p.Label ?? "",
                        Adopted = true,
                        AtrAtEntry = _atr
                    };

                    double tp2 = st.Tp2 > 0 ? st.Tp2 : (isLong ? p.EntryPrice + _atr : p.EntryPrice - _atr);
                    double poc = _engine != null && _engine.Composite != null ? _engine.Composite.Poc : double.NaN;
                    if (poc > 0 && (isLong ? (poc > p.EntryPrice && poc < tp2) : (poc < p.EntryPrice && poc > tp2)))
                        st.Tp1 = poc;
                    else
                        st.Tp1 = isLong ? p.EntryPrice + (tp2 - p.EntryPrice) * 0.5
                                        : p.EntryPrice - (p.EntryPrice - tp2) * 0.5;
                    st.Tp2 = tp2;

                    double vpu = Sizing.ValuePerPricePerUnit(p.Symbol.TickValue, p.Symbol.TickSize, p.Symbol.LotSize);
                    st.RiskUsd = double.IsNaN(st.Stop) ? 0 : Math.Abs(st.Entry - st.Stop) * p.VolumeInUnits * Math.Max(vpu, 0);
                    _states[p.Id] = st;
                    Log("adopsi posisi " + (p.Label ?? "") + " " + st.Side + " entry " + Fmt.N(st.Entry, Symbol.Digits) +
                        " SL " + (double.IsNaN(st.Stop) ? "tidak ada" : Fmt.N(st.Stop, Symbol.Digits)));
                }
            }
            catch (Exception ex) { LogOnce("adopt", "adopsi posisi gagal: " + ex.Message, 300); }
        }

        SignalModel GuessModel(string label)
        {
            if (!string.IsNullOrEmpty(label))
            {
                if (label.Contains("-M2")) return SignalModel.BreakoutRetest;
                if (label.Contains("-M3")) return SignalModel.MeanReversion;
            }
            return SignalModel.FailedAuction;
        }

        bool IsOurs(Position p)
        {
            return p != null && p.SymbolName == SymbolName && p.Label != null &&
                   p.Label.StartsWith(_labelPrefix + "-", StringComparison.Ordinal);
        }

        bool IsOurs(PendingOrder o)
        {
            return o != null && o.SymbolName == SymbolName && o.Label != null &&
                   o.Label.StartsWith(_labelPrefix + "-", StringComparison.Ordinal);
        }

        bool HasOurPending()
        {
            try
            {
                foreach (var o in PendingOrders)
                {
                    if (IsOurs(o)) return true;
                }
            }
            catch { }
            return false;
        }

        // ---------------------------------------------------------------- CSV
        void WriteTradeCsv(Position p, ManagedTrade st, string reason)
        {
            if (!EnableCsvTradeLog) return;
            try
            {
                string dir = string.IsNullOrWhiteSpace(CsvLogDirectory)
                    ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MacroAmtSwingBot")
                    : CsvLogDirectory;
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "trades_" + Sizing.Normalize(SymbolName) + ".csv");
                bool header = !System.IO.File.Exists(path);

                var sb = new StringBuilder(256);
                if (header)
                    sb.AppendLine("close_time_utc,symbol,label,side,model,entry_time,entry_price,volume_units,risk_usd,net_profit,swap,commissions,pips,reason");

                string[] fields =
                {
                    Server.TimeInUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    SymbolName,
                    CsvText(p.Label),
                    p.TradeType == TradeType.Buy ? "LONG" : "SHORT",
                    st != null ? "M" + (int)st.Model : "adopted",
                    p.EntryTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    Fmt.N(p.EntryPrice, Symbol.Digits),
                    Fmt.N(p.VolumeInUnits, 4),
                    Fmt.N(st != null ? st.RiskUsd : 0, 2),
                    Fmt.N(p.NetProfit, 2),
                    Fmt.N(p.Swap, 2),
                    Fmt.N(p.Commissions, 2),
                    Fmt.N(p.Pips, 1),
                    reason
                };
                sb.AppendLine(string.Join(",", fields));
                System.IO.File.AppendAllText(path, sb.ToString());
            }
            catch (Exception ex) { LogOnce("csv", "CSV log gagal: " + ex.Message, 600); }
        }

        static string CsvText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOf(',') < 0 && s.IndexOf('"') < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
#endif
}
