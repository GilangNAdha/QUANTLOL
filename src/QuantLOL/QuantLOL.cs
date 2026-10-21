using System;
using System.Collections.Generic;
using System.Globalization;
using cAlgo.API;
using cAlgo.API.Internals;
using QuantLOL.Core;
using QuantLOL.Runtime;

namespace QuantLOL
{
    /// <summary>
    /// QuantLOL - XAUUSD SMC/AMT/GEX confluence cBot with a hard monetary risk cap.
    ///
    /// Design invariants (enforced in code, documented in docs/):
    ///  * Fixed lot size, never resized. If the structural stop does not fit the USD risk
    ///    cap, the trade is skipped - the stop is never tightened to force an entry.
    ///  * Signals are computed from CLOSED bars only; the forming bar is never read.
    ///  * All heavy work happens on bar close; the tick path only manages protection.
    ///  * External data is optional, time-stamped, quality-tagged and can never block
    ///    the risk engine.
    /// </summary>
    [Robot(AccessRights = AccessRights.None, AddIndicators = false)]
    public partial class QuantLOLBot : Robot
    {
        const string Version = "1.0.0";

        BrokerSpec _spec;
        MarketFeed _feed;
        ExecutionEngine _exec;
        DrawdownGuard _guard;
        TradingGate _gate;
        Journal _journal;
        LatencyRegistry _lat;
        ExternalDataService _ext;
        SessionSettings _sessions;

        bool _running;
        bool _monitorOnly;
        string _monitorReason = "";
        bool _slowPathBusy;
        DateTime _lastEntryTfProcessed = DateTime.MinValue;
        DateTime _lastContextTfProcessed = DateTime.MinValue;
        DateTime _lastTickUtc = DateTime.MinValue;
        DateTime _lastJournalFlushUtc = DateTime.MinValue;
        DateTime _lastGuardLogUtc = DateTime.MinValue;
        long _lastTickTimestamp;

        StructureSnapshot _entryStructure;
        StructureSnapshot _contextStructure;
        ProfileSnapshot _sessionProfile;
        ProfileSnapshot _prevSessionProfile;
        OrderFlowSnapshot _orderFlow;
        MarketRegime _regime = MarketRegime.Unknown;
        string _externalStatus = "";
        double _atrContext;
        DateTime _positionOpenUtc = DateTime.MinValue;
        double _initialStopPrice;
        bool _breakEvenDone, _partialDone;
        double _bestPriceSinceEntry;
        int _adoptedCount;
        double _sessionDayRefEquity;
        DateTime _sessionDay = DateTime.MinValue;

        readonly Dictionary<string, DateTime> _logThrottle = new Dictionary<string, DateTime>(64);
        Bars _entryBars, _contextBars;

        // ---------------------------------------------------------------- lifecycle
        protected override void OnStart()
        {
            _running = true;
            _sessions = new SessionSettings
            {
                AsiaStartHour = AsiaStartUtc, AsiaEndHour = AsiaEndUtc,
                LondonStartHour = LondonStartUtc, LondonEndHour = LondonEndUtc,
                NewYorkStartHour = NyStartUtc, NewYorkEndHour = NyEndUtc
            };

            ValidateParameters();

            _spec = BrokerSpecFactory.Build(Symbol, Account, Lots, Symbol.Ask > 0 ? Symbol.Ask : 1.0);
            if (!_spec.Verified)
            {
                _monitorOnly = RejectIfSpecUnverified;
                _monitorReason = "symbol spec unverified: " + _spec.Reason;
            }
            else if (!string.Equals(NormalizeSymbol(_spec.SymbolName), NormalizeSymbol(ExpectedSymbolName), StringComparison.OrdinalIgnoreCase))
            {
                _monitorOnly = RejectIfSpecUnverified;
                _monitorReason = "attached symbol '" + _spec.SymbolName + "' does not match expected '" + ExpectedSymbolName + "'";
            }

            _feed = new MarketFeed(this, 360, 480, 480, 40);
            bool historyOk = _feed.Init();
            if (!historyOk)
            {
                _monitorOnly = true;
                _monitorReason = "market data unavailable: " + _feed.LastError;
            }

            _lat = new LatencyRegistry();
            foreach (var s in _lat.All) s.Enabled = EnableLatencyStats;

            _exec = new ExecutionEngine(this, Log)
            {
                Spec = _spec,
                Latencies = _lat,
                OrderTimeoutMs = OrderTimeoutMs,
                MaxSlippageTicks = MaxAllowedSlippageTicks,
                Enabled = !DryRun
            };
            _exec.TradeClosed += OnTradeFinalised;
            _exec.EntryFailed += (why, reason) => RecordRejection(reason, why, CurrentBid, 0, _lastEntryStructure?.Trend ?? 0, "");

            _guard = new DrawdownGuard(Account.Balance, DailyLossLimitUsd, EquityDrawdownLimitUsd)
            {
                LatchKillSwitch = KillSwitchLatch
            };
            _gate = new TradingGate();
            _journal = new Journal
            {
                Enabled = EnableCsvJournal,
                Directory = JournalDirectory,
                Prefix = JournalPrefix
            };
            _ext = new ExternalDataService(this, Log, HttpTimeoutSeconds);
            _ext.Configure(EnableGex, GexFilePath, GexUrl, GexMaxAgeMinutes, EnableVix, VixFilePath, VixUrl, VixMaxAgeMinutes);

            Positions.Closed += OnPositionClosed;
            Positions.Opened += OnPositionOpened;
            Server.Connected += () => Log("server connected", true);
            Server.Disconnected += () => { _monitorReason = "server disconnected"; Log("server disconnected", true); };

            ReconcilePositions("startup");

            if (ExternalRefreshSeconds > 0)
            {
                Timer.Start(ExternalRefreshSeconds);
                _ext.Refresh(NowUtc, force: true);
            }

            PrintBanner();
        }

