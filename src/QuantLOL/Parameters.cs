using cAlgo.API;

namespace QuantLOL
{
    public enum EntryTimeFrameOption { M1 = 0, M3 = 1, M5 = 2, M15 = 3 }
    public enum TargetModeOption { StructuralWithFallback = 0, StructuralOnly = 1 }
    public enum StalePolicyOption { BlockEntries = 0, WarnAndIgnore = 1 }
    public enum LogVerbosityOption { Minimal = 0, Normal = 1, Verbose = 2 }

    public partial class QuantLOLBot : Robot
    {
        // ------------------------------------------------------------------ identity
        [Parameter("Expected symbol", DefaultValue = "XAUUSD", Group = "1. Identity",
            Description = "The bot refuses to trade if the attached symbol name does not match (case-insensitive).")]
        public string ExpectedSymbolName { get; set; }

        [Parameter("Entry timeframe", DefaultValue = EntryTimeFrameOption.M5, Group = "1. Identity",
            Description = "Timeframe used for the setup/confirmation. Context always uses M15.")]
        public EntryTimeFrameOption EntryTimeFrame { get; set; }

        [Parameter("Order label prefix", DefaultValue = "QL", Group = "1. Identity",
            Description = "All orders are labelled PREFIX-signalKey so they can be reconciled after a restart.")]
        public string LabelPrefix { get; set; }

        [Parameter("Log verbosity", DefaultValue = LogVerbosityOption.Normal, Group = "1. Identity")]
        public LogVerbosityOption LogVerbosity { get; set; }

        [Parameter("Dry run (no orders)", DefaultValue = false, Group = "1. Identity",
            Description = "Compute and log everything but never submit an order.")]
        public bool DryRun { get; set; }

        // ------------------------------------------------------------------ risk
        [Parameter("Lots (fixed)", DefaultValue = 0.01, MinValue = 0.01, MaxValue = 10.0, Step = 0.01, Group = "2. Risk",
            Description = "Fixed volume in lots. Never resized by the bot. 0.01 lot is converted to broker volume units and verified at start.")]
        public double Lots { get; set; }

        [Parameter("Max planned risk / trade (USD)", DefaultValue = 5.0, MinValue = 0.5, MaxValue = 1000.0, Step = 0.5, Group = "2. Risk")]
        public double MaxRiskPerTradeUsd { get; set; }

        [Parameter("Daily loss limit (USD)", DefaultValue = 15.0, MinValue = 1.0, MaxValue = 10000.0, Step = 1.0, Group = "2. Risk")]
        public double DailyLossLimitUsd { get; set; }

        [Parameter("Equity drawdown limit (USD)", DefaultValue = 30.0, MinValue = 1.0, MaxValue = 10000.0, Step = 1.0, Group = "2. Risk")]
        public double EquityDrawdownLimitUsd { get; set; }

        [Parameter("Max open positions (symbol)", DefaultValue = 1, MinValue = 1, MaxValue = 5, Group = "2. Risk")]
        public int MaxOpenPositions { get; set; }

        [Parameter("Enforce per-trade risk cap", DefaultValue = true, Group = "2. Risk")]
        public bool EnforceRiskCap { get; set; }

        [Parameter("Min net reward:risk", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 20, Step = 0.1, Group = "2. Risk",
            Description = "Minimum reward-to-risk AFTER estimated commission and slippage.")]
        public double MinRewardRisk { get; set; }

        [Parameter("Refuse to trade if spec unverified", DefaultValue = true, Group = "2. Risk")]
        public bool RejectIfSpecUnverified { get; set; }

        [Parameter("Cool-down after trade (min)", DefaultValue = 5, MinValue = 0, MaxValue = 1440, Group = "2. Risk")]
        public int CooldownAfterTradeMinutes { get; set; }

        [Parameter("Cool-down after loss (min)", DefaultValue = 15, MinValue = 0, MaxValue = 1440, Group = "2. Risk")]
        public int CooldownAfterLossMinutes { get; set; }

        [Parameter("Latch kill switch on drawdown", DefaultValue = true, Group = "2. Risk",
            Description = "When the lifetime equity drawdown limit is hit, stop trading until the bot is restarted.")]
        public bool KillSwitchLatch { get; set; }

