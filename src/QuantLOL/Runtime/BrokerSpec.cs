using System;
using System.Text;
using cAlgo.API;
using cAlgo.API.Internals;
using QuantLOL.Core;

namespace QuantLOL.Runtime
{
    /// <summary>
    /// Verified contract facts for the traded symbol, derived from the broker's symbol
    /// object at runtime (never hard-coded). The fixed lot size is translated into
    /// cTrader volume units here, and the order is rejected if the broker cannot
    /// represent it exactly.
    /// </summary>
    public sealed class BrokerSpec
    {
        public bool Verified;
        public string Reason = "";
        public string SymbolName = "";
        public string Description = "";
        public double RequestedLots, Units, ActualLots;
        public long LotSize;
        public double TickSize, PipSize, TickValue, PipValue;
        public double VolumeInUnitsMin, VolumeInUnitsMax, VolumeInUnitsStep;
        public int Digits;
        public double MoneyPerPriceUnit;          // account currency per 1.0 price unit, for Units
        public double ContractPerPriceUnit;       // units * quote->account rate
        public double BrokerAmountRiskedPerPrice; // cross-check via Symbol.AmountRisked
        public double TickImpliedPerPrice;        // TickValue / TickSize
        public double TickImpliedScaledPerPrice;  // TickValue / TickSize * (Units / LotSize)
        public double CommissionPerSideUsd, CommissionRoundTripUsd;
        public string CommissionModel = "";
        public double MinStopLossDistance, MinTakeProfitDistance;
        public bool TradingEnabled;
        public string TradingMode = "";
        public double QuoteToAccountRate = 1.0;
        public string QuoteAsset = "";
        public string AccountAssetName = "";

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append("spec[").Append(SymbolName).Append("] lots=").Append(Numeric.F(RequestedLots, 4))
              .Append(" units=").Append(Numeric.F(Units, 4)).Append(" (lotSize=").Append(LotSize).Append(')')
              .Append(" tick=").Append(Numeric.F(TickSize, 6)).Append(" tickValue=").Append(Numeric.F(TickValue, 6))
              .Append(" pip=").Append(Numeric.F(PipSize, 6))
              .Append(" volMin=").Append(Numeric.F(VolumeInUnitsMin, 4)).Append(" step=").Append(Numeric.F(VolumeInUnitsStep, 4))
              .Append(" moneyPerPrice=").Append(Numeric.F(MoneyPerPriceUnit, 6))
              .Append(" comm=").Append(Numeric.F(CommissionRoundTripUsd, 4)).Append(" (").Append(CommissionModel).Append(')')
              .Append(" verified=").Append(Verified);
            if (!Verified) sb.Append(" reason=").Append(Reason);
            return sb.ToString();
        }
    }

    public static class BrokerSpecFactory
    {
        /// <summary>
        /// Builds and validates the spec. Any failure leaves Verified=false with a precise,
        /// actionable reason; the caller must refuse to trade in that case.
        /// </summary>
        public static BrokerSpec Build(Symbol symbol, IAccount account, double lots, double referencePrice)
        {
            var s = new BrokerSpec { RequestedLots = lots };
            if (symbol == null) { s.Reason = "no symbol object"; return s; }
            s.SymbolName = symbol.Name ?? "";
            s.Description = symbol.Description ?? "";
            s.LotSize = symbol.LotSize;
            s.TickSize = symbol.TickSize;
            s.PipSize = symbol.PipSize;
            s.TickValue = symbol.TickValue;
            s.PipValue = symbol.PipValue;
            s.Digits = symbol.Digits;
            s.VolumeInUnitsMin = symbol.VolumeInUnitsMin;
            s.VolumeInUnitsMax = symbol.VolumeInUnitsMax;
            s.VolumeInUnitsStep = symbol.VolumeInUnitsStep;
            s.MinStopLossDistance = symbol.MinStopLossDistance;
            s.MinTakeProfitDistance = symbol.MinTakeProfitDistance;
            s.TradingEnabled = symbol.IsTradingEnabled;
            s.TradingMode = symbol.TradingMode.ToString();
            s.QuoteAsset = symbol.QuoteAsset?.Name ?? "?";
            s.AccountAssetName = account?.Asset?.Name ?? "?";

            if (s.LotSize <= 0) { s.Reason = "LotSize is zero"; return s; }
            if (s.TickSize <= 0) { s.Reason = "TickSize is zero"; return s; }
            if (s.VolumeInUnitsStep <= 0) { s.Reason = "VolumeInUnitsStep is zero"; return s; }

            // 1. Translate the fixed lot size into volume units, exactly (pure, unit-tested logic).
            var vol = BrokerMath.ResolveVolume(lots, new ContractSpec
            {
                LotSize = s.LotSize, TickSize = s.TickSize, PipSize = s.PipSize, TickValue = s.TickValue,
                VolumeMin = s.VolumeInUnitsMin, VolumeMax = s.VolumeInUnitsMax, VolumeStep = s.VolumeInUnitsStep
            });
            if (!vol.Ok) { s.Reason = vol.Error; s.Units = 0; return s; }
            double stepped = vol.Units;
            s.Units = stepped;
            s.ActualLots = vol.ActualLots;

            // 2. Money per 1.0 price unit for exactly this volume.
            try
            {
                s.QuoteToAccountRate = symbol.QuoteAsset != null && account?.Asset != null
                    ? symbol.QuoteAsset.Convert(account.Asset, 1.0)
                    : 1.0;
            }
            catch { s.QuoteToAccountRate = 1.0; }
            s.ContractPerPriceUnit = BrokerMath.MoneyPerPriceUnit(stepped, s.QuoteToAccountRate);

            try
            {
                double probePips = 1000.0;
                double amount = symbol.AmountRisked(stepped, probePips);
                s.BrokerAmountRiskedPerPrice = amount / (probePips * s.PipSize);
            }
            catch { s.BrokerAmountRiskedPerPrice = double.NaN; }

            s.TickImpliedPerPrice = s.TickSize > 0 ? s.TickValue / s.TickSize : double.NaN;
            s.TickImpliedScaledPerPrice = s.LotSize > 0 ? s.TickImpliedPerPrice * (stepped / s.LotSize) : double.NaN;

            s.MoneyPerPriceUnit = s.ContractPerPriceUnit;
            if (!(s.MoneyPerPriceUnit > 0) || double.IsNaN(s.MoneyPerPriceUnit))
            {
                s.Reason = "could not derive money-per-price-unit (quote asset conversion failed)";
                return s;
            }
            if (Numeric.IsFinite(s.BrokerAmountRiskedPerPrice) && s.BrokerAmountRiskedPerPrice > 0)
            {
                double diff = Math.Abs(s.BrokerAmountRiskedPerPrice - s.MoneyPerPriceUnit) / s.MoneyPerPriceUnit;
                if (diff > 0.02)
                {
                    s.Reason = "broker AmountRisked (" + Numeric.F(s.BrokerAmountRiskedPerPrice, 6) +
                               "/price) disagrees with contract maths (" + Numeric.F(s.MoneyPerPriceUnit, 6) + "/price) by " +
                               Numeric.F(diff * 100, 2) + "%";
                    return s;
                }
            }

            // 3. Commission (per side), from the broker's own commission model.
            s.CommissionPerSideUsd = EstimateCommissionPerSide(symbol, stepped, referencePrice, out string model);
            s.CommissionModel = model;
            s.CommissionRoundTripUsd = 2.0 * s.CommissionPerSideUsd;

            if (!s.TradingEnabled || symbol.TradingMode != SymbolTradingMode.FullAccess)
            {
                s.Reason = "symbol not fully tradable (enabled=" + s.TradingEnabled + ", mode=" + s.TradingMode + ")";
                return s;
            }
            s.Verified = true;
            return s;
        }

        /// <summary>
        /// Commission estimate. cTrader charges commission on both entry and exit; the
        /// calculation follows Symbol.CommissionType. This is an ESTIMATE used for the
        /// pre-trade risk cap; realised commission is taken from the position/closed deal.
        /// </summary>
        public static double EstimateCommissionPerSide(Symbol symbol, double units, double price, out string model)
        {
            CommissionModel m;
            switch (symbol.CommissionType)
            {
                case SymbolCommissionType.UsdPerMillionUsdVolume: m = CommissionModel.UsdPerMillionUsdVolume; model = "usd_per_million_notional"; break;
                case SymbolCommissionType.UsdPerOneLot: m = CommissionModel.UsdPerOneLot; model = "usd_per_lot"; break;
                case SymbolCommissionType.PercentageOfTradingVolume: m = CommissionModel.PercentageOfTradingVolume; model = "pct_of_notional"; break;
                case SymbolCommissionType.QuoteCurrencyPerOneLot: m = CommissionModel.QuoteCurrencyPerOneLot; model = "quote_ccy_per_lot"; break;
                default: m = CommissionModel.None; model = "unknown"; break;
            }
            return BrokerMath.CommissionPerSide(m, symbol.Commission, units, price, symbol.LotSize, symbol.MinCommission);
        }
    }
}