        protected override void OnStop()
        {
            _running = false;
            try { _journal.Flush(); } catch { }
            PrintSummary();
        }

        protected override void OnTick()
        {
            if (!_running) return;
            long t0 = MonotonicClock.Timestamp;
            _lastTickTimestamp = t0;
            var now = NowUtc;
            _lastTickUtc = now;

            try
            {
                _exec.CheckTimeout(now);
                SyncPosition();
                if (_exec.Position != null) ManageOpenPosition(now);
                else TrySlowPath(now, "tick");
                _guard.OnEquity(Account.Equity, now);
            }
            catch (Exception ex)
            {
                LogThrottled("tick_error", "OnTick exception: " + ex.GetType().Name + ": " + ex.Message, 60);
            }
            _lat.OnTickTotal.Record(t0, MonotonicClock.Timestamp);
        }

        protected override void OnBar()
        {
            if (!_running) return;
            TrySlowPath(NowUtc, "bar");
        }

        protected override void OnTimer()
        {
            if (!_running) return;
            try
            {
                var now = NowUtc;
                _ext.Refresh(now, force: false);
                _exec.CheckTimeout(now);
                SyncPosition();
                _guard.OnEquity(Account.Equity, now);
                if (_guard.KillSwitchLatched && _exec.Position == null)
                    LogThrottled("killswitch", "KILL SWITCH: equity drawdown limit reached; trading disabled until restart", 3600);
                if ((now - _lastJournalFlushUtc).TotalSeconds >= 15)
                {
                    _lastJournalFlushUtc = now;
                    _journal.Flush();
                }
            }
            catch (Exception ex)
            {
                LogThrottled("timer_error", "OnTimer exception: " + ex.GetType().Name + ": " + ex.Message, 60);
            }
        }

        // ---------------------------------------------------------------- slow path
        void TrySlowPath(DateTime now, string trigger)
        {
            if (_slowPathBusy) return;
            if (_feed == null) return;
            _entryBars = _feed.BarsFor(EntryTfMinutes);
            _contextBars = _feed.M15;
            if (_entryBars == null || _contextBars == null) return;
            if (_entryBars.Count < 10 || _contextBars.Count < 20) return;

            var lastClosed = _entryBars[_entryBars.Count - 2].OpenTime.ToUniversalTime();
            if (lastClosed <= _lastEntryTfProcessed) return;

            _slowPathBusy = true;
            long tStart = MonotonicClock.Timestamp;
            try
            {
                SlowPath(now, lastClosed, trigger);
            }
            catch (Exception ex)
            {
                LogThrottled("slow_error", "slow path exception: " + ex.GetType().Name + ": " + ex.Message, 30);
            }
            finally
            {
                _lastEntryTfProcessed = lastClosed;
                _slowPathBusy = false;
                _lat.SlowPathTotal.Record(tStart, MonotonicClock.Timestamp);
            }
        }

