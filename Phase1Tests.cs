using Microsoft.Extensions.Logging.Abstractions;
using FinancialTerminal.App.Trading;
using FinancialTerminal.Core;
using FinancialTerminal.Core.Configuration;
using FinancialTerminal.Core.Interfaces;
using FinancialTerminal.Core.Models;
using FinancialTerminal.Data.Repositories;
using FinancialTerminal.Strategies.Caucion;
using Moq;
using Xunit;

namespace FinancialTerminal.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// CAPITAL RESERVOIR TESTS
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class CapitalReservoirTests
{
    private static AppSettings Settings(decimal capital = 100_000m) => new()
    {
        Trading = new TradingSettings { InitialCapital = capital }
    };

    [Fact]
    public void TryReserve_ReturnsToken_WhenCapitalAvailable()
    {
        var reservoir = new CapitalReservoir(Settings(), NullLogger<CapitalReservoir>.Instance);
        var token = reservoir.TryReserve(50_000m, "TestStrategy");

        Assert.NotNull(token);
        Assert.Equal(50_000m, token.Amount);
        Assert.Equal("TestStrategy", token.StrategyName);
        Assert.Equal(50_000m, reservoir.ReservedCapital);
        Assert.Equal(50_000m, reservoir.AvailableCapital);
    }

    [Fact]
    public void TryReserve_ReturnsNull_WhenInsufficientCapital()
    {
        var reservoir = new CapitalReservoir(Settings(100_000m), NullLogger<CapitalReservoir>.Instance);
        var token = reservoir.TryReserve(150_000m, "TestStrategy");

        Assert.Null(token);
        Assert.Equal(0m, reservoir.ReservedCapital);
    }

    [Fact]
    public void TryReserve_ReturnsNull_WhenCapitalAlreadyReserved()
    {
        var reservoir = new CapitalReservoir(Settings(100_000m), NullLogger<CapitalReservoir>.Instance);

        var token1 = reservoir.TryReserve(70_000m, "Strategy1");
        Assert.NotNull(token1);

        var token2 = reservoir.TryReserve(50_000m, "Strategy2");
        Assert.Null(token2); // Only 30k available

        Assert.Equal(70_000m, reservoir.ReservedCapital);
    }

    [Fact]
    public void Release_FreesCapital()
    {
        var reservoir = new CapitalReservoir(Settings(100_000m), NullLogger<CapitalReservoir>.Instance);

        var token = reservoir.TryReserve(60_000m, "Strategy1")!;
        Assert.Equal(40_000m, reservoir.AvailableCapital);

        reservoir.Release(token);
        Assert.Equal(100_000m, reservoir.AvailableCapital);
        Assert.Equal(0m, reservoir.ReservedCapital);
    }

    [Fact]
    public void Release_AllowsNewReservation()
    {
        var reservoir = new CapitalReservoir(Settings(100_000m), NullLogger<CapitalReservoir>.Instance);

        var token1 = reservoir.TryReserve(80_000m, "Strategy1")!;
        Assert.Null(reservoir.TryReserve(30_000m, "Strategy2"));

        reservoir.Release(token1);
        var token2 = reservoir.TryReserve(30_000m, "Strategy2");
        Assert.NotNull(token2);
    }

    [Fact]
    public void TryReserve_ReturnsNull_ForZeroOrNegativeAmount()
    {
        var reservoir = new CapitalReservoir(Settings(), NullLogger<CapitalReservoir>.Instance);

        Assert.Null(reservoir.TryReserve(0m, "Test"));
        Assert.Null(reservoir.TryReserve(-100m, "Test"));
    }

    [Fact]
    public void MultipleReservations_TrackCorrectly()
    {
        var reservoir = new CapitalReservoir(Settings(100_000m), NullLogger<CapitalReservoir>.Instance);

        var t1 = reservoir.TryReserve(30_000m, "S1")!;
        var t2 = reservoir.TryReserve(30_000m, "S2")!;
        var t3 = reservoir.TryReserve(30_000m, "S3")!;

        Assert.Equal(90_000m, reservoir.ReservedCapital);
        Assert.Equal(10_000m, reservoir.AvailableCapital);

        // Can't fit another 20k
        Assert.Null(reservoir.TryReserve(20_000m, "S4"));

        reservoir.Release(t2);
        Assert.Equal(60_000m, reservoir.ReservedCapital);

        var t4 = reservoir.TryReserve(20_000m, "S4");
        Assert.NotNull(t4);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// CAPITAL RESERVOIR INTEGRATION WITH ENGINE TESTS
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class CapitalReservoirIntegrationTests
{
    [Fact]
    public async Task Engine_RejectsWhenCapitalFullyReserved()
    {
        var settings = TestSupport.CreateSettings();
        settings.Trading.InitialCapital = 10_000m;
        var dbPath = TestSupport.CreateTempDbPath();

        await using var db = new DatabaseRepository(dbPath);
        var risk = new RiskManager(settings, db, NullLogger<RiskManager>.Instance);
        var orders = new OrderRepository(db);
        var reservoir = new CapitalReservoir(settings, NullLogger<CapitalReservoir>.Instance);

        // Pre-reserve all capital
        var preReserve = reservoir.TryReserve(10_000m, "OtherStrategy");
        Assert.NotNull(preReserve);

        var queue = new StubQueue();
        var engine = new TradeExecutionEngine(
            riskManager: risk,
            capitalReservoir: reservoir,
            executionQueue: queue,
            metrics: new OperationalMetrics(),
            orders: orders,
            db: db,
            settings: settings,
            logger: NullLogger<TradeExecutionEngine>.Instance);

        var strategy = new StubStrategy();
        var opportunity = new Opportunity
        {
            StrategyName = "TEST", Ticker = "GGAL",
            BuyPrice = 100m, SellPrice = 101m,
            Quantity = 1m,
            ExpectedNetProfit = 1m, ExpectedGrossProfit = 1m, FeesTotal = 0m,
            BuySettlement = Settlement.CI, SellSettlement = Settlement.T24hs
        };

        var result = await engine.ExecuteOpportunityAsync(strategy, opportunity);

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Contains("Capital insuficiente", result.ErrorMessage);
        Assert.Equal(0, queue.Calls);
    }

    [Fact]
    public async Task Engine_ReleasesCapitalAfterExecution()
    {
        var settings = TestSupport.CreateSettings();
        settings.Trading.InitialCapital = 100_000m;
        var dbPath = TestSupport.CreateTempDbPath();

        await using var db = new DatabaseRepository(dbPath);
        var risk = new RiskManager(settings, db, NullLogger<RiskManager>.Instance);
        var orders = new OrderRepository(db);
        var reservoir = new CapitalReservoir(settings, NullLogger<CapitalReservoir>.Instance);

        var queue = new StubQueue();
        var engine = new TradeExecutionEngine(
            riskManager: risk,
            capitalReservoir: reservoir,
            executionQueue: queue,
            metrics: new OperationalMetrics(),
            orders: orders,
            db: db,
            settings: settings,
            logger: NullLogger<TradeExecutionEngine>.Instance);

        var strategy = new StubStrategy();
        var opportunity = new Opportunity
        {
            StrategyName = "TEST", Ticker = "GGAL",
            BuyPrice = 100m, SellPrice = 101m,
            Quantity = 10m,
            ExpectedNetProfit = 10m, ExpectedGrossProfit = 12m, FeesTotal = 2m,
            BuySettlement = Settlement.CI, SellSettlement = Settlement.T24hs
        };

        await engine.ExecuteOpportunityAsync(strategy, opportunity);

        // Capital should be released after execution
        Assert.Equal(0m, reservoir.ReservedCapital);
        Assert.Equal(100_000m, reservoir.AvailableCapital);
    }

    private sealed class StubStrategy : IStrategy
    {
        public string Name => "Stub";
        public bool IsEnabled => true;
        public bool IsRunning => true;
        public Task<Opportunity?> ScanAsync(CancellationToken ct = default) => Task.FromResult<Opportunity?>(null);
        public Task<OrderResult?> ExecuteAsync(Opportunity o, CancellationToken ct = default) => Task.FromResult<OrderResult?>(null);
    }

    private sealed class StubQueue : IExecutionQueue
    {
        public int Calls { get; private set; }
        public int PendingCount => 0;

        public Task<OrderResult> EnqueueAsync(TradeIntent intent, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new OrderResult
            {
                Success = true,
                Broker = BrokerType.Paper,
                OrderId = $"stub-{Calls}",
                Ticker = intent.Ticker,
                Side = intent.Side,
                Settlement = intent.Settlement,
                ExecutedPrice = intent.Price,
                ExecutedQuantity = intent.Quantity,
                Status = OrderStatus.Filled
            });
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// MAX LOSS PER TRADE TESTS
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MaxLossPerTradeTests
{
    [Fact]
    public async Task EvaluateAsync_Rejects_WhenNotionalExceedsMaxLoss()
    {
        var settings = TestSupport.CreateSettings();
        settings.Risk.MaxLossPerTradeArs = 5_000m;
        var dbPath = TestSupport.CreateTempDbPath();

        await using var db = new DatabaseRepository(dbPath);
        var risk = new RiskManager(settings, db, NullLogger<RiskManager>.Instance);

        var result = await risk.EvaluateAsync(new RiskCheckRequest
        {
            StrategyName = "TEST",
            Ticker = "GGAL",
            Side = OrderSide.Buy,
            Settlement = Settlement.CI,
            Quantity = 100m,
            Price = 100m // notional 10,000 > 5,000
        });

        Assert.False(result.Allowed);
        Assert.Contains("Pérdida máxima por trade", result.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_Approves_WhenNotionalBelowMaxLoss()
    {
        var settings = TestSupport.CreateSettings();
        settings.Risk.MaxLossPerTradeArs = 50_000m;
        var dbPath = TestSupport.CreateTempDbPath();

        await using var db = new DatabaseRepository(dbPath);
        var risk = new RiskManager(settings, db, NullLogger<RiskManager>.Instance);
        await risk.WarmupAsync();

        var result = await risk.EvaluateAsync(new RiskCheckRequest
        {
            StrategyName = "TEST",
            Ticker = "GGAL",
            Side = OrderSide.Buy,
            Settlement = Settlement.CI,
            Quantity = 10m,
            Price = 100m // notional 1,000 < 50,000
        });

        Assert.True(result.Allowed);
    }

    [Fact]
    public async Task EvaluateAsync_SkipsMaxLossCheck_WhenZero()
    {
        var settings = TestSupport.CreateSettings();
        settings.Risk.MaxLossPerTradeArs = 0m; // disabled
        var dbPath = TestSupport.CreateTempDbPath();

        await using var db = new DatabaseRepository(dbPath);
        var risk = new RiskManager(settings, db, NullLogger<RiskManager>.Instance);
        await risk.WarmupAsync();

        var result = await risk.EvaluateAsync(new RiskCheckRequest
        {
            StrategyName = "TEST",
            Ticker = "GGAL",
            Side = OrderSide.Buy,
            Settlement = Settlement.CI,
            Quantity = 1000m,
            Price = 1000m // huge notional, but check disabled
        });

        // Should pass max-loss check (may fail on other checks like max capital per op)
        Assert.DoesNotContain("Pérdida máxima por trade", result.Reason);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// ICLOCK TESTS
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class IClockTests
{
    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow { get; init; } = DateTime.UtcNow;
        public TimeOnly LocalTimeNow { get; init; } = TimeOnly.FromDateTime(DateTime.Now);
        public DateOnly TodayLocal { get; init; } = DateOnly.FromDateTime(DateTime.Now);
    }

    [Fact]
    public async Task CaucionAuto_UsesInjectedClock_ForTimeCheck()
    {
        var settings = new AppSettings
        {
            Caucion = new CaucionSettings
            {
                EnableAutoAtClose = true,
                MinRatePercent = 30m,
                ComisionColocadoraTNA = 0m,
                CloseExecutionTime = new TimeOnly(16, 50)
            },
            Trading = new TradingSettings { InitialCapital = 1_000_000m, MinNetProfit = 0.01m },
            Market = new MarketSchedule { AuctionCloseTime = new TimeOnly(16, 57) }
        };

        var md = new Mock<IMarketDataProvider>();
        var acc = new Mock<IAccountProvider>();

        // Clock says it's 14:00 — outside the execution window
        var earlyClock = new FixedClock { LocalTimeNow = new TimeOnly(14, 0) };
        var earlyStrategy = new CaucionAutoStrategy(md.Object, acc.Object, settings,
            NullLogger<CaucionAutoStrategy>.Instance, earlyClock);

        var earlyResult = await earlyStrategy.ScanAsync();
        Assert.Null(earlyResult); // Too early

        // Clock says it's 16:52 — inside the execution window
        var lateClock = new FixedClock { LocalTimeNow = new TimeOnly(16, 52) };
        acc.Setup(a => a.GetBalanceAsync(It.IsAny<CancellationToken>()))
           .ReturnsAsync(new AccountBalance { AvailableARS = 500_000m });
        md.Setup(m => m.GetQuoteAsync("CAUCION-1D", Settlement.CI, It.IsAny<CancellationToken>()))
          .ReturnsAsync(new Quote { Ticker = "CAUCION-1D", Settlement = Settlement.CI, Last = 40m });

        var lateStrategy = new CaucionAutoStrategy(md.Object, acc.Object, settings,
            NullLogger<CaucionAutoStrategy>.Instance, lateClock);

        var lateResult = await lateStrategy.ScanAsync();
        Assert.NotNull(lateResult);
    }

    [Fact]
    public void SystemClock_ReturnsCurrentTime()
    {
        var clock = new SystemClock();
        var now = DateTime.UtcNow;

        Assert.True(Math.Abs((clock.UtcNow - now).TotalSeconds) < 2);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Now), clock.TodayLocal);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// CAUCION FALLBACK RATE TESTS
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class CaucionFallbackTests
{
    [Fact]
    public async Task ScanAsync_ReturnsNull_WhenNoMarketData()
    {
        var settings = new AppSettings
        {
            Caucion = new CaucionSettings
            {
                EnableAutoAtClose = true,
                MinRatePercent = 30m,
                CloseExecutionTime = new TimeOnly(0, 0)
            },
            Trading = new TradingSettings { InitialCapital = 1_000_000m },
            Market = new MarketSchedule { AuctionCloseTime = new TimeOnly(23, 59) }
        };

        var md = new Mock<IMarketDataProvider>();
        var acc = new Mock<IAccountProvider>();

        acc.Setup(a => a.GetBalanceAsync(It.IsAny<CancellationToken>()))
           .ReturnsAsync(new AccountBalance { AvailableARS = 500_000m });

        // No quote available — returns null
        md.Setup(m => m.GetQuoteAsync("CAUCION-1D", Settlement.CI, It.IsAny<CancellationToken>()))
          .ReturnsAsync((Quote?)null);

        var strategy = new CaucionAutoStrategy(md.Object, acc.Object, settings,
            NullLogger<CaucionAutoStrategy>.Instance);

        var result = await strategy.ScanAsync();
        Assert.Null(result); // Should NOT execute with fabricated rate
    }

    [Fact]
    public async Task ScanAsync_ReturnsNull_WhenQuoteThrowsException()
    {
        var settings = new AppSettings
        {
            Caucion = new CaucionSettings
            {
                EnableAutoAtClose = true,
                MinRatePercent = 30m,
                CloseExecutionTime = new TimeOnly(0, 0)
            },
            Trading = new TradingSettings { InitialCapital = 1_000_000m },
            Market = new MarketSchedule { AuctionCloseTime = new TimeOnly(23, 59) }
        };

        var md = new Mock<IMarketDataProvider>();
        var acc = new Mock<IAccountProvider>();

        acc.Setup(a => a.GetBalanceAsync(It.IsAny<CancellationToken>()))
           .ReturnsAsync(new AccountBalance { AvailableARS = 500_000m });

        md.Setup(m => m.GetQuoteAsync("CAUCION-1D", Settlement.CI, It.IsAny<CancellationToken>()))
          .ThrowsAsync(new Exception("Connection timeout"));

        var strategy = new CaucionAutoStrategy(md.Object, acc.Object, settings,
            NullLogger<CaucionAutoStrategy>.Instance);

        var result = await strategy.ScanAsync();
        Assert.Null(result); // Should safely return null, not throw or fabricate
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// MARKET CALENDAR 2026 TESTS
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MarketCalendar2026Tests
{
    [Theory]
    [InlineData(2026, 1, 1)]   // Año Nuevo
    [InlineData(2026, 2, 16)]  // Carnaval
    [InlineData(2026, 2, 17)]  // Carnaval
    [InlineData(2026, 3, 24)]  // Día Nacional de la Memoria
    [InlineData(2026, 5, 25)]  // Revolución de Mayo
    [InlineData(2026, 6, 20)]  // Paso a la Inmortalidad Güemes/Belgrano
    [InlineData(2026, 7, 9)]   // Día de la Independencia
    [InlineData(2026, 12, 25)] // Navidad
    public void IsHoliday_Returns_True_For2026Holidays(int year, int month, int day)
    {
        Assert.True(MarketCalendar.IsHoliday(new DateOnly(year, month, day)));
    }

    [Fact]
    public void IsTradingDay_ReturnsFalse_For2026Holidays()
    {
        Assert.False(MarketCalendar.IsTradingDay(new DateOnly(2026, 1, 1)));
        Assert.False(MarketCalendar.IsTradingDay(new DateOnly(2026, 7, 9)));
    }

    [Fact]
    public void IsTradingDay_ReturnsTrue_ForRegular2026Weekday()
    {
        // Jan 5, 2026 is a Monday, not a holiday
        Assert.True(MarketCalendar.IsTradingDay(new DateOnly(2026, 1, 5)));
    }

    [Fact]
    public void IsTradingDay_ReturnsFalse_ForWeekend()
    {
        // Jan 3, 2026 is a Saturday
        Assert.False(MarketCalendar.IsTradingDay(new DateOnly(2026, 1, 3)));
    }
}