        // ------------------------------------------------------------------ SMC
        [Parameter("Swing lookback (bars)", DefaultValue = 2, MinValue = 1, MaxValue = 10, Group = "3. SMC")]
        public int SwingLookback { get; set; }

        [Parameter("Structure scan bars", DefaultValue = 320, MinValue = 80, MaxValue = 1000, Group = "3. SMC")]
        public int StructureScanBars { get; set; }

        [Parameter("Min sweep pierce (ticks)", DefaultValue = 2, MinValue = 0, MaxValue = 200, Group = "3. SMC")]
        public int MinSweepPierceTicks { get; set; }

        [Parameter("Sweep window (bars)", DefaultValue = 12, MinValue = 1, MaxValue = 100, Group = "3. SMC")]
        public int SweepWindowBars { get; set; }

        [Parameter("Confirmation window (bars)", DefaultValue = 12, MinValue = 1, MaxValue = 100, Group = "3. SMC")]
        public int ConfirmWindowBars { get; set; }

        [Parameter("Min FVG (ticks)", DefaultValue = 10, MinValue = 1, MaxValue = 500, Group = "3. SMC")]
        public int MinFvgTicks { get; set; }

        [Parameter("Require FVG confluence", DefaultValue = true, Group = "3. SMC")]
        public bool EnableFvg { get; set; }

        [Parameter("Require order-block confluence", DefaultValue = true, Group = "3. SMC")]
        public bool EnableOrderBlock { get; set; }

        [Parameter("Require structure confirmation", DefaultValue = true, Group = "3. SMC")]
        public bool RequireConfirmation { get; set; }

        [Parameter("Require location filter", DefaultValue = true, Group = "3. SMC")]
        public bool RequireLocation { get; set; }

        [Parameter("Location proximity (ticks)", DefaultValue = 60, MinValue = 1, MaxValue = 5000, Group = "3. SMC")]
        public int LocationProximityTicks { get; set; }

        [Parameter("Minimum signal score", DefaultValue = 4.0, MinValue = 0, MaxValue = 50, Step = 0.5, Group = "3. SMC")]
        public double MinSignalScore { get; set; }

        [Parameter("Weight: sweep", DefaultValue = 2.0, Group = "3.1 Score weights")]
        public double WeightSweep { get; set; }
        [Parameter("Weight: confirmation", DefaultValue = 2.0, Group = "3.1 Score weights")]
        public double WeightConfirmation { get; set; }
        [Parameter("Weight: location", DefaultValue = 1.0, Group = "3.1 Score weights")]
        public double WeightLocation { get; set; }
        [Parameter("Weight: order flow", DefaultValue = 1.0, Group = "3.1 Score weights")]
        public double WeightOrderFlow { get; set; }
        [Parameter("Weight: FVG", DefaultValue = 0.5, Group = "3.1 Score weights")]
        public double WeightFvg { get; set; }
        [Parameter("Weight: order block", DefaultValue = 0.5, Group = "3.1 Score weights")]
        public double WeightOrderBlock { get; set; }
        [Parameter("Weight: GEX confluence", DefaultValue = 1.0, Group = "3.1 Score weights")]
        public double WeightGex { get; set; }
        [Parameter("Weight: VIX context", DefaultValue = 0.5, Group = "3.1 Score weights")]
        public double WeightVix { get; set; }

        [Parameter("Fallback R:R for synthetic target", DefaultValue = 2.0, MinValue = 1.0, MaxValue = 10.0, Step = 0.1, Group = "3. SMC")]
        public double FallbackRr { get; set; }

        [Parameter("Target mode", DefaultValue = TargetModeOption.StructuralWithFallback, Group = "3. SMC")]
        public TargetModeOption TargetMode { get; set; }

        // ------------------------------------------------------------------ AMT
        [Parameter("Enable AMT profile", DefaultValue = true, Group = "4. AMT")]
        public bool EnableAmt { get; set; }

        [Parameter("Profile bin (ticks)", DefaultValue = 10, MinValue = 1, MaxValue = 500, Group = "4. AMT")]
        public int ProfileBinTicks { get; set; }

        [Parameter("Value area (%)", DefaultValue = 70.0, MinValue = 50, MaxValue = 95, Step = 1, Group = "4. AMT")]
        public double ValueAreaPercent { get; set; }

        [Parameter("Initial balance (minutes)", DefaultValue = 60, MinValue = 15, MaxValue = 240, Group = "4. AMT")]
        public int InitialBalanceMinutes { get; set; }