        void SlowPath(DateTime now, DateTime entryBarCloseUtc, string trigger)
        {
            _feed.RefreshAll();

            var entryIdx = _feed.EntryCountFor(EntryTfMinutes) - 1;
            var contextIdx = _feed.ContextCount - 1;
            if (entryIdx < 10 || contextIdx < 20) return;

            bool newContext = _feed.LastM15CloseUtc > _lastContextTfProcessed;
            if (newContext)
            {
                _lastContextTfProcessed = _feed.LastM15CloseUtc;
                _contextStructure = StructureAnalyzer.Analyze(_feed.M15Buf, contextIdx, BuildStructureOptions(_feed.M15Buf, contextIdx));
                _atrContext = _contextStructure.Atr;
                UpdateProfiles(now);
            }
            if (_contextStructure == null) return;

            // Context-derived levels are shared by sweep detection and target selection.
            var levels = BuildContextLevels();
            _entryStructure = StructureAnalyzer.Analyze(_feed.EntryBuffer(EntryTfMinutes), entryIdx, BuildStructureOptions(_feed.EntryBuffer(EntryTfMinutes), entryIdx), levels);

            double avgVol = OrderFlowAnalyzer.AverageVolume(_feed.M1Buf, _feed.M1Count - 1, 60);
            _orderFlow = EnableOrderFlow
                ? OrderFlowAnalyzer.Compute(_feed.M1Buf, _feed.M1Count - 1, Math.Max(10, DeltaWindowMinutes), avgVol)
                : null;

            double price = _feed.EntryBuffer(EntryTfMinutes)[entryIdx].Close;
            _regime = RegimeClassifier.Classify(price, _sessionProfile, 3);

            var targets = BuildTargets(price);
            var gex = _ext.Gex;
            var vix = _ext.Vix;
            _externalStatus = _ext.StatusLine(now);
            var staleAssessment = _ext.AssessAll(now, StalePolicy == StalePolicyOption.BlockEntries);

            var inputs = new SignalInputs
            {
                EntryBars = _feed.EntryBuffer(EntryTfMinutes),
                LastClosedIndex = entryIdx,
                EntryStructure = _entryStructure,
                ContextStructure = _contextStructure,
                SessionProfile = _sessionProfile,
                PrevSessionProfile = _prevSessionProfile,
                OrderFlow = _orderFlow,
                Gex = gex,
                Vix = vix,
                Targets = targets.ToArray(),
                TickSize = _spec?.TickSize ?? 0.01,
                EntryPrice = price,
                Ask = Symbol.Ask,
                Bid = Symbol.Bid,
                MoneyPerPriceUnit = _spec?.MoneyPerPriceUnit ?? 1.0,
                CommissionRoundTripUsd = _spec?.CommissionRoundTripUsd ?? 0,
                SlippageRoundTripUsd = (_spec?.TickSize ?? 0.01) * SlippageAllowanceTicks * (_spec?.MoneyPerPriceUnit ?? 1.0),
                Caps = new RiskCaps
                {
                    MaxRiskPerTradeUsd = MaxRiskPerTradeUsd,
                    DailyLossLimitUsd = DailyLossLimitUsd,
                    EquityDrawdownLimitUsd = EquityDrawdownLimitUsd,
                    MaxOpenPositions = MaxOpenPositions,
                    MinRewardRisk = MinRewardRisk,
                    EnforceRiskCap = EnforceRiskCap
                },
                RequireLocation = RequireLocation,
                RequireOrderFlow = RequireOrderFlow && EnableOrderFlow,
                RequireConfirmation = RequireConfirmation,
                EnableFvg = EnableFvg,
                EnableOrderBlock = EnableOrderBlock,
                EnableGex = EnableGex && gex != null && gex.Valid,
                EnableVix = EnableVix && vix != null && vix.Valid,
                AllowSyntheticTarget = TargetMode == TargetModeOption.StructuralWithFallback,
                BlockOnHighVix = BlockOnHighVix,
                VixHighThreshold = VixHighThreshold,
                SweepWindowBars = SweepWindowBars,
                ConfirmWindowBars = ConfirmWindowBars,
                LocationProximityTicks = LocationProximityTicks,
                MinScore = MinSignalScore,
                FallbackRr = FallbackRr,
                StopBufferTicks = StopBufferTicks,
                MaxSpreadTicks = MaxSpreadTicks,
                SpreadKnown = Symbol.Bid > 0 && Symbol.Ask > 0,
                Weights = new SignalWeights
                {
                    Sweep = WeightSweep, Confirmation = WeightConfirmation, Location = WeightLocation,
                    OrderFlow = WeightOrderFlow, Fvg = WeightFvg, OrderBlock = WeightOrderBlock,
                    GexConfluence = WeightGex, VixContext = WeightVix
                },
                OrderFlowEnabled = EnableOrderFlow,
                EntryTimeFrameLabel = EntryTimeFrame.ToString()
            };

            long tSignal0 = MonotonicClock.Timestamp;
            var decision = SignalEngine.Evaluate(inputs);
            long tSignal1 = MonotonicClock.Timestamp;
            _lat.TickToSignal.RecordMs(MonotonicClock.MsBetween(_lastTickTimestamp > 0 ? _lastTickTimestamp : tSignal0, tSignal1));

            if (VerboseSignalLog)
                Log("signal " + decision.Summary(), false);

            if (decision.State != SignalState.Bullish && decision.State != SignalState.Bearish)
            {
                if (LogRejections && decision.Reject != RejectReason.NoSetup)
                    RecordRejection(decision.Reject, decision.RejectDetail, price, decision.RiskUsd, decision.Direction, _externalStatus);
                return;
            }

            // ---- authorisation gates (risk first, then execution) ----
            var reject = AuthoriseEntry(now, decision, staleAssessment, out string why);
            if (reject != RejectReason.None)
            {
                if (LogRejections) RecordRejection(reject, why, price, decision.RiskUsd, decision.Direction, _externalStatus);
                return;
            }

            var plan = BuildPlan(decision, now);
            if (plan == null) return;
            plan.T0Decision = tSignal0;

            long tRisk1 = MonotonicClock.Timestamp;
            _lat.SignalToRisk.RecordMs(tSignal1, tRisk1);

            if (_monitorOnly)
            {
                Log("MONITOR-ONLY (" + _monitorReason + "): would " + decision.Summary(), false);
                return;
            }

            _gate.OnEntry(now, decision.SignalKey);
            plan.T3Submit = MonotonicClock.Timestamp;
            _lat.RiskToSubmit.RecordMs(plan.T3Submit - tRisk1);
            Log("ENTRY " + decision.Summary() + " session=" + SessionClock.Name(SessionClock.Classify(now, _sessions)) +
                " regime=" + RegimeClassifier.Name(_regime) + " ext{" + _externalStatus + "}", false);
            _exec.SubmitEntry(plan, plan.T3Submit);
        }

