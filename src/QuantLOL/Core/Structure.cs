using System;
using System.Collections.Generic;

namespace QuantLOL.Core
{
    public sealed class StructureOptions
    {
        public int SwingLookback = 2;          // bars on each side required to confirm a fractal swing
        public int ScanBars = 320;             // history window analysed on each slow-path tick
        public double TickSize = 0.01;
        public int MinSweepPierceTicks = 2;     // wick must exceed the level by at least this many ticks
        public int MinFvgTicks = 10;            // minimum imbalance size
        public int ObLookbackBars = 20;         // how far back to search for the order block candle
        public int SweepWindowBars = 12;        // a sweep stays actionable this many entry-TF bars
        public int DealingRangeSwings = 4;      // swing highs/lows used for premium/discount
        public int FvgScanBars = 60;
    }

    public sealed class SwingPoint
    {
        public int Index;          // bar index of the extreme
        public int ConfirmIndex;   // first bar index at which the swing is knowable (no repaint)
        public double Price;
        public bool IsHigh;
    }

    public sealed class SweepEvent
    {
        public int BarIndex;
        public DateTime BarTimeUtc;
        public double LevelPrice;
        public double Extreme;     // wick extreme that pierced the level
        public LevelKind LevelKind;
        public int Direction;      // +1 = bullish sweep (sell-side liquidity taken), -1 = bearish sweep
        public double PierceTicks;
    }

    public sealed class FvgZone
    {
        public int BarIndex;
        public double Top;
        public double Bottom;
        public int Direction;      // +1 bullish (support), -1 bearish (resistance)
        public bool Filled;
        public int FilledIndex = -1;
    }

    public sealed class Zone
    {
        public double Top;
        public double Bottom;
        public int BarIndex = -1;
        public bool IsValid => BarIndex >= 0 && Top > Bottom;
    }

    /// <summary>
    /// Fully deterministic SMC state derived from closed bars only.
    /// Every field can be recomputed from the same inputs, and every swing carries the
    /// index at which it becomes knowable, which is what prevents look-ahead bias.
    /// </summary>
    public sealed class StructureSnapshot
    {
        public readonly List<SwingPoint> Swings = new List<SwingPoint>(24);
        public int Trend;                    // +1 bullish, -1 bearish, 0 undecided
        public int LastBosIndex = -1;
        public int LastBosDirection;
        public double LastBosLevel;
        public int LastChochIndex = -1;
        public int LastChochDirection;
        public double LastChochLevel;
        public DateTime LastChochTimeUtc;
        public double DealingHigh, DealingLow;
        public double LastSwingHigh, LastSwingLow;
        public SweepEvent BullishSweep;
        public SweepEvent BearishSweep;
        public FvgZone LastBullishFvg;
        public FvgZone LastBearishFvg;
        public Zone BullishOrderBlock = new Zone();
        public Zone BearishOrderBlock = new Zone();
        public int BarsAnalysed;
        public double Atr;                   // average true range of the analysed series (price units)

        public bool ChochWithin(int direction, int currentIndex, int windowBars)
        {
            if (LastChochIndex < 0 || LastChochDirection != direction) return false;
            return currentIndex - LastChochIndex <= windowBars;
        }

        public SweepEvent ActiveSweep(int direction, int currentIndex, int windowBars)
        {
            var s = direction > 0 ? BullishSweep : BearishSweep;
            if (s == null) return null;
            return currentIndex - s.BarIndex <= windowBars ? s : null;
        }

        public bool InDiscount(double price) => DealingHigh > DealingLow && price <= (DealingHigh + DealingLow) / 2.0;
        public bool InPremium(double price) => DealingHigh > DealingLow && price >= (DealingHigh + DealingLow) / 2.0;
    }

