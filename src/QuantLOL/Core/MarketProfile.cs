using System;

namespace QuantLOL.Core
{
    /// <summary>
    /// Auction-market-theory profile built from broker bars.
    /// Volume is distributed uniformly across each bar's high-low range (a standard
    /// approximation when only OHLC + tick volume is available). The volume is broker
    /// TICK volume, not exchange volume, and is documented as such everywhere.
    /// </summary>
    public sealed class ProfileSnapshot
    {
        public bool HasData;
        public int BarsUsed;
        public double Poc, Vah, Val;
        public double SessionHigh, SessionLow;
        public double InitialBalanceHigh, InitialBalanceLow;
        public double TotalVolume;
        public double BinSize;
        public double[] BinPrices = Array.Empty<double>();
        public double[] BinVolumes = Array.Empty<double>();
        public DateTime StartUtc, EndUtc;
        public bool Partial;   // true when the analysed window does not start at the session boundary

        public double Mid => HasData ? (Vah + Val) / 2.0 : 0;
        public double Range => SessionHigh - SessionLow;

        public bool PriceInValueArea(double p) => HasData && p >= Val && p <= Vah;

        public string Describe()
            => HasData ? $"POC={Poc:0.##} VAL={Val:0.##} VAH={Vah:0.##} IB={InitialBalanceLow:0.##}-{InitialBalanceHigh:0.##} vol={TotalVolume:0}" : "no-profile";
    }

    public static class MarketProfileBuilder
    {
        public static ProfileSnapshot Build(BarDto[] bars, int startIndex, int endIndex, double binSize,
                                            double valueAreaPercent, int initialBalanceBars)
        {
            var p = new ProfileSnapshot();
            if (bars == null || bars.Length == 0 || binSize <= 0) return p;
            startIndex = Math.Max(0, startIndex);
            endIndex = Math.Min(endIndex, bars.Length - 1);
            if (endIndex < startIndex) return p;

            double high = double.MinValue, low = double.MaxValue, vol = 0;
            for (int i = startIndex; i <= endIndex; i++)
            {
                if (bars[i].High > high) high = bars[i].High;
                if (bars[i].Low < low) low = bars[i].Low;
                vol += bars[i].Volume;
            }
            if (high <= low) return p;

            double range = high - low;
            int maxBins = 4000;
            if (range / binSize > maxBins) binSize = range / maxBins;
            double floor = Math.Floor(low / binSize) * binSize;
            int bins = (int)Math.Ceiling((high - floor) / binSize) + 1;
            bins = Math.Max(1, Math.Min(bins, maxBins + 2));
            var vols = new double[bins];

            for (int i = startIndex; i <= endIndex; i++)
            {
                double bLow = bars[i].Low, bHigh = bars[i].High, v = bars[i].Volume;
                if (v <= 0) continue;
                if (bHigh <= bLow)
                {
                    int bi = (int)((bars[i].Close - floor) / binSize);
                    if (bi >= 0 && bi < bins) vols[bi] += v;
                    continue;
                }
                double span = bHigh - bLow;
                int firstBin = (int)Math.Floor((bLow - floor) / binSize);
                int lastBin = (int)Math.Floor((bHigh - floor) / binSize);
                if (firstBin < 0) firstBin = 0;
                if (lastBin >= bins) lastBin = bins - 1;
                for (int b = firstBin; b <= lastBin; b++)
                {
                    double binLo = floor + b * binSize, binHi = binLo + binSize;
                    double ov = Math.Min(binHi, bHigh) - Math.Max(binLo, bLow);
                    if (ov > 0) vols[b] += v * (ov / span);
                }
            }

            int poc = 0;
            for (int b = 1; b < bins; b++) if (vols[b] > vols[poc]) poc = b;   // ties resolve to the lowest price bin
            double total = 0; for (int b = 0; b < bins; b++) total += vols[b];
            double target = total * Numeric.Clamp(valueAreaPercent, 0.1, 0.99) / 100.0;
            double acc = vols[poc];
            int lo = poc, hi = poc;
            while (acc < target && (lo > 0 || hi < bins - 1))
            {
                double vUp = hi < bins - 1 ? vols[hi + 1] : -1;
                double vDn = lo > 0 ? vols[lo - 1] : -1;
                if (vUp >= vDn) { hi++; acc += vols[hi]; }
                else { lo--; acc += vols[lo]; }
            }

            p.HasData = true;
            p.BarsUsed = endIndex - startIndex + 1;
            p.TotalVolume = total;
            p.BinSize = binSize;
            p.BinPrices = new double[bins];
            p.BinVolumes = vols;
            for (int b = 0; b < bins; b++) p.BinPrices[b] = floor + (b + 0.5) * binSize;
            p.Poc = floor + (poc + 0.5) * binSize;
            p.Val = floor + lo * binSize;
            p.Vah = floor + (hi + 1) * binSize;
            p.SessionHigh = high;
            p.SessionLow = low;
            p.StartUtc = bars[startIndex].TimeUtc;
            p.EndUtc = bars[endIndex].TimeUtc;

            if (initialBalanceBars > 0)
            {
                int ibEnd = Math.Min(endIndex, startIndex + initialBalanceBars - 1);
                double ibH = double.MinValue, ibL = double.MaxValue;
                for (int i = startIndex; i <= ibEnd; i++) { if (bars[i].High > ibH) ibH = bars[i].High; if (bars[i].Low < ibL) ibL = bars[i].Low; }
                p.InitialBalanceHigh = ibH; p.InitialBalanceLow = ibL;
            }
            return p;
        }