        RejectReason AuthoriseEntry(DateTime now, SignalDecision d, bool externalBlocks, out string why)
        {
            why = "";
            if (_monitorOnly) { why = _monitorReason; return RejectReason.SpecUnverified; }
            if (!_spec.Verified) { why = _spec.Reason; return RejectReason.SpecUnverified; }
            if (!Server.IsConnected) { why = "server disconnected"; return RejectReason.Disconnected; }
            if (!_spec.TradingEnabled) { why = "symbol trading disabled by broker"; return RejectReason.MarketClosed; }
            if (SessionClock.IsMarketClosedByCalendar(now, _sessions)) { why = "outside gold market hours"; return RejectReason.MarketClosed; }

            var session = SessionClock.Classify(now, _sessions);
            if (!SessionClock.IsSessionEnabled(session, EnableAsia, EnableLondon, EnableNewYork))
            { why = "session " + session + " disabled"; return RejectReason.MarketClosed; }

            if ((now - _lastTickUtc).TotalSeconds > MaxQuoteAgeSeconds)
            { why = "quote age " + Numeric.F((now - _lastTickUtc).TotalSeconds, 1) + "s > " + MaxQuoteAgeSeconds; return RejectReason.QuoteStale; }

            int open = CountOpenPositions();
            if (open >= MaxOpenPositions) { why = open + " position(s) open"; return RejectReason.MaxPositionsReached; }
            if (_exec.InFlight) { why = "order already in flight"; return RejectReason.DuplicateSuppressed; }
            if (_gate.IsDuplicate(d.SignalKey)) { why = "duplicate signal " + d.SignalKey; return RejectReason.DuplicateSuppressed; }
            if (!_gate.CanOpen(now, CooldownAfterTradeMinutes, CooldownAfterLossMinutes, out string cool)) { why = cool; return RejectReason.Cooldown; }

            var guard = _guard.Evaluate(Account.Equity, now);
            if (!guard.Allowed) { why = guard.Reason; return guard.Reason.Contains("daily") ? RejectReason.DailyLossLimit : RejectReason.EquityDrawdownLimit; }

            if (externalBlocks) { why = "stale external data (policy=BlockEntries)"; return RejectReason.ExternalDataRequired; }

            double drift = Math.Abs((d.Direction > 0 ? Symbol.Ask : Symbol.Bid) - d.Entry) / Math.Max(1e-12, _spec.TickSize);
            if (MaxEntryDriftTicks > 0 && drift > MaxEntryDriftTicks)
            { why = "entry drift " + Numeric.F(drift, 1) + " ticks > " + MaxEntryDriftTicks; return RejectReason.EntryDrift; }

            return RejectReason.None;
        }

        EntryPlan BuildPlan(SignalDecision d, DateTime now)
        {
            var type = d.Direction > 0 ? TradeType.Buy : TradeType.Sell;
            var session = SessionClock.Classify(now, _sessions);
            var plan = new EntryPlan
            {
                Type = type,
                Units = _spec.Units,
                DecisionPrice = d.Direction > 0 ? Symbol.Ask : Symbol.Bid,
                StopPrice = d.Stop,
                TargetPrice = d.Target,
                Label = BuildLabel(d.SignalKey),
                Comment = "QL " + d.SetupType,
                SignalKey = d.SignalKey,
                Score = d.Score,
                SetupType = d.SetupType,
                Session = SessionClock.Name(session),
                Regime = RegimeClassifier.Name(_regime),
                ExternalStatus = _externalStatus,
                Filters = BuildFiltersTag(),
                PlannedRiskUsd = d.RiskUsd,
                SpreadTicks = (Symbol.Ask - Symbol.Bid) / Math.Max(1e-12, _spec.TickSize),
                SlippageAllowanceUsd = SlippageAllowanceTicks * _spec.TickSize * _spec.MoneyPerPriceUnit,
                SyntheticTarget = d.TargetIsSynthetic,
                SignalTimeUtc = now,
                DecisionLatencyMs = 0
            };
            if (plan.DecisionPrice <= 0 || plan.Units < _spec.VolumeInUnitsMin - 1e-9)
            {
                RecordRejection(RejectReason.VolumeNotAccepted, "invalid price/volume", plan.DecisionPrice, d.RiskUsd, d.Direction, _externalStatus);
                return null;
            }
            return plan;
        }

        string BuildLabel(string key)
        {
            string k = string.IsNullOrEmpty(key) ? "sig" : key.Replace(" ", "");
            if (k.Length > 40) k = k.Substring(0, 40);
            return LabelPrefix + "-" + k;
        }

        string BuildFiltersTag()
        {
            var parts = new List<string>();
            if (EnableAmt) parts.Add("amt");
            if (EnableOrderFlow) parts.Add("of");
            if (EnableGex) parts.Add("gex");
            if (EnableVix) parts.Add("vix");
            if (EnableFvg) parts.Add("fvg");
            return string.Join("+", parts);
        }

        // ---------------------------------------------------------------- context
        StructureOptions BuildStructureOptions(BarDto[] bars, int lastIndex) => new StructureOptions
        {
            SwingLookback = SwingLookback,
            ScanBars = StructureScanBars,
            TickSize = _spec?.TickSize ?? 0.01,
            MinSweepPierceTicks = MinSweepPierceTicks,
            MinFvgTicks = MinFvgTicks,
            ObLookbackBars = 20,
            SweepWindowBars = SweepWindowBars,
            DealingRangeSwings = 4,
            FvgScanBars = 60
        };

        void UpdateProfiles(DateTime now)
        {
            if (!EnableAmt) return;
            double bin = Math.Max(1, ProfileBinTicks) * (_spec?.TickSize ?? 0.01);
            var win = SessionClock.WindowStart(now, _sessions);
            _sessionProfile = BuildWindowProfile(win, now, bin);
            var prev = SessionClock.PreviousSession(now, _sessions);
            _prevSessionProfile = BuildWindowProfile(prev.start, prev.end, bin);
            if (_sessionProfile != null && _sessionProfile.Partial)
                LogThrottled("profile_partial", "session profile is PARTIAL (insufficient M1/M5 history for " +
                    win.ToString("yyyy-MM-dd HH:mm") + "z); POC/VA still used but flagged", 3600);
        }