        [Parameter("Require AMT location", DefaultValue = false, Group = "4. AMT")]
        public bool RequireAmtLocation { get; set; }

        // ------------------------------------------------------------------ order flow
        [Parameter("Enable order-flow proxy", DefaultValue = true, Group = "5. Order flow")]
        public bool EnableOrderFlow { get; set; }

        [Parameter("Require order-flow confirmation", DefaultValue = true, Group = "5. Order flow")]
        public bool RequireOrderFlow { get; set; }

        [Parameter("Delta window (minutes)", DefaultValue = 60, MinValue = 5, MaxValue = 480, Group = "5. Order flow")]
        public int DeltaWindowMinutes { get; set; }

        // ------------------------------------------------------------------ sessions
        [Parameter("Trade Asia (UTC)", DefaultValue = true, Group = "6. Sessions")]
        public bool EnableAsia { get; set; }
        [Parameter("Trade London (UTC)", DefaultValue = true, Group = "6. Sessions")]
        public bool EnableLondon { get; set; }
        [Parameter("Trade New York (UTC)", DefaultValue = true, Group = "6. Sessions")]
        public bool EnableNewYork { get; set; }
        [Parameter("Asia start hour UTC", DefaultValue = 0, MinValue = 0, MaxValue = 23, Group = "6. Sessions")]
        public int AsiaStartUtc { get; set; }
        [Parameter("Asia end hour UTC", DefaultValue = 7, MinValue = 1, MaxValue = 24, Group = "6. Sessions")]
        public int AsiaEndUtc { get; set; }
        [Parameter("London start hour UTC", DefaultValue = 7, MinValue = 0, MaxValue = 23, Group = "6. Sessions")]
        public int LondonStartUtc { get; set; }
        [Parameter("London end hour UTC", DefaultValue = 12, MinValue = 1, MaxValue = 24, Group = "6. Sessions")]
        public int LondonEndUtc { get; set; }
        [Parameter("New York start hour UTC", DefaultValue = 12, MinValue = 0, MaxValue = 23, Group = "6. Sessions")]
        public int NyStartUtc { get; set; }
        [Parameter("New York end hour UTC", DefaultValue = 21, MinValue = 1, MaxValue = 24, Group = "6. Sessions")]
        public int NyEndUtc { get; set; }

        // ------------------------------------------------------------------ exits
        [Parameter("Stop buffer (ticks)", DefaultValue = 5, MinValue = 0, MaxValue = 500, Group = "7. Exits")]
        public int StopBufferTicks { get; set; }

        [Parameter("Break-even at R", DefaultValue = 1.0, MinValue = 0, MaxValue = 10, Step = 0.1, Group = "7. Exits",
            Description = "0 disables break-even.")]
        public double BreakEvenAtR { get; set; }

        [Parameter("Trail start at R", DefaultValue = 1.5, MinValue = 0, MaxValue = 20, Step = 0.1, Group = "7. Exits",
            Description = "0 disables trailing.")]
        public double TrailStartR { get; set; }

        [Parameter("Trail ATR multiple", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 10, Step = 0.1, Group = "7. Exits")]
        public double TrailAtrMultiple { get; set; }

        [Parameter("Time stop (minutes)", DefaultValue = 240, MinValue = 0, MaxValue = 10080, Group = "7. Exits",
            Description = "0 disables the time stop. Only closes trades that never reached break-even.")]
        public int TimeStopMinutes { get; set; }

        [Parameter("Enable partial exit", DefaultValue = false, Group = "7. Exits",
            Description = "Auto-disabled when the broker cannot close half of the fixed volume (typical with 0.01 lot).")]
        public bool PartialExitEnabled { get; set; }

        [Parameter("Partial exit at R", DefaultValue = 1.0, MinValue = 0.2, MaxValue = 10, Step = 0.1, Group = "7. Exits")]
        public double PartialAtR { get; set; }

        [Parameter("Partial exit fraction", DefaultValue = 0.5, MinValue = 0.1, MaxValue = 0.9, Step = 0.05, Group = "7. Exits")]
        public double PartialFraction { get; set; }

        // ------------------------------------------------------------------ execution
        [Parameter("Max spread (ticks)", DefaultValue = 40, MinValue = 1, MaxValue = 2000, Group = "8. Execution")]
        public int MaxSpreadTicks { get; set; }