    public static class StructureAnalyzer
    {
        /// <summary>
        /// Analyses bars[0..lastIndex] (lastIndex must be a CLOSED bar). contextLevels are
        /// externally derived levels (session/previous-session/VA/GEX) that can also be swept.
        /// </summary>
        public static StructureSnapshot Analyze(BarDto[] bars, int lastIndex, StructureOptions o,
                                                IReadOnlyList<LevelRef> contextLevels = null)
        {
            var snap = new StructureSnapshot();
            if (bars == null || bars.Length == 0 || o == null) return snap;
            lastIndex = Math.Min(lastIndex, bars.Length - 1);
            int k = Math.Max(1, o.SwingLookback);
            int start = Math.Max(0, lastIndex - Math.Max(20, o.ScanBars) + 1);
            int n = lastIndex - start + 1;
            if (n < 3 * k + 3) return snap;
            snap.BarsAnalysed = n;
            snap.Atr = AverageTrueRange(bars, start, lastIndex, Math.Min(14, n - 1));

            // ---- 1. Confirmed fractal swings (confirmation index = i + k, no repaint) ----
            var swings = new List<SwingPoint>(32);
            for (int i = start + k; i <= lastIndex - k; i++)
            {
                bool isHigh = true, isLow = true;
                for (int j = 1; j <= k && (isHigh || isLow); j++)
                {
                    if (bars[i].High <= bars[i - j].High || bars[i].High < bars[i + j].High) isHigh = false;
                    if (bars[i].Low >= bars[i - j].Low || bars[i].Low > bars[i + j].Low) isLow = false;
                }
                if (isHigh && !isLow) swings.Add(new SwingPoint { Index = i, ConfirmIndex = i + k, Price = bars[i].High, IsHigh = true });
                else if (isLow && !isHigh) swings.Add(new SwingPoint { Index = i, ConfirmIndex = i + k, Price = bars[i].Low, IsHigh = false });
            }
            snap.Swings.AddRange(swings.Count > 20 ? swings.GetRange(swings.Count - 20, 20) : swings);

            // ---- 2. BOS / CHoCH walk with only knowable swings ----
            SwingPoint refHigh = null, refLow = null;
            int sw = 0;
            var levelList = new List<LevelRef>();
            if (contextLevels != null) levelList.AddRange(contextLevels);
            for (int i = start + k; i <= lastIndex; i++)
            {
                while (sw < swings.Count && swings[sw].ConfirmIndex <= i)
                {
                    if (swings[sw].IsHigh) refHigh = swings[sw]; else refLow = swings[sw];
                    sw++;
                }
                double c = bars[i].Close;
                if (refHigh != null && c > refHigh.Price)
                {
                    snap.LastBosIndex = i; snap.LastBosDirection = +1; snap.LastBosLevel = refHigh.Price;
                    if (snap.Trend <= 0)
                    {
                        snap.LastChochIndex = i; snap.LastChochDirection = +1;
                        snap.LastChochLevel = refHigh.Price; snap.LastChochTimeUtc = bars[i].TimeUtc;
                    }
                    snap.Trend = +1;
                    var ob = FindOrderBlock(bars, i, o.ObLookbackBars, bullish: true);
                    if (ob != null) snap.BullishOrderBlock = ob;
                    refHigh = null;
                }
                else if (refLow != null && c < refLow.Price)
                {
                    snap.LastBosIndex = i; snap.LastBosDirection = -1; snap.LastBosLevel = refLow.Price;
                    if (snap.Trend >= 0)
                    {
                        snap.LastChochIndex = i; snap.LastChochDirection = -1;
                        snap.LastChochLevel = refLow.Price; snap.LastChochTimeUtc = bars[i].TimeUtc;
                    }
                    snap.Trend = -1;
                    var ob = FindOrderBlock(bars, i, o.ObLookbackBars, bullish: false);
                    if (ob != null) snap.BearishOrderBlock = ob;
                    refLow = null;
                }
            }
            snap.LastSwingHigh = refHigh?.Price ?? LastSwing(swings, true);
            snap.LastSwingLow = refLow?.Price ?? LastSwing(swings, false);

            // ---- 3. Dealing range from the most recent confirmed swings ----
            double hi = double.MinValue, lo = double.MaxValue;
            int highs = 0, lows = 0;
            for (int i = swings.Count - 1; i >= 0 && (highs < o.DealingRangeSwings || lows < o.DealingRangeSwings); i--)
            {
                var s = swings[i];
                if (s.ConfirmIndex > lastIndex) continue;
                if (s.IsHigh && highs < o.DealingRangeSwings) { hi = Math.Max(hi, s.Price); highs++; }
                else if (!s.IsHigh && lows < o.DealingRangeSwings) { lo = Math.Min(lo, s.Price); lows++; }
            }
            if (highs == 0 || lows == 0)
            {
                hi = double.MinValue; lo = double.MaxValue;
                for (int i = start; i <= lastIndex; i++) { if (bars[i].High > hi) hi = bars[i].High; if (bars[i].Low < lo) lo = bars[i].Low; }
            }
            snap.DealingHigh = hi; snap.DealingLow = lo;

            // ---- 4. Liquidity sweeps within the recent window ----
            double pierce = Math.Max(1, o.MinSweepPierceTicks) * o.TickSize;
            int sweepStart = Math.Max(start + k + 1, lastIndex - Math.Max(1, o.SweepWindowBars) + 1);
            for (int i = sweepStart; i <= lastIndex; i++)
            {
                // swing-based sweep: level must be confirmed strictly before this bar
                for (int s = 0; s < swings.Count; s++)
                {
                    var sp = swings[s];
                    if (sp.ConfirmIndex >= i) continue;
                    if (!sp.IsHigh)
                    {
                        double lvl = sp.Price;
                        if (bars[i].Low < lvl - pierce && bars[i].Close > lvl)
                            ConsiderBullish(snap, i, bars, lvl, LevelKind.SwingLow, o.TickSize);
                    }
                    else
                    {
                        double lvl = sp.Price;
                        if (bars[i].High > lvl + pierce && bars[i].Close < lvl)
                            ConsiderBearish(snap, i, bars, lvl, LevelKind.SwingHigh, o.TickSize);
                    }
                }
                for (int L = 0; L < levelList.Count; L++)
                {
                    var lv = levelList[L];
                    if (lv.Price <= 0) continue;
                    if (IsSupport(lv.Kind))
                    {
                        if (bars[i].Low < lv.Price - pierce && bars[i].Close > lv.Price)
                            ConsiderBullish(snap, i, bars, lv.Price, lv.Kind, o.TickSize);
                    }
                    else if (IsResistance(lv.Kind))
                    {
                        if (bars[i].High > lv.Price + pierce && bars[i].Close < lv.Price)
                            ConsiderBearish(snap, i, bars, lv.Price, lv.Kind, o.TickSize);
                    }
                }
            }

            // ---- 5. Fair value gaps (most recent unfilled per direction) ----
            int fstart = Math.Max(start + 2, lastIndex - Math.Max(5, o.FvgScanBars) + 1);
            double minGap = Math.Max(1, o.MinFvgTicks) * o.TickSize;
            for (int i = fstart; i <= lastIndex; i++)
            {
                if (bars[i].Low > bars[i - 2].High + minGap - o.TickSize * 0.5)
                {
                    var z = new FvgZone { BarIndex = i, Bottom = bars[i - 2].High, Top = bars[i].Low, Direction = +1 };
                    z.Filled = IsFilled(bars, i + 1, lastIndex, z);
                    if (z.Filled && z.FilledIndex < 0) z.FilledIndex = FirstFill(bars, i + 1, lastIndex, z);
                    snap.LastBullishFvg = z;
                }
                if (bars[i].High < bars[i - 2].Low - minGap + o.TickSize * 0.5)
                {
                    var z = new FvgZone { BarIndex = i, Top = bars[i - 2].Low, Bottom = bars[i].High, Direction = -1 };
                    z.Filled = IsFilled(bars, i + 1, lastIndex, z);
                    if (z.Filled && z.FilledIndex < 0) z.FilledIndex = FirstFill(bars, i + 1, lastIndex, z);
                    snap.LastBearishFvg = z;
                }
            }
            return snap;
        }