        /// <summary>
        /// Builds a volume profile for a UTC window. Prefers M1 (fine resolution); falls back to
        /// M5 when M1 history does not reach the window start, and marks the result Partial.
        /// </summary>
        ProfileSnapshot BuildWindowProfile(DateTime startUtc, DateTime endUtc, double bin)
        {
            var p = BuildFromSeries(_feed.M1Buf, _feed.M1Count, startUtc, endUtc, bin, 1);
            if (p != null && p.HasData && !p.Partial) return p;
            var p5 = BuildFromSeries(_feed.M5Buf, _feed.M5Count, startUtc, endUtc, bin, 5);
            if (p5 != null && p5.HasData && (!p5.Partial || p == null || !p.HasData)) return p5;
            return p ?? p5;
        }

        ProfileSnapshot BuildFromSeries(BarDto[] bars, int count, DateTime startUtc, DateTime endUtc, double bin, int minutesPerBar)
        {
            if (bars == null || count < 5) return null;
            int s = FindIndexAtOrAfter(bars, count, startUtc);
            int e = FindIndexBefore(bars, count, endUtc);
            if (s < 0 || e <= s) return null;
            int ibBars = Math.Max(1, InitialBalanceMinutes / Math.Max(1, minutesPerBar));
            var p = MarketProfileBuilder.Build(bars, s, e, bin, ValueAreaPercent, ibBars);
            p.Partial = s <= 0;
            return p;
        }

        static int FindIndexAtOrAfter(BarDto[] bars, int count, DateTime utc)
        {
            for (int i = 0; i < count; i++) if (bars[i].TimeUtc >= utc) return i;
            return -1;
        }

        static int FindIndexBefore(BarDto[] bars, int count, DateTime utc)
        {
            for (int i = count - 1; i >= 0; i--) if (bars[i].TimeUtc < utc) return i;
            return -1;
        }

        List<LevelRef> BuildContextLevels()
        {
            var levels = new List<LevelRef>(16);
            if (_sessionProfile != null && _sessionProfile.HasData)
            {
                levels.Add(new LevelRef(_sessionProfile.Val, LevelKind.ValueAreaLow));
                levels.Add(new LevelRef(_sessionProfile.Vah, LevelKind.ValueAreaHigh));
                levels.Add(new LevelRef(_sessionProfile.SessionLow, LevelKind.SessionLow));
                levels.Add(new LevelRef(_sessionProfile.SessionHigh, LevelKind.SessionHigh));
                levels.Add(new LevelRef(_sessionProfile.Poc, LevelKind.PocLow));
                if (_sessionProfile.InitialBalanceHigh > 0) levels.Add(new LevelRef(_sessionProfile.InitialBalanceHigh, LevelKind.InitialBalanceHigh));
                if (_sessionProfile.InitialBalanceLow > 0) levels.Add(new LevelRef(_sessionProfile.InitialBalanceLow, LevelKind.InitialBalanceLow));
            }
            if (_prevSessionProfile != null && _prevSessionProfile.HasData)
            {
                levels.Add(new LevelRef(_prevSessionProfile.SessionLow, LevelKind.PrevSessionLow));
                levels.Add(new LevelRef(_prevSessionProfile.SessionHigh, LevelKind.PrevSessionHigh));
            }
            if (EnableGex && _ext?.Gex != null && _ext.Gex.Valid)
            {
                foreach (var l in _ext.Gex.Levels) levels.Add(l);
            }
            return levels;
        }

        List<LevelRef> BuildTargets(double price)
        {
            var t = new List<LevelRef>(24);
            if (_contextStructure != null)
            {
                if (Numeric.IsFinite(_contextStructure.DealingHigh)) t.Add(new LevelRef(_contextStructure.DealingHigh, LevelKind.SwingHigh, "dealing_high"));
                if (Numeric.IsFinite(_contextStructure.DealingLow)) t.Add(new LevelRef(_contextStructure.DealingLow, LevelKind.SwingLow, "dealing_low"));
                foreach (var sw in _contextStructure.Swings)
                {
                    if (sw.ConfirmIndex > _feed.ContextCount - 1) continue;
                    t.Add(new LevelRef(sw.Price, sw.IsHigh ? LevelKind.SwingHigh : LevelKind.SwingLow, "swing"));
                }
            }
            if (_sessionProfile != null && _sessionProfile.HasData)
            {
                t.Add(new LevelRef(_sessionProfile.SessionHigh, LevelKind.SessionHigh, "session_high"));
                t.Add(new LevelRef(_sessionProfile.SessionLow, LevelKind.SessionLow, "session_low"));
                t.Add(new LevelRef(_sessionProfile.Vah, LevelKind.ValueAreaHigh, "vah"));
                t.Add(new LevelRef(_sessionProfile.Val, LevelKind.ValueAreaLow, "val"));
                t.Add(new LevelRef(_sessionProfile.Poc, LevelKind.PocHigh, "poc"));
            }
            if (_prevSessionProfile != null && _prevSessionProfile.HasData)
            {
                t.Add(new LevelRef(_prevSessionProfile.SessionHigh, LevelKind.PrevSessionHigh, "pdh"));
                t.Add(new LevelRef(_prevSessionProfile.SessionLow, LevelKind.PrevSessionLow, "pdl"));
                t.Add(new LevelRef(_prevSessionProfile.Poc, LevelKind.PocHigh, "pd_poc"));
            }
            if (EnableGex && _ext?.Gex != null && _ext.Gex.Valid)
                foreach (var l in _ext.Gex.Levels) t.Add(l);
            return t;
        }

