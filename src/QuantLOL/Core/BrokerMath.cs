using System;

namespace QuantLOL.Core
{
    /// <summary>Broker contract facts needed for volume/risk maths (pure data, unit-testable).</summary>
    public sealed class ContractSpec
    {
        public long LotSize;
        public double TickSize;
        public double PipSize;
        public double TickValue;
        public double VolumeMin;
        public double VolumeMax;
        public double VolumeStep;
    }

    public readonly struct VolumeResolution
    {
        public readonly bool Ok;
        public readonly string Error;
        public readonly double Units;
        public readonly double ActualLots;
        public VolumeResolution(bool ok, string error, double units, double actualLots)
        { Ok = ok; Error = error; Units = units; ActualLots = actualLots; }
    }

    public enum CommissionModel
    {
        None = 0,
        UsdPerMillionUsdVolume = 1,
        UsdPerOneLot = 2,
        PercentageOfTradingVolume = 3,
        QuoteCurrencyPerOneLot = 4
    }

    /// <summary>
    /// Converts a required lot size into cTrader volume units and validates it against the
    /// broker's minimum / maximum / step. Also derives the account-currency value of a
    /// 1.0 price move for that exact volume, and estimates commission.
    /// Nothing here is broker-API specific, so every rule is covered by unit tests.
    /// </summary>
    public static class BrokerMath
    {
        /// <summary>
        /// 0.01 lot on a 100 oz/lot XAUUSD contract = 1 volume unit (1 troy ounce).
        /// The result is rejected - never silently rounded - if the broker cannot represent it.
        /// </summary>
        public static VolumeResolution ResolveVolume(double lots, ContractSpec spec)
        {
            if (spec == null) return new VolumeResolution(false, "no contract spec", 0, 0);
            if (spec.LotSize <= 0) return new VolumeResolution(false, "LotSize is zero", 0, 0);
            if (spec.VolumeStep <= 0) return new VolumeResolution(false, "VolumeStep is zero", 0, 0);
            if (lots <= 0) return new VolumeResolution(false, "lots must be > 0", 0, 0);

            double raw = lots * spec.LotSize;
            double q = raw / spec.VolumeStep;
            if (Math.Abs(q - Math.Round(q)) > 1e-6)
                return new VolumeResolution(false,
                    "requested " + Numeric.F(lots, 5) + " lots = " + Numeric.F(raw, 6) +
                    " units is not a multiple of the volume step " + Numeric.F(spec.VolumeStep, 6), 0, 0);

            double units = Math.Round(q, MidpointRounding.AwayFromZero) * spec.VolumeStep;
            if (units < spec.VolumeMin - 1e-9)
                return new VolumeResolution(false,
                    "requested volume " + Numeric.F(units, 4) + " units < broker minimum " + Numeric.F(spec.VolumeMin, 4) +
                    " (0.01 lot would be " + Numeric.F(raw, 4) + " units on this symbol)", 0, 0);
            if (units > spec.VolumeMax + 1e-9)
                return new VolumeResolution(false,
                    "requested volume " + Numeric.F(units, 4) + " > broker maximum " + Numeric.F(spec.VolumeMax, 4), 0, 0);

            double actualLots = units / spec.LotSize;
            if (Math.Abs(actualLots - lots) > 1e-9)
                return new VolumeResolution(false,
                    "volume rounding would change the lot size from " + Numeric.F(lots, 5) + " to " + Numeric.F(actualLots, 5), 0, 0);

            return new VolumeResolution(true, null, units, actualLots);
        }

        /// <summary>Account currency P&amp;L of a 1.0 price move for the given volume units.</summary>
        public static double MoneyPerPriceUnit(double units, double quoteToAccountRate)
        {
            if (units <= 0 || !Numeric.IsFinite(quoteToAccountRate) || quoteToAccountRate <= 0) return double.NaN;
            return units * quoteToAccountRate;
        }

        /// <summary>Commission for ONE side of the trade (cTrader charges on entry and exit).</summary>
        public static double CommissionPerSide(CommissionModel model, double commission, double units,
                                               double price, long lotSize, double minCommission)
        {
            if (commission <= 0) return 0;
            double perSide;
            switch (model)
            {
                case CommissionModel.UsdPerMillionUsdVolume: perSide = commission * units * price / 1_000_000.0; break;
                case CommissionModel.UsdPerOneLot: perSide = commission * units / Math.Max(1, lotSize); break;
                case CommissionModel.PercentageOfTradingVolume: perSide = commission / 100.0 * units * price; break;
                case CommissionModel.QuoteCurrencyPerOneLot: perSide = commission * units / Math.Max(1, lotSize); break;
                default: perSide = 0; break;
            }
            if (minCommission > 0 && perSide < minCommission) perSide = minCommission;
            return perSide;
        }

        /// <summary>Maximum stop distance in price units that still fits the per-trade risk cap.</summary>
        public static double MaxStopDistance(double maxRiskUsd, double moneyPerPriceUnit, double commissionRoundTrip, double slippageUsd)
        {
            if (moneyPerPriceUnit <= 0) return 0;
            double budget = maxRiskUsd - commissionRoundTrip - slippageUsd;
            return budget <= 0 ? 0 : budget / moneyPerPriceUnit;
        }
    }
}
