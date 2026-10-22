using System;

namespace QuantLOL.Core
{
    public enum SessionKind { OffHours = 0, Asia = 1, London = 2, NewYork = 3 }

    /// <summary>
    /// Session boundaries are defined in UTC and are fully configurable.
    /// Default windows approximate the XAUUSD liquidity cycle:
    ///   Asia   00:00-07:00 UTC
    ///   London 07:00-12:00 UTC
    ///   New York 12:00-21:00 UTC
    /// The bot never guesses the broker's timezone: all comparisons use TimeInUtc.
    /// </summary>
    public sealed class SessionSettings
    {
        public int AsiaStartHour = 0, AsiaEndHour = 7;
        public int LondonStartHour = 7, LondonEndHour = 12;
        public int NewYorkStartHour = 12, NewYorkEndHour = 21;
        public int FridayCloseHourUtc = 21;
        public int SundayOpenHourUtc = 22;

        public static SessionSettings Default => new SessionSettings();
    }

    public static class SessionClock
    {
        public static SessionKind Classify(DateTime utc, SessionSettings s)
        {
            s = s ?? SessionSettings.Default;
            int h = utc.Hour;
            if (h >= s.AsiaStartHour && h < s.AsiaEndHour) return SessionKind.Asia;
            if (h >= s.LondonStartHour && h < s.LondonEndHour) return SessionKind.London;
            if (h >= s.NewYorkStartHour && h < s.NewYorkEndHour) return SessionKind.NewYork;
            return SessionKind.OffHours;
        }

        public static string Name(SessionKind k)
        {
            switch (k)
            {
                case SessionKind.Asia: return "Asia";
                case SessionKind.London: return "London";
                case SessionKind.NewYork: return "NewYork";
                default: return "OffHours";
            }
        }

        /// <summary>Start of the current session window (UTC). For OffHours the start of the current UTC hour.</summary>
        public static DateTime WindowStart(DateTime utc, SessionSettings s)
        {
            s = s ?? SessionSettings.Default;
            var day = utc.Date;
            var k = Classify(utc, s);
            switch (k)
            {
                case SessionKind.Asia: return day.AddHours(s.AsiaStartHour);
                case SessionKind.London: return day.AddHours(s.LondonStartHour);
                case SessionKind.NewYork: return day.AddHours(s.NewYorkStartHour);
                default: return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
            }
        }

        /// <summary>Previous *completed* session window (used for previous-session value areas).</summary>
        public static (DateTime start, DateTime end) PreviousSession(DateTime utc, SessionSettings s)
        {
            s = s ?? SessionSettings.Default;
            var day = utc.Date;
            var k = Classify(utc, s);
            switch (k)
            {
                case SessionKind.Asia:
                    return (day.AddDays(-1).AddHours(s.NewYorkStartHour), day.AddDays(-1).AddHours(s.NewYorkEndHour));
                case SessionKind.London:
                    return (day.AddHours(s.AsiaStartHour), day.AddHours(s.AsiaEndHour));
                case SessionKind.NewYork:
                    return (day.AddHours(s.LondonStartHour), day.AddHours(s.LondonEndHour));
                default:
                    // Before Asia: previous day's New York; after NY: today's NY.
                    return utc.Hour < s.AsiaStartHour
                        ? (day.AddDays(-1).AddHours(s.NewYorkStartHour), day.AddDays(-1).AddHours(s.NewYorkEndHour))
                        : (day.AddHours(s.NewYorkStartHour), day.AddHours(s.NewYorkEndHour));
            }
        }

        /// <summary>Gold market-hours approximation. Broker holidays are not modelled; broker order errors are handled instead.</summary>
        public static bool IsMarketClosedByCalendar(DateTime utc, SessionSettings s)
        {
            s = s ?? SessionSettings.Default;
            if (utc.DayOfWeek == DayOfWeek.Saturday) return true;
            if (utc.DayOfWeek == DayOfWeek.Sunday && utc.Hour < s.SundayOpenHourUtc) return true;
            if (utc.DayOfWeek == DayOfWeek.Friday && utc.Hour >= s.FridayCloseHourUtc) return true;
            return false;
        }

        public static bool IsSessionEnabled(SessionKind k, bool asia, bool london, bool ny)
        {
            switch (k)
            {
                case SessionKind.Asia: return asia;
                case SessionKind.London: return london;
                case SessionKind.NewYork: return ny;
                default: return false;
            }
        }
    }
}