        static bool IsSupport(LevelKind k)
        {
            switch (k)
            {
                case LevelKind.SwingLow:
                case LevelKind.SessionLow:
                case LevelKind.PrevSessionLow:
                case LevelKind.ValueAreaLow:
                case LevelKind.InitialBalanceLow:
                case LevelKind.PutWall:
                case LevelKind.GammaFlip:
                    return true;
                default: return false;
            }
        }

        static bool IsResistance(LevelKind k)
        {
            switch (k)
            {
                case LevelKind.SwingHigh:
                case LevelKind.SessionHigh:
                case LevelKind.PrevSessionHigh:
                case LevelKind.ValueAreaHigh:
                case LevelKind.InitialBalanceHigh:
                case LevelKind.CallWall:
                    return true;
                default: return false;
            }
        }

        static void ConsiderBullish(StructureSnapshot snap, int i, BarDto[] bars, double level, LevelKind kind, double tickSize)
        {
            if (snap.BullishSweep != null && snap.BullishSweep.BarIndex > i) return;
            if (snap.BullishSweep != null && snap.BullishSweep.BarIndex == i && snap.BullishSweep.LevelPrice <= level) return;
            snap.BullishSweep = new SweepEvent
            {
                BarIndex = i, BarTimeUtc = bars[i].TimeUtc, LevelPrice = level, Extreme = bars[i].Low,
                LevelKind = kind, Direction = +1, PierceTicks = Numeric.Ticks(level - bars[i].Low, tickSize)
            };
        }

