using System;
using QuantLOL.Core;
using Xunit;

namespace QuantLOL.Tests
{
    /// <summary>
    /// Acceptance items 2 and 3: 0.01-lot volume resolution and the USD 5 planned-risk
    /// calculation, verified against broker symbol specifications (cTrader semantics).
    /// </summary>
    public class BrokerMathTests
    {
        static ContractSpec Gold(long lotSize = 100, double tick = 0.01, double min = 1, double max = 100000, double step = 1)
            => new ContractSpec { LotSize = lotSize, TickSize = tick, PipSize = tick, TickValue = tick, VolumeMin = min, VolumeMax = max, VolumeStep = step };

        [Fact]
        public void ZeroPointZeroOneLotIsOneUnitOnHundredOunceContract()
        {
            var r = BrokerMath.ResolveVolume(0.01, Gold());
            Assert.True(r.Ok, r.Error);
            Assert.Equal(1.0, r.Units, 9);
            Assert.Equal(0.01, r.ActualLots, 12);
        }

        [Fact]
        public void OneOuncePerLotContractIsAlsoRepresentable()
        {
            var r = BrokerMath.ResolveVolume(0.01, new ContractSpec
            { LotSize = 1, TickSize = 0.01, PipSize = 0.01, TickValue = 0.01, VolumeMin = 0.01, VolumeMax = 1000, VolumeStep = 0.01 });
            Assert.True(r.Ok, r.Error);
            Assert.Equal(0.01, r.Units, 9);
        }

        [Fact]
        public void TenOuncePerLotContractGivesPointOneUnits()
        {
            var r = BrokerMath.ResolveVolume(0.01, new ContractSpec
            { LotSize = 10, TickSize = 0.01, PipSize = 0.01, TickValue = 0.1, VolumeMin = 0.1, VolumeMax = 10000, VolumeStep = 0.1 });
            Assert.True(r.Ok, r.Error);
            Assert.Equal(0.1, r.Units, 9);
        }

        [Fact]
        public void RejectsWhenBrokerMinimumExceedsIntendedSize()
        {
            var r = BrokerMath.ResolveVolume(0.01, Gold(min: 5));
            Assert.False(r.Ok, "0.01 lot = 1 unit cannot satisfy a 5-unit minimum");
            Assert.Contains("minimum", r.Error);
        }

        [Fact]
        public void RejectsMisalignedVolumeStep()
        {
            var r = BrokerMath.ResolveVolume(0.015, Gold(step: 1));
            Assert.False(r.Ok, "1.5 units is not aligned to a 1-unit step");
        }

        [Fact]
        public void MoneyPerPriceUnitIsOneDollarPerDollarForOneOunce()
        {
            Assert.Equal(1.0, BrokerMath.MoneyPerPriceUnit(1.0, 1.0), 12);
            Assert.Equal(0.1, BrokerMath.MoneyPerPriceUnit(0.1, 1.0), 12);
        }

        [Fact]
        public void FiveDollarCapImpliesMaxStructuralStopDistance()
        {
            double perPrice = BrokerMath.MoneyPerPriceUnit(1.0, 1.0);
            const double commissionRoundTrip = 0.14;   // 0.07 per side
            const double slippage = 0.05;              // 5 ticks at 0.01
            double maxDist = BrokerMath.MaxStopDistance(5.0, perPrice, commissionRoundTrip, slippage);
            Assert.Equal(4.81, maxDist, 9);

            var fits = RiskMath.Compute(3300.00, 3300.00 - 4.80, 3310.00, perPrice, commissionRoundTrip, slippage);
            Assert.True(RiskMath.Approve(fits, new RiskCaps { MaxRiskPerTradeUsd = 5.0, MinRewardRisk = 1.0 }, out _));

            var tooWide = RiskMath.Compute(3300.00, 3300.00 - 5.00, 3310.00, perPrice, commissionRoundTrip, slippage);
            Assert.False(RiskMath.Approve(tooWide, new RiskCaps { MaxRiskPerTradeUsd = 5.0, MinRewardRisk = 1.0 }, out string why));
            Assert.Contains("planned risk", why);
        }

        [Fact]
        public void CommissionModelsAndMinimum()
        {
            Assert.Equal(0.07, BrokerMath.CommissionPerSide(CommissionModel.UsdPerOneLot, 7.0, 1.0, 3300, 100, 0), 12);
            Assert.Equal(0.066, BrokerMath.CommissionPerSide(CommissionModel.PercentageOfTradingVolume, 0.002, 1.0, 3300, 100, 0), 9);
            Assert.Equal(0.5, BrokerMath.CommissionPerSide(CommissionModel.UsdPerOneLot, 1.0, 1.0, 3300, 100, 0.5), 12);
            Assert.Equal(0.0, BrokerMath.CommissionPerSide(CommissionModel.None, 0, 1.0, 3300, 100, 0), 12);
        }
    }
}