        // ---------------------------------------------------------------- position mgmt
        void SyncPosition()
        {
            Position mine = null;
            foreach (var p in Positions)
            {
                if (!string.Equals(p.SymbolName, SymbolName, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Label != null && p.Label.StartsWith(LabelPrefix + "-", StringComparison.Ordinal))
                {
                    if (mine == null || p.Id > mine.Id) mine = p;
                }
            }
            if (mine == null && _exec.Position != null)
            {
                _exec.BindPosition(null);
                _positionOpenUtc = DateTime.MinValue;
                return;
            }
            if (mine != null && (_exec.Position == null || _exec.Position.Id != mine.Id))
            {
                _exec.BindPosition(mine);
                if (_positionOpenUtc == DateTime.MinValue) _positionOpenUtc = mine.EntryTime.ToUniversalTime();
                if (_initialStopPrice <= 0) _initialStopPrice = mine.StopLoss ?? 0;
                _bestPriceSinceEntry = mine.EntryPrice;
                EnsureProtectiveStop(mine);
            }
        }

        void EnsureProtectiveStop(Position p)
        {
            if (p.StopLoss.HasValue) return;
            double maxDist = RiskMath.MaxStopDistanceForCap(
                new RiskCaps { MaxRiskPerTradeUsd = MaxRiskPerTradeUsd },
                _spec.MoneyPerPriceUnit, _spec.CommissionRoundTripUsd, SlippageAllowanceTicks * _spec.TickSize * _spec.MoneyPerPriceUnit);
            if (maxDist <= 0) return;
            double sl = p.TradeType == TradeType.Buy ? p.EntryPrice - maxDist : p.EntryPrice + maxDist;
            try
            {
                var r = ModifyPosition(p, Numeric.RoundToTick(sl, _spec.TickSize), p.TakeProfit, ProtectionType.Absolute);
                Log("EMERGENCY protective stop set on #" + p.Id + " at " + Numeric.F(sl, 3) + " (position had no SL); result=" + (r?.IsSuccessful.ToString() ?? "null"), true);
            }
            catch (Exception ex) { Log("emergency stop failed: " + ex.Message, true); }
        }

        void ManageOpenPosition(DateTime now)
        {
            var p = _exec.Position;
            if (p == null) return;
            double pnl = _exec.TrackExcursion();
            double entry = p.EntryPrice;
            double riskDist = Math.Abs(entry - (_initialStopPrice > 0 ? _initialStopPrice : p.StopLoss ?? entry));
            if (riskDist <= 0) return;
            double price = p.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;
            if (p.TradeType == TradeType.Buy) _bestPriceSinceEntry = Math.Max(_bestPriceSinceEntry, price);
            else _bestPriceSinceEntry = Math.Min(_bestPriceSinceEntry, price);
            double r = p.TradeType == TradeType.Buy ? (price - entry) / riskDist : (entry - price) / riskDist;

            if (TimeStopMinutes > 0 && !_breakEvenDone && _positionOpenUtc != DateTime.MinValue &&
                (now - _positionOpenUtc).TotalMinutes >= TimeStopMinutes)
            {
                _exec.ClosePosition("time_stop", Numeric.F((now - _positionOpenUtc).TotalMinutes, 0) + "min");
                return;
            }

            if (PartialExitEnabled && !_partialDone && r >= PartialAtR)
            {
                double closeVol = Math.Round(p.VolumeInUnits * PartialFraction / _spec.VolumeInUnitsStep) * _spec.VolumeInUnitsStep;
                if (closeVol >= _spec.VolumeInUnitsMin && p.VolumeInUnits - closeVol >= _spec.VolumeInUnitsMin)
                {
                    var res = ClosePosition(p, (long)closeVol);
                    if (res != null && res.IsSuccessful) { _partialDone = true; Log("partial exit " + Numeric.F(closeVol, 2) + " units at " + Numeric.F(r, 2) + "R", false); }
                }
                else
                {
                    PartialExitEnabled = false;
                    Log("partial exit disabled: cannot split " + Numeric.F(p.VolumeInUnits, 2) + " units with step " + Numeric.F(_spec.VolumeInUnitsStep, 2), true);
                }
            }

            if (BreakEvenAtR > 0 && !_breakEvenDone && r >= BreakEvenAtR)
            {
                double be = p.TradeType == TradeType.Buy ? entry + _spec.TickSize * 2 : entry - _spec.TickSize * 2;
                if (TryModifyStop(p, be)) { _breakEvenDone = true; Log("break-even set at " + Numeric.F(be, 3) + " (" + Numeric.F(r, 2) + "R)", false); }
            }

            if (TrailStartR > 0 && r >= TrailStartR && _atrContext > 0)
            {
                double trailDist = _atrContext * TrailAtrMultiple;
                double candidate = p.TradeType == TradeType.Buy ? _bestPriceSinceEntry - trailDist : _bestPriceSinceEntry + trailDist;
                double current = p.StopLoss ?? (_initialStopPrice > 0 ? _initialStopPrice : entry);
                bool improvement = p.TradeType == TradeType.Buy ? candidate > current + _spec.TickSize * 0.5 : candidate < current - _spec.TickSize * 0.5;
                if (improvement) TryModifyStop(p, Numeric.RoundToTick(candidate, _spec.TickSize));
            }
        }

        bool TryModifyStop(Position p, double newStop)
        {
            try
            {
                var r = ModifyPosition(p, newStop, p.TakeProfit, ProtectionType.Absolute);
                if (r == null || !r.IsSuccessful)
                {
                    LogThrottled("modify_fail", "stop modify failed: " + (r?.Error?.ToString() ?? "null"), 120);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                LogThrottled("modify_ex", "stop modify exception: " + ex.Message, 120);
                return false;
            }
        }

        // ---------------------------------------------------------------- events
        void OnPositionOpened(PositionOpenedEventArgs args)
        {
            var p = args.Position;
            if (!string.Equals(p.SymbolName, SymbolName, StringComparison.OrdinalIgnoreCase)) return;
            if (p.Label != null && p.Label.StartsWith(LabelPrefix + "-", StringComparison.Ordinal)) return; // ours, handled at submission
            Log("note: external/manual position #" + p.Id + " on " + p.SymbolName + " detected; it counts toward the position limit and is not managed", true);
        }

        void OnPositionClosed(PositionClosedEventArgs args)
        {
            var pos = args.Position;
            if (!string.Equals(pos.SymbolName, SymbolName, StringComparison.OrdinalIgnoreCase)) return;
            HistoricalTrade hist = null;
            try
            {
                var all = History.FindByPositionId(pos.Id);
                if (all != null) foreach (var h in all) if (hist == null || h.ClosingTime > hist.ClosingTime) hist = h;
            }
            catch (Exception ex) { LogThrottled("hist_fail", "history lookup failed: " + ex.Message, 120); }
            _exec.OnPositionClosed(pos, args.Reason, hist);
        }

        void OnTradeFinalised(TradeRecord t)
        {
            var now = NowUtc;
            _guard.OnTradeClosed(t.NetPnl, now, Account.Equity);
            _gate.OnExit(now, t.NetPnl);
            _journal.AddTrade(t);
            _positionOpenUtc = DateTime.MinValue;
            _initialStopPrice = 0;
            _breakEvenDone = false;
            _partialDone = false;
            string msg = "RESULT " + t.Direction + " #" + t.Id + " net=" + Numeric.F(t.NetPnl, 2) +
                         " R=" + Numeric.F(t.RMultiple, 2) + " exit=" + t.ExitReason +
                         " session=" + t.Session + " regime=" + t.Regime +
                         " dailyPnl=" + Numeric.F(_guard.DailyRealizedPnl, 2) + "/-" + Numeric.F(DailyLossLimitUsd, 2) +
                         " equity=" + Numeric.F(Account.Equity, 2);
            Log(msg, false);
        }

        void ReconcilePositions(string context)
        {
            int adopted = 0;
            int foreign = 0;
            foreach (var p in Positions)
            {
                if (!string.Equals(p.SymbolName, SymbolName, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Label != null && p.Label.StartsWith(LabelPrefix + "-", StringComparison.Ordinal))
                {
                    _exec.BindPosition(p);
                    _positionOpenUtc = p.EntryTime.ToUniversalTime();
                    _initialStopPrice = p.StopLoss ?? 0;
                    _bestPriceSinceEntry = p.EntryPrice;
                    EnsureProtectiveStop(p);
                    adopted++;
                }
                else foreign++;
            }
            _adoptedCount = adopted;
            if (adopted > 0 || foreign > 0)
                Log("reconcile(" + context + "): adopted=" + adopted + " foreign=" + foreign +
                    " (foreign positions are never modified; they still count toward MaxOpenPositions)", true);
        }

        int CountOpenPositions()
        {
            int n = 0;
            foreach (var p in Positions) if (string.Equals(p.SymbolName, SymbolName, StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }

        // ---------------------------------------------------------------- helpers
        int EntryTfMinutes
        {
            get
            {
                switch (EntryTimeFrame)
                {
                    case EntryTimeFrameOption.M1: return 1;
                    case EntryTimeFrameOption.M3: return 3;
                    case EntryTimeFrameOption.M15: return 15;
                    default: return 5;
                }
            }
        }

        DateTime NowUtc
        {
            get
            {
                try { return Server.TimeInUtc; }
                catch { return DateTime.UtcNow; }
            }
        }

        double CurrentBid => Symbol.Bid;

        void RecordRejection(RejectReason reason, string detail, double price, double risk, int direction, string extStatus)
        {
            if (!LogRejections) return;
            if (reason == RejectReason.None) return;
            _journal.AddReject(new RejectedSetupRecord
            {
                TimeUtc = NowUtc,
                Reason = reason,
                Detail = detail ?? "",
                Direction = direction > 0 ? "LONG" : direction < 0 ? "SHORT" : "",
                Price = price,
                PlannedRiskUsd = risk,
                Session = SessionClock.Name(SessionClock.Classify(NowUtc, _sessions)),
                Score = 0
            });
            LogThrottled("reject_" + reason, "rejected[" + reason + "] " + detail + " @" + Numeric.F(price, 2) +
                          " session=" + SessionClock.Name(SessionClock.Classify(NowUtc, _sessions)) + " ext{" + extStatus + "}", 30);
        }

        void ValidateParameters()
        {
            if (Lots <= 0) { Log("INVALID: Lots must be > 0; forcing 0.01", true); Lots = 0.01; }
            if (MaxRiskPerTradeUsd <= 0) { Log("INVALID: MaxRiskPerTradeUsd must be > 0; forcing 5", true); MaxRiskPerTradeUsd = 5; }
            if (DailyLossLimitUsd <= 0) DailyLossLimitUsd = 15;
            if (EquityDrawdownLimitUsd <= 0) EquityDrawdownLimitUsd = 30;
            if (MaxOpenPositions < 1) MaxOpenPositions = 1;
            if (SweepWindowBars < 1) SweepWindowBars = 1;
            if (ConfirmWindowBars < 1) ConfirmWindowBars = 1;
            if (MinSweepPierceTicks < 0) MinSweepPierceTicks = 0;
            if (MaxSpreadTicks <= 0) MaxSpreadTicks = 40;
            if (StructureScanBars < 80) StructureScanBars = 80;
            if (EntryTimeFrame == EntryTimeFrameOption.M15 && StructureScanBars > 320) StructureScanBars = 320;
            if (AsiaEndUtc <= AsiaStartUtc || LondonEndUtc <= LondonStartUtc || NyEndUtc <= NyStartUtc)
                Log("INVALID session hours: end must be after start; sessions may never trigger", true);
        }

        void PrintBanner()
        {
            var spec = _spec?.Describe() ?? "spec unavailable";
            Log("================================================================", false);
            Log("QuantLOL v" + Version + " XAUUSD SMC/AMT/GEX bot starting", false);
            Log("mode=" + (IsBacktesting ? "BACKTEST(" + RunningMode + ")" : "LIVE/DEMO") +
                " symbol=" + SymbolName + " timeframe=" + TimeFrame + " entryTF=" + EntryTimeFrame, false);
            Log("account: balance=" + Numeric.F(Account.Balance, 2) + " " + Account.Asset?.Name +
                " equity=" + Numeric.F(Account.Equity, 2) + " leverage=" + Account.PreciseLeverage +
                " type=" + Account.AccountType, false);
            Log(spec, false);
            Log("volume resolution: " + Numeric.F(Lots, 4) + " lot = " + Numeric.F(_spec?.Units ?? 0, 4) + " units" +
                " (broker min " + Numeric.F(_spec?.VolumeInUnitsMin ?? 0, 4) + ", step " + Numeric.F(_spec?.VolumeInUnitsStep ?? 0, 4) + ")", false);
            Log("money-per-price-unit=" + Numeric.F(_spec?.MoneyPerPriceUnit ?? 0, 6) +
                " (contract cross-check " + Numeric.F(_spec?.BrokerAmountRiskedPerPrice ?? 0, 6) + ")", false);
            double maxStop = RiskMath.MaxStopDistanceForCap(
                new RiskCaps { MaxRiskPerTradeUsd = MaxRiskPerTradeUsd }, _spec?.MoneyPerPriceUnit ?? 1,
                _spec?.CommissionRoundTripUsd ?? 0, SlippageAllowanceTicks * (_spec?.TickSize ?? 0.01) * (_spec?.MoneyPerPriceUnit ?? 1));
            Log("risk cap: " + Numeric.F(MaxRiskPerTradeUsd, 2) + " USD implies a maximum structural stop distance of " +
                Numeric.F(maxStop, 3) + " price units (" + Numeric.F(maxStop / Math.Max(1e-12, _spec?.TickSize ?? 0.01), 0) + " ticks) at this volume", false);
            Log("limits: daily loss " + Numeric.F(DailyLossLimitUsd, 2) + ", equity drawdown " + Numeric.F(EquityDrawdownLimitUsd, 2) +
                ", max positions " + MaxOpenPositions + ", spread<= " + MaxSpreadTicks + " ticks", false);
            Log("history: m1=" + _feed.M1Count + " m5=" + _feed.M5Count + " m15=" + _feed.M15Count + " d1=" + _feed.D1Count +
                " loadMore=" + _feed.LoadMoreCalls, false);
            Log("external: " + _ext.StatusLine(NowUtc), false);
            if (_monitorOnly) Log("MONITOR-ONLY MODE: " + _monitorReason, true);
            Log("================================================================", false);
        }

        void PrintSummary()
        {
            try
            {
                Log("================ SHUTDOWN SUMMARY ================", false);
                Log("submits=" + _exec.SubmitCount + " brokerRejects=" + _exec.RejectCount + " timeouts=" + _exec.TimeoutCount +
                    " adopted=" + _adoptedCount + " journalTrades=" + _journal.TradesWritten + " journalRejects=" + _journal.RejectsWritten, false);
                if (EnableLatencyStats) Print(_lat.RenderTable());
                var trades = _journal.Trades;
                if (trades.Count > 0)
                {
                    var m = PerformanceAnalyzer.Compute(trades);
                    Print(PerformanceAnalyzer.RenderMarkdown(m, "Bot session performance", "closed trades this session; small samples are not evidence of an edge"));
                }
                else Log("no closed trades this session", false);
                if (!string.IsNullOrEmpty(_journal.LastError)) Log("journal error: " + _journal.LastError, true);
                Log("=================================================", false);
            }
            catch (Exception ex)
            {
                Print("summary failed: " + ex.Message);
            }
        }

        void Log(string message, bool warn)
        {
            try { Print((warn ? "[WARN] " : "[QL] ") + message); } catch { }
        }

        void LogThrottled(string key, string message, int seconds)
        {
            var now = NowUtc;
            if (_logThrottle.TryGetValue(key, out var last) && (now - last).TotalSeconds < seconds) return;
            if (_logThrottle.Count > 200) _logThrottle.Clear();
            _logThrottle[key] = now;
            Log(message, true);
        }

        static string NormalizeSymbol(string s) => (s ?? "").Replace("/", "").Replace(".", "").Trim().ToUpperInvariant();
    }
}
