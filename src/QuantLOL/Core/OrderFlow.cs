using System;

namespace QuantLOL.Core
{
    /// <summary>
    /// Price-based order-flow PROXY computed from broker bars.
    /// It is NOT global order flow: it cannot see other venues, resting liquidity or
    /// true aggressor sides. Each bar's tick volume is split by close location value
    /// (CLV), which is the standard OHLC approximation of aggressive buying/selling.
    /// </summary>
    public sealed class OrderFlowSnapshot
    {
        public int BarsUsed;
        public double Delta;          // signed volume (buy - sell) in tick-volume units
        public double BuyVolume, SellVolume, TotalVolume;
        public double DeltaRatio;     // delta / total volume, in [-1, 1]
        public double PreviousDelta;
        public double DeltaChange;    // delta of the recent half minus the earlier half
        public bool BullishDivergence;
        public bool BearishDivergence;
        public bool BullishAbsorption;
        public bool BearishAbsorption;
        public double PriceChange;

        public string Describe()
            => $"delta={Delta:0} ratio={DeltaRatio:0.000} buy={BuyVolume:0} sell={SellVolume:0} div={(BullishDivergence ? "bull" : BearishDivergence ? "bear" : "none")} abs={(BullishAbsorption ? "bull" : BearishAbsorption ? "bear" : "none")}";
    }

    public static class OrderFlowAnalyzer
    {
        /// <summary>Computes signed-volume statistics for the closed bars ending at lastIndex.</summary>
        public static OrderFlowSnapshot Compute(BarDto[] bars, int lastIndex, int windowBars, double avgBarVolume)
        {
            var r = new OrderFlowSnapshot();
            if (bars == null || bars.Length == 0 || windowBars < 2) return r;
            lastIndex = Math.Min(lastIndex, bars.Length - 1);
            int start = Math.Max(0, lastIndex - windowBars + 1);
            if (lastIndex - start < 1) return r;

            int mid = (start + lastIndex) / 2;
            double firstLow = double.MaxValue, secondLow = double.MaxValue;
            double firstHigh = double.MinValue, secondHigh = double.MinValue;
            double d1 = 0, d2 = 0;
            int n1 = 0, n2 = 0;

            for (int i = start; i <= lastIndex; i++)
            {
                var b = bars[i];
                double range = b.High - b.Low;
                double clv = range <= 0 ? 0 : ((b.Close - b.Low) - (b.High - b.Close)) / range;
                double delta = b.Volume * Numeric.Clamp(clv, -1, 1);
                double buy = b.Volume * (clv + 1) / 2.0;
                double sell = b.Volume - buy;
                r.Delta += delta; r.BuyVolume += buy; r.SellVolume += sell; r.TotalVolume += b.Volume;
                if (i <= mid) { d1 += delta; n1++; firstLow = Math.Min(firstLow, b.Low); firstHigh = Math.Max(firstHigh, b.High); }
                else { d2 += delta; n2++; secondLow = Math.Min(secondLow, b.Low); secondHigh = Math.Max(secondHigh, b.High); }
            }
            r.BarsUsed = lastIndex - start + 1;
            r.DeltaRatio = r.TotalVolume > 0 ? r.Delta / r.TotalVolume : 0;
            r.PreviousDelta = n1 > 0 ? d1 / n1 * Math.Max(1, n2) : 0;
            r.DeltaChange = d2 - d1;
            r.PriceChange = bars[lastIndex].Close - bars[start].Close;

            if (n1 > 0 && n2 > 0)
            {
                r.BullishDivergence = secondLow < firstLow && d2 > d1;
                r.BearishDivergence = secondHigh > firstHigh && d2 < d1;
            }

            // Absorption proxy: unusual volume with an unusually small net price move.
            if (avgBarVolume > 0)
            {
                int absBars = Math.Min(3, r.BarsUsed);
                double v = 0, rng = 0;
                for (int i = lastIndex - absBars + 1; i <= lastIndex; i++) { v += bars[i].Volume; rng += bars[i].High - bars[i].Low; }
                if (v > avgBarVolume * absBars * 1.5)
                {
                    double avgRange = rng / absBars;
                    double windowRange = bars[lastIndex].High - bars[lastIndex - absBars + 1].Low;
                    bool compressed = windowRange < avgRange * absBars * 0.6;
                    if (compressed)
                    {
                        r.BullishAbsorption = r.DeltaRatio < -0.05;
                        r.BearishAbsorption = r.DeltaRatio > 0.05;
                    }
                }
            }
            return r;
        }

        /// <summary>Simple moving average of bar volume over the closed window (tick volume).</summary>
        public static double AverageVolume(BarDto[] bars, int lastIndex, int windowBars)
        {
            if (bars == null || windowBars <= 0) return 0;
            lastIndex = Math.Min(lastIndex, bars.Length - 1);
            int start = Math.Max(0, lastIndex - windowBars + 1);
            double sum = 0; int n = 0;
            for (int i = start; i <= lastIndex; i++) { sum += bars[i].Volume; n++; }
            return n == 0 ? 0 : sum / n;
        }
    }
}
