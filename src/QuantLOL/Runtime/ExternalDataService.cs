using System;
using System.IO;
using System.Text;
using System.Threading;
using cAlgo.API;
using QuantLOL.Core;

namespace QuantLOL.Runtime
{
    /// <summary>
    /// Optional external context (GEX / VIX). Design rules enforced here:
    ///  * never called from the tick path (timer only);
    ///  * async HTTP with timeout, retry cap and exponential backoff;
    ///  * the last VALID value is kept separate from the last RECEIVED payload;
    ///  * every value carries source + timestamp + quality + age;
    ///  * a failed refresh never disables risk controls - it only marks data stale.
    /// </summary>
    public sealed class ExternalDataService
    {
        readonly Robot _bot;
        readonly Action<string, bool> _log;
        readonly int _timeoutSeconds;
        readonly object _sync = new object();

        GexSnapshot _gex;
        VixSnapshot _vix;
        DateTime _gexReceivedUtc = DateTime.MinValue;
        DateTime _vixReceivedUtc = DateTime.MinValue;
        string _gexLastError = "";
        string _vixLastError = "";
        int _gexFailures, _vixFailures;
        DateTime _gexNextAttemptUtc = DateTime.MinValue, _vixNextAttemptUtc = DateTime.MinValue;
        bool _httpInFlightGex, _httpInFlightVix;

        public bool EnableGex { get; private set; }
        public bool EnableVix { get; private set; }
        public string GexFilePath { get; private set; } = "";
        public string GexUrl { get; private set; } = "";
        public int GexMaxAgeMinutes { get; private set; } = 240;
        public string VixFilePath { get; private set; } = "";
        public string VixUrl { get; private set; } = "";
        public int VixMaxAgeMinutes { get; private set; } = 1440;

        public GexSnapshot Gex => Volatile.Read(ref _gex);
        public VixSnapshot Vix => Volatile.Read(ref _vix);
        public DateTime GexReceivedUtc => _gexReceivedUtc;
        public DateTime VixReceivedUtc => _vixReceivedUtc;
        public string GexLastError => _gexLastError;
        public string VixLastError => _vixLastError;

        public ExternalDataService(Robot bot, Action<string, bool> log, int timeoutSeconds)
        {
            _bot = bot;
            _log = log;
            _timeoutSeconds = Math.Max(1, timeoutSeconds);
        }

        public void Configure(bool gex, string gexFile, string gexUrl, int gexMaxAge,
                              bool vix, string vixFile, string vixUrl, int vixMaxAge)
        {
            EnableGex = gex; GexFilePath = gexFile ?? ""; GexUrl = gexUrl ?? ""; GexMaxAgeMinutes = gexMaxAge;
            EnableVix = vix; VixFilePath = vixFile ?? ""; VixUrl = vixUrl ?? ""; VixMaxAgeMinutes = vixMaxAge;
        }

        /// <summary>Called from OnTimer only. Never blocks: HTTP goes through the callback path.</summary>
        public void Refresh(DateTime nowUtc, bool force)
        {
            if (EnableGex)
            {
                if (!string.IsNullOrWhiteSpace(GexFilePath)) TryLoadFileGex(nowUtc);
                if (!string.IsNullOrWhiteSpace(GexUrl) && (force || nowUtc >= _gexNextAttemptUtc)) TryLoadUrlGex(nowUtc);
            }
            if (EnableVix)
            {
                if (!string.IsNullOrWhiteSpace(VixFilePath)) TryLoadFileVix(nowUtc);
                if (!string.IsNullOrWhiteSpace(VixUrl) && (force || nowUtc >= _vixNextAttemptUtc)) TryLoadUrlVix(nowUtc);
            }
        }

        /// <summary>Live mid used to sanity-check the price scale of external levels.
        /// A GLD-priced file (≈400) can never validate against XAUUSD (≈4300).</summary>
        double LiveSpot()
        {
            try
            {
                double a = _bot.Symbol.Ask, b = _bot.Symbol.Bid;
                if (a > 0 && b > 0) return (a + b) / 2.0;
            }
            catch { }
            return 2000.0;   // conservative fallback band; levels must still be within a wide gold range
        }

        void TryLoadFileGex(DateTime nowUtc)
        {
            try
            {
                if (!System.IO.File.Exists(GexFilePath)) { _gexLastError = "file not found: " + GexFilePath; return; }
                long t0 = MonotonicClock.Timestamp;
                string json = System.IO.File.ReadAllText(GexFilePath, Encoding.UTF8);
                double spot = LiveSpot();
                var g = ExternalDataParser.ParseGex(json, "XAUUSD", nowUtc, spot * 0.6, spot * 1.6, out string err);
                if (err != null) { _gexLastError = err; _log("GEX file invalid: " + err, true); return; }
                g.Source = "file:" + Path.GetFileName(GexFilePath);
                lock (_sync) { _gex = g; _gexReceivedUtc = nowUtc; _gexLastError = ""; }
            }
            catch (Exception ex) { _gexLastError = ex.Message; }
        }

        void TryLoadFileVix(DateTime nowUtc)
        {
            try
            {
                if (!System.IO.File.Exists(VixFilePath)) { _vixLastError = "file not found: " + VixFilePath; return; }
                string json = System.IO.File.ReadAllText(VixFilePath, Encoding.UTF8);
                var v = ExternalDataParser.ParseVix(json, nowUtc, out string err);
                if (err != null) { _vixLastError = err; _log("VIX file invalid: " + err, true); return; }
                v.Source = "file:" + Path.GetFileName(VixFilePath);
                lock (_sync) { _vix = v; _vixReceivedUtc = nowUtc; _vixLastError = ""; }
            }
            catch (Exception ex) { _vixLastError = ex.Message; }
        }

