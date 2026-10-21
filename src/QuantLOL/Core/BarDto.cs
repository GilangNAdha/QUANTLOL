using System;

namespace QuantLOL.Core
{
    /// <summary>
    /// Immutable closed-bar value type used by every pure calculation.
    /// TimeUtc is always the bar OPEN time in UTC. Volume is broker tick volume
    /// (number of price updates), never exchange volume.
    /// </summary>
    public readonly struct BarDto
    {
        public readonly DateTime TimeUtc;
        public readonly double Open;
        public readonly double High;
        public readonly double Low;
        public readonly double Close;
        public readonly double Volume;

        public BarDto(DateTime timeUtc, double open, double high, double low, double close, double volume)
        {
            TimeUtc = timeUtc; Open = open; High = high; Low = low; Close = close; Volume = volume;
        }

        public double Range => High - Low;
        public bool IsBull => Close >= Open;

        public override string ToString()
            => TimeUtc.ToString("yyyy-MM-dd HH:mm") + " O" + Open + " H" + High + " L" + Low + " C" + Close + " V" + Volume;
    }

    /// <summary>Level kinds used for sweep detection and target selection. Ordered for logging.</summary>
    public enum LevelKind
    {
        SwingLow = 0,
        SwingHigh = 1,
        SessionLow = 2,
        SessionHigh = 3,
        PrevSessionLow = 4,
        PrevSessionHigh = 5,
        PocHigh = 6,
        PocLow = 7,
        ValueAreaHigh = 8,
        ValueAreaLow = 9,
        InitialBalanceHigh = 10,
        InitialBalanceLow = 11,
        GexLevel = 12,
        GammaFlip = 13,
        CallWall = 14,
        PutWall = 15,
        RoundNumber = 16
    }

    public readonly struct LevelRef
    {
        public readonly double Price;
        public readonly LevelKind Kind;
        public readonly string Tag;
        public LevelRef(double price, LevelKind kind, string tag = null) { Price = price; Kind = kind; Tag = tag; }
        public override string ToString() => $"{Kind}@{Price:0.######}";
    }
}