        /// <summary>
        /// Acceptance: N consecutive closes outside the value area on the same side.
        /// Rejection: a bar pierces the level but closes back inside the value area.
        /// Both are objective, closed-bar-only tests.
        /// </summary>
        public static bool AcceptedAbove(BarDto[] bars, int lastIndex, ProfileSnapshot p, int barsRequired)
        {
            if (p == null || !p.HasData || barsRequired <= 0) return false;
            int from = lastIndex - barsRequired + 1;
            if (from < 0) return false;
            for (int i = from; i <= lastIndex; i++) if (bars[i].Close <= p.Vah) return false;
            return true;
        }

        public static bool AcceptedBelow(BarDto[] bars, int lastIndex, ProfileSnapshot p, int barsRequired)
        {
            if (p == null || !p.HasData || barsRequired <= 0) return false;
            int from = lastIndex - barsRequired + 1;
            if (from < 0) return false;
            for (int i = from; i <= lastIndex; i++) if (bars[i].Close >= p.Val) return false;
            return true;
        }

        public static bool RejectedAtLevel(BarDto[] bars, int index, double level, bool fromAbove, double pierceTolerance)
        {
            if (bars == null || index < 0 || index >= bars.Length) return false;
            var b = bars[index];
            if (fromAbove) return b.High > level + pierceTolerance && b.Close < level;
            return b.Low < level - pierceTolerance && b.Close > level;
        }
    }

    public enum MarketRegime
    {
        Unknown = 0,
        Balance = 1,        // price inside value area
        DiscoveryUp = 2,    // accepted above value area
        DiscoveryDown = 3   // accepted below value area
    }

    public static class RegimeClassifier
    {
        public static MarketRegime Classify(double price, ProfileSnapshot sessionProfile, int acceptanceBars)
        {
            if (sessionProfile == null || !sessionProfile.HasData) return MarketRegime.Unknown;
            if (price > sessionProfile.Vah) return MarketRegime.DiscoveryUp;
            if (price < sessionProfile.Val) return MarketRegime.DiscoveryDown;
            return MarketRegime.Balance;
        }

        public static string Name(MarketRegime r)
        {
            switch (r)
            {
                case MarketRegime.Balance: return "Balance";
                case MarketRegime.DiscoveryUp: return "DiscoveryUp";
                case MarketRegime.DiscoveryDown: return "DiscoveryDown";
                default: return "Unknown";
            }
        }
    }
}