        [Parameter("Slippage allowance (ticks)", DefaultValue = 5, MinValue = 0, MaxValue = 200, Group = "8. Execution",
            Description = "Added to planned risk before the USD cap is tested.")]
        public int SlippageAllowanceTicks { get; set; }

        [Parameter("Max allowed fill slippage (ticks)", DefaultValue = 20, MinValue = 0, MaxValue = 500, Group = "8. Execution",
            Description = "If the fill is worse than this, the position is closed immediately. 0 disables.")]
        public int MaxAllowedSlippageTicks { get; set; }

        [Parameter("Max entry drift (ticks)", DefaultValue = 8, MinValue = 0, MaxValue = 500, Group = "8. Execution")]
        public int MaxEntryDriftTicks { get; set; }

        [Parameter("Order timeout (ms)", DefaultValue = 8000, MinValue = 500, MaxValue = 60000, Group = "8. Execution")]
        public int OrderTimeoutMs { get; set; }

        [Parameter("Max quote age (seconds)", DefaultValue = 5, MinValue = 1, MaxValue = 300, Group = "8. Execution")]
        public int MaxQuoteAgeSeconds { get; set; }

        // ------------------------------------------------------------------ external
        [Parameter("Enable GEX levels", DefaultValue = false, Group = "9. External data")]
        public bool EnableGex { get; set; }

        [Parameter("GEX file path (JSON)", DefaultValue = "", Group = "9. External data")]
        public string GexFilePath { get; set; }

        [Parameter("GEX URL (optional)", DefaultValue = "", Group = "9. External data")]
        public string GexUrl { get; set; }

        [Parameter("GEX max age (minutes)", DefaultValue = 240, MinValue = 1, MaxValue = 10080, Group = "9. External data")]
        public int GexMaxAgeMinutes { get; set; }

        [Parameter("Enable VIX filter", DefaultValue = false, Group = "9. External data")]
        public bool EnableVix { get; set; }

        [Parameter("VIX file path (JSON)", DefaultValue = "", Group = "9. External data")]
        public string VixFilePath { get; set; }

        [Parameter("VIX URL (optional)", DefaultValue = "", Group = "9. External data")]
        public string VixUrl { get; set; }

        [Parameter("VIX max age (minutes)", DefaultValue = 1440, MinValue = 1, MaxValue = 20160, Group = "9. External data")]
        public int VixMaxAgeMinutes { get; set; }

        [Parameter("VIX high threshold", DefaultValue = 30.0, MinValue = 5, MaxValue = 100, Step = 1, Group = "9. External data")]
        public double VixHighThreshold { get; set; }

        [Parameter("Block entries on high VIX", DefaultValue = true, Group = "9. External data")]
        public bool BlockOnHighVix { get; set; }

        [Parameter("Stale-data policy", DefaultValue = StalePolicyOption.BlockEntries, Group = "9. External data")]
        public StalePolicyOption StalePolicy { get; set; }

        [Parameter("External refresh (seconds)", DefaultValue = 60, MinValue = 5, MaxValue = 3600, Group = "9. External data")]
        public int ExternalRefreshSeconds { get; set; }

        [Parameter("HTTP timeout (seconds)", DefaultValue = 10, MinValue = 1, MaxValue = 120, Group = "9. External data")]
        public int HttpTimeoutSeconds { get; set; }

        // ------------------------------------------------------------------ diagnostics
        [Parameter("Enable latency stats", DefaultValue = true, Group = "10. Diagnostics")]
        public bool EnableLatencyStats { get; set; }

        [Parameter("Write CSV journal", DefaultValue = true, Group = "10. Diagnostics")]
        public bool EnableCsvJournal { get; set; }

        [Parameter("Journal directory", DefaultValue = ".", Group = "10. Diagnostics")]
        public string JournalDirectory { get; set; }

        [Parameter("Journal prefix", DefaultValue = "quantlol", Group = "10. Diagnostics")]
        public string JournalPrefix { get; set; }

        [Parameter("Log rejected setups", DefaultValue = true, Group = "10. Diagnostics")]
        public bool LogRejections { get; set; }

        [Parameter("Verbose signal log", DefaultValue = false, Group = "10. Diagnostics")]
        public bool VerboseSignalLog { get; set; }
    }
}