        void TryLoadUrlGex(DateTime nowUtc)
        {
            if (_httpInFlightGex) return;
            _httpInFlightGex = true;
            try
            {
                var req = new HttpRequest(new Uri(GexUrl))
                {
                    Method = cAlgo.API.HttpMethod.Get,
                    Timeout = TimeSpan.FromSeconds(_timeoutSeconds)
                };
                _bot.Http.SendAsync(req, resp => OnGexResponse(resp, nowUtc));
            }
            catch (Exception ex)
            {
                _httpInFlightGex = false;
                OnGexFailure("send: " + ex.Message, nowUtc);
            }
        }

        void TryLoadUrlVix(DateTime nowUtc)
        {
            if (_httpInFlightVix) return;
            _httpInFlightVix = true;
            try
            {
                var req = new HttpRequest(new Uri(VixUrl))
                {
                    Method = cAlgo.API.HttpMethod.Get,
                    Timeout = TimeSpan.FromSeconds(_timeoutSeconds)
                };
                _bot.Http.SendAsync(req, resp => OnVixResponse(resp, nowUtc));
            }
            catch (Exception ex)
            {
                _httpInFlightVix = false;
                OnVixFailure("send: " + ex.Message, nowUtc);
            }
        }

        void OnGexResponse(HttpResponse resp, DateTime requestedUtc)
        {
            _httpInFlightGex = false;
            try
            {
                if (resp == null || !resp.IsSuccessful) { OnGexFailure("http status " + (resp?.StatusCode.ToString() ?? "null"), requestedUtc); return; }
                var now = requestedUtc;
                double spot = LiveSpot();
                var g = ExternalDataParser.ParseGex(resp.Body, "XAUUSD", now, spot * 0.6, spot * 1.6, out string err);
                if (err != null) { OnGexFailure("parse: " + err, now); return; }
                g.Source = "http:" + new Uri(GexUrl).Host;
                lock (_sync) { _gex = g; _gexReceivedUtc = now; _gexLastError = ""; _gexFailures = 0; _gexNextAttemptUtc = DateTime.MinValue; }
            }
            catch (Exception ex) { OnGexFailure("callback: " + ex.Message, requestedUtc); }
        }

        void OnGexFailure(string why, DateTime nowUtc)
        {
            _gexFailures = Math.Min(_gexFailures + 1, 6);
            _gexLastError = why;
            int backoff = (int)Math.Min(600, 10 * Math.Pow(2, _gexFailures - 1));
            _gexNextAttemptUtc = nowUtc.AddSeconds(backoff);
            _log("GEX refresh failed (" + why + "); retry in " + backoff + "s; keeping last valid value if any", true);
        }

        void OnVixResponse(HttpResponse resp, DateTime requestedUtc)
        {
            _httpInFlightVix = false;
            try
            {
                if (resp == null || !resp.IsSuccessful) { OnVixFailure("http status " + (resp?.StatusCode.ToString() ?? "null"), requestedUtc); return; }
                var now = requestedUtc;
                var v = ExternalDataParser.ParseVix(resp.Body, now, out string err);
                if (err != null) { OnVixFailure("parse: " + err, now); return; }
                v.Source = "http:" + new Uri(VixUrl).Host;
                lock (_sync) { _vix = v; _vixReceivedUtc = now; _vixLastError = ""; _vixFailures = 0; _vixNextAttemptUtc = DateTime.MinValue; }
            }
            catch (Exception ex) { OnVixFailure("callback: " + ex.Message, requestedUtc); }
        }

        void OnVixFailure(string why, DateTime nowUtc)
        {
            _vixFailures = Math.Min(_vixFailures + 1, 6);
            _vixLastError = why;
            int backoff = (int)Math.Min(600, 10 * Math.Pow(2, _vixFailures - 1));
            _vixNextAttemptUtc = nowUtc.AddSeconds(backoff);
            _log("VIX refresh failed (" + why + "); retry in " + backoff + "s; keeping last valid value if any", true);
        }

        public string StatusLine(DateTime nowUtc)
        {
            var sb = new StringBuilder();
            sb.Append("gex=");
            if (!EnableGex) sb.Append("off");
            else if (Gex == null) sb.Append("none(" + (_gexLastError.Length > 0 ? _gexLastError : "not loaded") + ")");
            else sb.Append(Gex.Status(nowUtc, GexMaxAgeMinutes)).Append(' ').Append(Gex.Quality);
            sb.Append(" vix=");
            if (!EnableVix) sb.Append("off");
            else if (Vix == null) sb.Append("none(" + (_vixLastError.Length > 0 ? _vixLastError : "not loaded") + ")");
            else sb.Append(Vix.Status(nowUtc, VixMaxAgeMinutes));
            return sb.ToString();
        }

        /// <summary>
        /// Applies the stale-data policy for enabled inputs. Returns true when entries
        /// must be blocked for this bar. Missing-but-enabled data is treated as stale.
        /// </summary>
        public bool AssessAll(DateTime nowUtc, bool blockOnStalePolicy)
        {
            if (EnableGex)
            {
                var a = ExternalDataParser.Assess(Gex, nowUtc, GexMaxAgeMinutes,
                    blockOnStalePolicy ? StalePolicy.BlockEntries : StalePolicy.WarnAndIgnore, false);
                if (a.BlocksEntry) return true;
            }
            if (EnableVix)
            {
                var a = ExternalDataParser.Assess(Vix, nowUtc, VixMaxAgeMinutes,
                    blockOnStalePolicy ? StalePolicy.BlockEntries : StalePolicy.WarnAndIgnore, false);
                if (a.BlocksEntry) return true;
            }
            return false;
        }
    }
}