        static void ConsiderBearish(StructureSnapshot snap, int i, BarDto[] bars, double level, LevelKind kind, double tickSize)
        {
            if (snap.BearishSweep != null && snap.BearishSweep.BarIndex > i) return;
            if (snap.BearishSweep != null && snap.BearishSweep.BarIndex == i && snap.BearishSweep.LevelPrice >= level) return;
            snap.BearishSweep = new SweepEvent
            {
                BarIndex = i, BarTimeUtc = bars[i].TimeUtc, LevelPrice = level, Extreme = bars[i].High,
                LevelKind = kind, Direction = -1, PierceTicks = Numeric.Ticks(bars[i].High - level, tickSize)
            };
        }

        static double LastSwing(List<SwingPoint> swings, bool high)
        {
            for (int i = swings.Count - 1; i >= 0; i--) if (swings[i].IsHigh == high) return swings[i].Price;
            return 0;
        }

        static Zone FindOrderBlock(BarDto[] bars, int bosIndex, int lookback, bool bullish)
        {
            int from = Math.Max(1, bosIndex - Math.Max(1, lookback));
            for (int i = bosIndex - 1; i >= from; i--)
            {
                bool opposing = bullish ? bars[i].Close < bars[i].Open : bars[i].Close > bars[i].Open;
                if (opposing) return new Zone { Top = bars[i].High, Bottom = bars[i].Low, BarIndex = i };
            }
            return null;
        }

        static bool IsFilled(BarDto[] bars, int from, int last, FvgZone z)
        {
            for (int i = from; i <= last; i++)
            {
                if (z.Direction > 0 && bars[i].Close < z.Bottom) return true;
                if (z.Direction < 0 && bars[i].Close > z.Top) return true;
            }
            return false;
        }

        static int FirstFill(BarDto[] bars, int from, int last, FvgZone z)
        {
            for (int i = from; i <= last; i++)
            {
                if (z.Direction > 0 && bars[i].Close < z.Bottom) return i;
                if (z.Direction < 0 && bars[i].Close > z.Top) return i;
            }
            return -1;
        }

        /// <summary>Wilder ATR over [start,end] with the given period (inclusive indices).</summary>
        public static double AverageTrueRange(BarDto[] bars, int start, int end, int period)
        {
            if (bars == null || period < 1) return 0;
            start = Math.Max(1, start); end = Math.Min(end, bars.Length - 1);
            if (end - start < 1) return 0;
            double atr = 0; int used = 0;
            for (int i = start; i <= end; i++)
            {
                double tr = TrueRange(bars, i);
                if (used < period) { atr += tr; used++; if (used == period) atr /= period; }
                else atr = (atr * (period - 1) + tr) / period;
            }
            return used == 0 ? 0 : atr;
        }

        public static double TrueRange(BarDto[] bars, int i)
        {
            if (i <= 0) return bars[Math.Max(0, i)].High - bars[Math.Max(0, i)].Low;
            double prevClose = bars[i - 1].Close;
            return Math.Max(bars[i].High - bars[i].Low,
                   Math.Max(Math.Abs(bars[i].High - prevClose), Math.Abs(bars[i].Low - prevClose)));
        }

        /// <summary>Last confirmed fractal high above / low below a price, used for target selection.</summary>
        public static double NearestSwingAbove(StructureSnapshot s, double price)
        {
            double best = double.MaxValue;
            foreach (var sw in s.Swings) if (sw.IsHigh && sw.Price > price && sw.Price < best) best = sw.Price;
            return best == double.MaxValue ? double.NaN : best;
        }

        public static double NearestSwingBelow(StructureSnapshot s, double price)
        {
            double best = double.MinValue;
            foreach (var sw in s.Swings) if (!sw.IsHigh && sw.Price < price && sw.Price > best) best = sw.Price;
            return best == double.MinValue ? double.NaN : best;
        }
    }
}
