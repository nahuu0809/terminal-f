using Microsoft.Extensions.Logging.Abstractions;
using FinancialTerminal.Core.Configuration;
using FinancialTerminal.Core.Interfaces;
using FinancialTerminal.Core.Models;
using FinancialTerminal.Strategies.Arbitrage;
using FinancialTerminal.Strategies.Caucion;
using FinancialTerminal.Strategies.MarketMaking;
using FinancialTerminal.Strategies.Options;
using Moq;
using Xunit;

namespace FinancialTerminal.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// MEP/CCL ARBITRAGE TESTS
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MepCclArbitrageTests
{
    private static AppSettings CreateSettings() => new()
    {
        MepCcl = new MepCclSettings
        {
            Enabled = true,
            MinSpreadPercent = 0.1m,
            BondBases = ["AL30", "GD30"],
            ArsSettlement = Settlement.CI,
            UsdSettlement = Settlement.T24hs
        },
        Trading = new TradingSettings
        {
            InitialCapital = 1_000_000m,
            MaxCapitalPerOperationPercent = 25m,
            MinNetProfit = 1m
        }
    };

    [Fact]
    public async Task ScanAsync_ReturnsNull_WhenDisabled()
    {
        var settings = CreateSettings();
        settings.MepCcl.Enabled = false;

        var md = new Mock<IMarketDataProvider>();
        var acc = new Mock<IAccountProvider>();
        var strategy = new MepCclArbitrageStrategy(md.Object, acc.Object, settings,
            NullLogger<MepCclArbitrageStrategy>.Instance);

        var result = await strategy.ScanAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task ScanAsync_ReturnsNull_WhenOnlyOneBondPairAvailable()
    {
        var settings = CreateSettings();
        var md = new Mock<IMarketDataProvider>();
        var acc = new Mock<IAccountProvider>();

        // Solo AL30 tiene quotes, GD30 retorna null
        md.Setup(m => m.GetQuoteAsync("AL30", Settlement.CI, It.IsAny<CancellationToken>()))
          .ReturnsAsync(new Quote { Ticker = "AL30", Settlement = Settlement.CI, Ask = 65000, Bid = 64900 });
        md.Setup(m => m.GetQuoteAsync("AL30D", Settlement.T24hs, It.IsAny<CancellationToken>()))
          .ReturnsAsync(new Quote { Ticker = "AL30D", Settlement = Settlement.T24hs, Ask = 55, Bid = 54.8m });
        md.Setup(m => m.GetQuoteAsync("GD30", Settlement.CI, It.IsAny<CancellationToken>()))
          .ReturnsAsync((Quote?)null);

        var strategy = new MepCclArbitrageStrategy(md.Object, acc.Object, settings,
            NullLogger<MepCclArbitrageStrategy>.Instance);

        var result = await strategy.ScanAsync();
        Assert.Null(result); // Necesita 2+ pares para cross-bond
    }

    [Fact]
    public async Task ScanAsync_FindsOpportunity_WhenSpreadExceedsMinimum()
    {
        var settings = CreateSettings();
        settings.MepCcl.MinSpreadPercent = 0.1m;

        var md = new Mock<IMarketDataProvider>();
        var acc = new Mock<IAccountProvider>();

        // AL30: MEP implícito buy = 65000/54.8 = 1186.13
        md.Setup(m => m.GetQuoteAsync("AL30", Settlement.CI, It.IsAny<CancellationToken>()))
          .ReturnsAsync(new Quote { Ticker = "AL30", Settlement = Settlement.CI, Ask = 65000, Bid = 64900 });
        md.Setup(m => m.GetQuoteAsync("AL30D", Settlement.T24hs, It.IsAny<CancellationToken>()))
          .ReturnsAsync(new Quote { Ticker = "AL30D", Settlement = Settlement.T24hs, Ask = 55m, Bid = 54.8m });

        // GD30: MEP implícito sell = 64000/53 = 1207.55 (mucho más alto → spread favorable)
        md.Setup(m => m.GetQuoteAsync("GD30", Settlement.CI, It.IsAny<CancellationToken>()))
          .ReturnsAsync(new Quote { Ticker = "GD30", Settlement = Settlement.CI, Ask = 63000, Bid = 64000 });
        md.Setup(m => m.GetQuoteAsync("GD30D", Settlement.T24hs, It.IsAny<CancellationToken>()))
          .ReturnsAsync(new Quote { Ticker = "GD30D", Settlement = Settlement.T24hs, Ask = 53m, Bid = 53.5m });

        acc.Setup(a => a.GetBalanceAsync(It.IsAny<CancellationToken>()))
           .ReturnsAsync(new AccountBalance { AvailableARS = 1_000_000m });

        var strategy = new MepCclArbitrageStrategy(md.Object, acc.Object, settings,
            NullLogger<MepCclArbitrageStrategy>.Instance);

        var result = await strategy.ScanAsync();
        Assert.NotNull(result);
        Assert.Equal("MEP/CCL Arbitrage", result.StrategyName);
        Assert.True(result.ExpectedNetProfit > 0);
        Assert.True(result.Quantity > 0);
    }

    [Fact]
    public async Task ScanAsync_ReturnsNull_WhenSpreadTooSmall()
    {
        var settings = CreateSettings();
        settings.MepCcl.MinSpreadPercent = 5m; // Spread muy alto requerido

        var md = new Mock<IMarketDataProvider>();
        var acc = new Mock<IAccountProvider>();

        // Precios casi iguales, spread mínimo
        md.Setup(m => m.GetQuoteAsync("AL30", Settlement.CI, It.IsAny<CancellationToken>()))
          .ReturnsAsync(new Quote { Ticker = "AL30", Settlement = Settlement.CI, Ask = 65000, Bid = 64900 });
        md.Setup(m => m.GetQuoteAsync("AL30D", Settlement.T24hs, It.IsAny<CancellationToken>()))
          .ReturnsAsync(new Quote { Ticker = "AL30D", Settlement = Settlement.T24hs, Ask = 55, Bid = 54.9m });
        md.Setup(m => m.GetQuoteAsync("GD30", Settlement.CI, It.IsAny<CancellationToken>()))
          .ReturnsAsync(new Quote { Ticker = "GD30", Settlement = Settlement.CI, Ask = 64950, Bid = 64850 });
        md.Setup(m => m.GetQuoteAsync("GD30D", Settlement.T24hs, It.IsAny<CancellationToken>()))
          .ReturnsAsync(new Quote { Ticker = "GD30D", Settlement = Settlement.T24hs, Ask = 55, Bid = 54.9m });

        acc.Setup(a => a.GetBalanceAsync(It.IsAny<CancellationToken>()))
           .ReturnsAsync(new AccountBalance { AvailableARS = 1_000_000m });

        var strategy = new MepCclArbitrageStrategy(md.Object, acc.Object, settings,
            NullLogger<MepCclArbitrageStrategy>.Instance);

        var result = await strategy.ScanAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task BuildTradeIntentsAsync_Creates4Legs()
    {
        var settings = CreateSettings();
        var md = new Mock<IMarketDataProvider>();
        var acc = new Mock<IAccountProvider>();

        var strategy = new MepCclArbitrageStrategy(md.Object, acc.Object, settings,
            NullLogger<MepCclArbitrageStrategy>.Instance);

        var opportunity = new Opportunity
        {
            StrategyName = "MEP/CCL Arbitrage",
            Ticker = "AL30",
            BuyPrice = 65000, SellPrice = 64000,
            BuySettlement = Settlement.CI, SellSettlement = Settlement.CI,
            Quantity = 3,
            ExpectedNetProfit = 500,
            Details = new()
            {
                ["buyBond"] = "AL30", ["sellBond"] = "GD30",
                ["buyArsTicker"] = "AL30", ["buyUsdTicker"] = "AL30D",
                ["sellArsTicker"] = "GD30", ["sellUsdTicker"] = "GD30D",
                ["buyArsAsk"] = 65000m, ["buyUsdBid"] = 54.8m,
                ["sellUsdAsk"] = 53m, ["sellArsBid"] = 64000m,
            }
        };

        var intents = await strategy.BuildTradeIntentsAsync(opportunity);
        Assert.Equal(4, intents.Count);
        Assert.All(intents, i => Assert.Equal(AssetType.Bono, i.AssetType));
        Assert.Equal(OrderSide.Buy, intents[0].Side);   // Buy AL30 ARS
        Assert.Equal(OrderSide.Sell, intents[1].Side);   // Sell AL30D
        Assert.Equal(OrderSide.Buy, intents[2].Side);    // Buy GD30D
        Assert.Equal(OrderSide.Sell, intents[3].Side);   // Sell GD30 ARS
        Assert.Equal("AL30", intents[0].Ticker);
        Assert.Equal("AL30D", intents[1].Ticker);
        Assert.Equal("GD30D", intents[2].Ticker);
        Assert.Equal("GD30", intents[3].Ticker);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// CAUCIÓN AUTO TESTS
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class CaucionAutoTests
{
    private static AppSettings CreateSettings() => new()
    {
        Caucion = new CaucionSettings
        {
            EnableAutoAtClose = true,
            MinRatePercent = 30m,
            ComisionColocadoraTNA = 0m,
            CloseExecutionTime = new TimeOnly(0, 0) // always open for testing
        },
        Trading = new TradingSettings
        {
            InitialCapital = 1_000_000m,
            MinNetProfit = 0.01m
        },
        Market = new MarketSchedule
        {
            AuctionCloseTime = new TimeOnly(23, 59) // always open for testing
        }
    };

    [Fact]
    public async Task ScanAsync_ReturnsNull_WhenDisabled()
    {
        var settings = CreateSettings();
        settings.Caucion.EnableAutoAtClose = false;

        var md = new Mock<IMarketDataProvider>();
        var acc = new Mock<IAccountProvider>();
        var strategy = new CaucionAutoStrategy(md.Object, acc.Object, settings,
            NullLogger<CaucionAutoStrategy>.Instance);

        var result = await strategy.ScanAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task ScanAsync_ReturnsNull_WhenInsufficientCash()
    {
        var settings = CreateSettings();
        var md = new Mock<IMarketDataProvider>();
        var acc = new Mock<IAccountProvider>();

        acc.Setup(a => a.GetBalanceAsync(It.IsAny<CancellationToken>()))
           .ReturnsAsync(new AccountBalance { AvailableARS = 5_000m }); // < 10,000

        var strategy = new CaucionAutoStrategy(md.Object, acc.Object, settings,
            NullLogger<CaucionAutoStrategy>.Instance);

        var result = await strategy.ScanAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task ScanAsync_ReturnsNull_WhenRateBelowMinimum()
    {
        var settings = CreateSettings();
        settings.Caucion.MinRatePercent = 100m; // Requiere TNA > 100%

        var md = new Mock<IMarketDataProvider>();
        var acc = new Mock<IAccountProvider>();

        acc.Setup(a => a.GetBalanceAsync(It.IsAny<CancellationToken>()))
           .ReturnsAsync(new AccountBalance { AvailableARS = 500_000m });

        // Mock caución quote returning a low TNA (20%) which is below min (100%)
        md.Setup(m => m.GetQuoteAsync("CAUCION-1D", Settlement.CI, It.IsAny<CancellationToken>()))
          .ReturnsAsync(new Quote { Ticker = "CAUCION-1D", Settlement = Settlement.CI, Last = 20m });

        var strategy = new CaucionAutoStrategy(md.Object, acc.Object, settings,
            NullLogger<CaucionAutoStrategy>.Instance);

        var result = await strategy.ScanAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task ScanAsync_ReturnsOpportunity_WhenConditionsMet()
    {
        var settings = CreateSettings();
        settings.Caucion.MinRatePercent = 30m;

        var md = new Mock<IMarketDataProvider>();
        var acc = new Mock<IAccountProvider>();

        acc.Setup(a => a.GetBalanceAsync(It.IsAny<CancellationToken>()))
           .ReturnsAsync(new AccountBalance { AvailableARS = 500_000m });

        // Quote de caución con TNA 40%
        md.Setup(m => m.GetQuoteAsync("CAUCION-1D", Settlement.CI, It.IsAny<CancellationToken>()))
          .ReturnsAsync(new Quote { Ticker = "CAUCION-1D", Settlement = Settlement.CI, Last = 40m });

        var strategy = new CaucionAutoStrategy(md.Object, acc.Object, settings,
            NullLogger<CaucionAutoStrategy>.Instance);

        var result = await strategy.ScanAsync();
        Assert.NotNull(result);
        Assert.Equal("Caucion Auto", result.StrategyName);
        Assert.True(result.ExpectedNetProfit > 0);
        Assert.Equal(500_000m, result.Quantity);
    }

    [Fact]
    public async Task BuildTradeIntentsAsync_CreatesSingleCaucionIntent()
    {
        var settings = CreateSettings();
        var md = new Mock<IMarketDataProvider>();
        var acc = new Mock<IAccountProvider>();

        var strategy = new CaucionAutoStrategy(md.Object, acc.Object, settings,
            NullLogger<CaucionAutoStrategy>.Instance);

        var opp = new Opportunity
        {
            StrategyName = "Caucion Auto",
            Ticker = "CAUCION", BuyPrice = 40, SellPrice = 0,
            BuySettlement = Settlement.CI, SellSettlement = Settlement.CI,
            Quantity = 500_000m, ExpectedNetProfit = 50,
            Details = new() { ["tna"] = 40m, ["dias"] = 1 }
        };

        var intents = await strategy.BuildTradeIntentsAsync(opp);
        Assert.Single(intents);
        Assert.Equal(AssetType.Caucion, intents[0].AssetType);
        Assert.Equal(OrderSide.Sell, intents[0].Side); // Colocadora
        Assert.Equal(500_000m, intents[0].Quantity);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// MARKET MAKING TESTS
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MarketMakingTests
{
    private static AppSettings CreateSettings() => new()
    {
        MarketMaking = new MarketMakingSettings
        {
            Enabled = true,
            Ticker = "GGAL",
            Settlement = Settlement.CI,
            MinSpread = 0.5m,
            OrderSize = 10m,
            MaxPosition = 100m,
            CancelThresholdTicks = 2
        },
        Trading = new TradingSettings { MinNetProfit = 0.01m }
    };

    [Fact]
    public async Task ScanAsync_ReturnsNull_WhenDisabled()
    {
        var settings = CreateSettings();
        settings.MarketMaking.Enabled = false;

        var md = new Mock<IMarketDataProvider>();
        var acc = new Mock<IAccountProvider>();
        var strategy = new MarketMakingStrategy(md.Object, acc.Object, settings,
            NullLogger<MarketMakingStrategy>.Instance);

        var result = await strategy.ScanAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task ScanAsync_ReturnsNull_WhenSpreadTooTight()
    {
        var settings = CreateSettings();
        settings.MarketMaking.MinSpread = 5m; // Requiere 5% de spread

        var md = new Mock<IMarketDataProvider>();
        var acc = new Mock<IAccountProvider>();

        md.Setup(m => m.GetOrderBookAsync("GGAL", Settlement.CI, It.IsAny<CancellationToken>()))
          .ReturnsAsync(new OrderBook
          {
              Ticker = "GGAL", Settlement = Settlement.CI,
              Bids = [new BookLevel(5000m, 100)],
              Asks = [new BookLevel(5010m, 100)] // 0.2% spread < 5%
          });

        var strategy = new MarketMakingStrategy(md.Object, acc.Object, settings,
            NullLogger<MarketMakingStrategy>.Instance);

        var result = await strategy.ScanAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task ScanAsync_ReturnsOpportunity_WhenSpreadSufficient()
    {
        var settings = CreateSettings();
        settings.MarketMaking.MinSpread = 1.0m; // quoted spread must exceed fees (~0.44% round-trip)
        settings.MarketMaking.OrderSize = 500m;

        var md = new Mock<IMarketDataProvider>();
        var acc = new Mock<IAccountProvider>();

        md.Setup(m => m.GetOrderBookAsync("GGAL", Settlement.CI, It.IsAny<CancellationToken>()))
          .ReturnsAsync(new OrderBook
          {
              Ticker = "GGAL", Settlement = Settlement.CI,
              Bids = [new BookLevel(4950m, 1000)],
              Asks = [new BookLevel(5050m, 1000)] // 2% spread > 1%
          });

        acc.Setup(a => a.GetPositionsAsync(It.IsAny<CancellationToken>()))
           .ReturnsAsync(new List<Position>());

        var strategy = new MarketMakingStrategy(md.Object, acc.Object, settings,
            NullLogger<MarketMakingStrategy>.Instance);

        var result = await strategy.ScanAsync();
        Assert.NotNull(result);
        Assert.Equal("Market Making", result.StrategyName);
        Assert.Equal("GGAL", result.Ticker);
    }

    [Fact]
    public async Task BuildTradeIntentsAsync_CreatesSingleLeg()
    {
        var settings = CreateSettings();
        var md = new Mock<IMarketDataProvider>();
        var acc = new Mock<IAccountProvider>();

        var strategy = new MarketMakingStrategy(md.Object, acc.Object, settings,
            NullLogger<MarketMakingStrategy>.Instance);

        var opp = new Opportunity
        {
            StrategyName = "Market Making",
            Ticker = "GGAL", BuyPrice = 5000, SellPrice = 5050,
            BuySettlement = Settlement.CI, SellSettlement = Settlement.CI,
            Quantity = 10, ExpectedNetProfit = 100,
            Details = new()
            {
                ["mid"] = 5025m, ["currentSpread"] = 1m,
                ["position"] = 0m, ["side"] = "Buy",
                ["bidPrice"] = 5010m, ["askPrice"] = 5040m
            }
        };

        var intents = await strategy.BuildTradeIntentsAsync(opp);
        Assert.Single(intents);
        Assert.Equal(OrderSide.Buy, intents[0].Side);
        Assert.Equal(5010m, intents[0].Price);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// PUTs BAJO LA PAR TESTS
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class PutsArbitrageTests
{
    private static AppSettings CreateSettings() => new()
    {
        Options = new OptionsSettings
        {
            Enabled = true,
            UmbralPut = 0.95m,
            ComisionOpcionesPct = 0.37m,
            ComisionAccionesPct = 0.22m,
            MinRatioPct = 0.1m,
            Underlyings = ["GGAL"],
            PutTickers = ["GFGV75054F", "GFGV80000F"]
        },
        Trading = new TradingSettings
        {
            InitialCapital = 1_000_000m,
            MaxCapitalPerOperationPercent = 25m,
            MinNetProfit = 0.01m
        }
    };

    [Fact]
    public async Task ScanAsync_ReturnsNull_WhenDisabled()
    {
        var settings = CreateSettings();
        settings.Options.Enabled = false;

        var md = new Mock<IMarketDataProvider>();
        var strategy = new PutsArbitrageStrategy(md.Object, settings,
            NullLogger<PutsArbitrageStrategy>.Instance);

        var result = await strategy.ScanAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task ScanAsync_ReturnsNull_WhenNoPutTickers()
    {
        var settings = CreateSettings();
        settings.Options.PutTickers.Clear();

        var md = new Mock<IMarketDataProvider>();
        var strategy = new PutsArbitrageStrategy(md.Object, settings,
            NullLogger<PutsArbitrageStrategy>.Instance);

        var result = await strategy.ScanAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task ScanAsync_FindsOpportunity_WhenRatioAboveMinimum()
    {
        var settings = CreateSettings();
        settings.Options.MinRatioPct = 0.1m;

        var md = new Mock<IMarketDataProvider>();

        // GGAL spot = 7400
        md.Setup(m => m.GetQuoteAsync("GGAL", Settlement.T24hs, It.IsAny<CancellationToken>()))
          .ReturnsAsync(new Quote { Ticker = "GGAL", Settlement = Settlement.T24hs, Ask = 7400 });

        // PUT GFGV75054F: strike = 7505.4, ask = 50
        // costoArmar = 50 * 1.0037 + 7400 * 1.0022 = 50.185 + 7416.28 = 7466.465
        // ejercer = 7505.4 * 1.0022 = 7521.91
        // ratio = (7521.91 / 7466.465 - 1) * 100 = 0.74% > 0.1% ✓
        md.Setup(m => m.GetQuoteAsync("GFGV75054F", Settlement.T24hs, It.IsAny<CancellationToken>()))
          .ReturnsAsync(new Quote { Ticker = "GFGV75054F", Settlement = Settlement.T24hs, Ask = 50 });

        // GFGV80000F no matchea para GGAL (prefix GFGV sí matchea, pero strike too high)
        md.Setup(m => m.GetQuoteAsync("GFGV80000F", Settlement.T24hs, It.IsAny<CancellationToken>()))
          .ReturnsAsync(new Quote { Ticker = "GFGV80000F", Settlement = Settlement.T24hs, Ask = 700 });

        var strategy = new PutsArbitrageStrategy(md.Object, settings,
            NullLogger<PutsArbitrageStrategy>.Instance);

        var result = await strategy.ScanAsync();
        Assert.NotNull(result);
        Assert.Equal("PUTs bajo la par", result.StrategyName);
        Assert.True(result.ExpectedNetProfit > 0);
    }

    [Fact]
    public async Task BuildTradeIntentsAsync_Creates2Legs_PutAndUnderlying()
    {
        var settings = CreateSettings();
        var md = new Mock<IMarketDataProvider>();

        var strategy = new PutsArbitrageStrategy(md.Object, settings,
            NullLogger<PutsArbitrageStrategy>.Instance);

        var opp = new Opportunity
        {
            StrategyName = "PUTs bajo la par",
            Ticker = "GFGV75054F", BuyPrice = 50, SellPrice = 7521,
            BuySettlement = Settlement.T24hs, SellSettlement = Settlement.T24hs,
            Quantity = 5, ExpectedNetProfit = 200,
            Details = new()
            {
                ["putTicker"] = "GFGV75054F",
                ["underlying"] = "GGAL",
                ["spotAsk"] = 7400m, ["putAsk"] = 50m,
                ["strike"] = 7505.4m
            }
        };

        var intents = await strategy.BuildTradeIntentsAsync(opp);
        Assert.Equal(2, intents.Count);
        Assert.Equal(AssetType.Opcion, intents[0].AssetType);
        Assert.Equal("GFGV75054F", intents[0].Ticker);
        Assert.Equal(AssetType.Accion, intents[1].AssetType);
        Assert.Equal("GGAL", intents[1].Ticker);
    }

    [Fact]
    public void ExtractStrike_5Digits_DividesBy10()
    {
        // GFGV75054F → 7505.4
        // Access via reflection since it's private
        var method = typeof(PutsArbitrageStrategy).GetMethod("ExtractStrike",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var result = (decimal)method.Invoke(null, ["GFGV75054F"])!;
        Assert.Equal(7505.4m, result);
    }

    [Fact]
    public void ExtractStrike_4Digits_NoDecimal()
    {
        var method = typeof(PutsArbitrageStrategy).GetMethod("ExtractStrike",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var result = (decimal)method.Invoke(null, ["GFGV7505F"])!;
        Assert.Equal(7505m, result);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// PIPELINE / ASSETTYPE TESTS
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class AssetTypePipelineTests
{
    [Theory]
    [InlineData(AssetType.Accion, "ACCIONES")]
    [InlineData(AssetType.Bono, "BONOS")]
    [InlineData(AssetType.Letra, "BONOS")]
    [InlineData(AssetType.Opcion, "OPCIONES")]
    [InlineData(AssetType.Cedear, "CEDEARS")]
    [InlineData(AssetType.Caucion, "CAUCIONES")]
    [InlineData(AssetType.ON, "OBLIGACIONES-NEGOCIABLES")]
    [InlineData(AssetType.FCI, "FCI")]
    public void MapInstrumentType_ReturnsCorrectPpiValue(AssetType assetType, string expected)
    {
        // Access via reflection since it's private static
        var method = typeof(FinancialTerminal.Brokers.PPI.PPIClient).GetMethod("MapInstrumentType",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var result = (string)method.Invoke(null, [assetType])!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void TradeIntent_DefaultAssetType_IsAccion()
    {
        var intent = new TradeIntent
        {
            StrategyName = "Test", Ticker = "GGAL",
            Side = OrderSide.Buy, Settlement = Settlement.CI,
            Quantity = 10, Price = 5000
        };

        Assert.Equal(AssetType.Accion, intent.AssetType);
    }

    [Fact]
    public void OrderRequest_DefaultAssetType_IsAccion()
    {
        var request = new OrderRequest
        {
            Ticker = "GGAL", Side = OrderSide.Buy,
            Settlement = Settlement.CI, Quantity = 10, Price = 5000
        };

        Assert.Equal(AssetType.Accion, request.AssetType);
    }

    [Fact]
    public void TradeIntent_CanSetBondAssetType()
    {
        var intent = new TradeIntent
        {
            StrategyName = "MEP", Ticker = "AL30",
            Side = OrderSide.Buy, Settlement = Settlement.CI,
            Quantity = 10, Price = 65000,
            AssetType = AssetType.Bono
        };

        Assert.Equal(AssetType.Bono, intent.AssetType);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// FEES CALCULATOR TESTS
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class FeesCalculatorTests
{
    [Fact]
    public void CalculateRoundTripFees_BondRate_IsLowerThanEquity()
    {
        var equityFees = FinancialTerminal.Core.FeesCalculator.CalculateRoundTripFees(
            100, 101, 100, AssetType.Accion);
        var bondFees = FinancialTerminal.Core.FeesCalculator.CalculateRoundTripFees(
            100, 101, 100, AssetType.Bono);

        Assert.True(bondFees.DerechosMercado < equityFees.DerechosMercado);
    }

    [Fact]
    public void CalculateTNA_CorrectFormula()
    {
        var tna = FinancialTerminal.Core.FeesCalculator.CalculateTNA(100m, 101m);
        // (101/100 - 1) * 365 = 0.01 * 365 = 3.65
        Assert.Equal(3.65m, tna);
    }
}
