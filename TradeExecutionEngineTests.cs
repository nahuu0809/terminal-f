using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using FinancialTerminal.App.Trading;
using FinancialTerminal.Core.Interfaces;
using FinancialTerminal.Core.Models;
using FinancialTerminal.Data.Repositories;
using Xunit;

namespace FinancialTerminal.Tests;

public sealed class TradeExecutionEngineTests
{
    [Fact]
    public async Task ExecuteOpportunityAsync_DoesNotCallStrategy_WhenRiskRejects()
    {
        var settings = TestSupport.CreateSettings();
        settings.Trading.InitialCapital = 10_000m;
        settings.Trading.MaxCapitalPerOperationPercent = 1m; // max notional = 100
        var dbPath = TestSupport.CreateTempDbPath();

        await using var db = new DatabaseRepository(dbPath);
        var risk = new RiskManager(settings, db, NullLogger<RiskManager>.Instance);
        var orders = new OrderRepository(db);
        var queue = new StubExecutionQueue(_ => Task.FromResult(new OrderResult
        {
            Success = true,
            OrderId = "q1",
            Ticker = "GGAL",
            Side = OrderSide.Buy,
            Settlement = Settlement.CI,
            ExecutedPrice = 100m,
            ExecutedQuantity = 1m,
            Status = OrderStatus.Filled
        }));
        var reservoir = new CapitalReservoir(settings, NullLogger<CapitalReservoir>.Instance);
        var engine = new TradeExecutionEngine(
            settings: settings,
            riskManager: risk,
            capitalReservoir: reservoir,
            executionQueue: queue,
            metrics: new OperationalMetrics(),
            orders: orders,
            db: db,
            logger: NullLogger<TradeExecutionEngine>.Instance);

        var strategy = new StubStrategy(() => Task.FromResult<OrderResult?>(new OrderResult
        {
            Success = true,
            OrderId = "should-not-run",
            Ticker = "GGAL",
            Side = OrderSide.Buy,
            Settlement = Settlement.CI,
            ExecutedPrice = 100m,
            ExecutedQuantity = 1m,
            Status = OrderStatus.Filled
        }));

        var opportunity = new Opportunity
        {
            StrategyName = "TEST",
            Ticker = "GGAL",
            BuyPrice = 50m,
            SellPrice = 51m,
            Quantity = 10m, // notional=500 > 100
            ExpectedNetProfit = 10m,
            ExpectedGrossProfit = 12m,
            FeesTotal = 2m,
            BuySettlement = Settlement.CI,
            SellSettlement = Settlement.T24hs
        };

        var result = await engine.ExecuteOpportunityAsync(strategy, opportunity);

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Equal(0, strategy.ExecuteCalls);
        Assert.Equal(0, queue.Calls);
    }

    [Fact]
    public async Task ExecuteOpportunityAsync_ActivatesAutoKillSwitchWithinTwoSeconds_OnConsecutiveFailure()
    {
        var settings = TestSupport.CreateSettings();
        settings.Risk.MaxConsecutiveExecutionFailures = 1;
        settings.Risk.EnableLatencyKillSwitch = false;
        var dbPath = TestSupport.CreateTempDbPath();

        await using var db = new DatabaseRepository(dbPath);
        var risk = new RiskManager(settings, db, NullLogger<RiskManager>.Instance);
        var orders = new OrderRepository(db);
        var queue = new StubExecutionQueue(_ => Task.FromResult(new OrderResult
        {
            Success = false,
            OrderId = "ord-fail-1",
            Ticker = "GGAL",
            Side = OrderSide.Buy,
            Settlement = Settlement.CI,
            ExecutedPrice = 100m,
            ExecutedQuantity = 1m,
            Status = OrderStatus.Rejected,
            ErrorMessage = "simulated failure"
        }));
        var reservoir = new CapitalReservoir(settings, NullLogger<CapitalReservoir>.Instance);
        var engine = new TradeExecutionEngine(
            settings: settings,
            riskManager: risk,
            capitalReservoir: reservoir,
            executionQueue: queue,
            metrics: new OperationalMetrics(),
            orders: orders,
            db: db,
            logger: NullLogger<TradeExecutionEngine>.Instance);

        var strategy = new StubStrategy(() => Task.FromResult<OrderResult?>(null));

        var opportunity = new Opportunity
        {
            StrategyName = "TEST",
            Ticker = "GGAL",
            BuyPrice = 100m,
            SellPrice = 101m,
            Quantity = 1m,
            ExpectedNetProfit = 1m,
            ExpectedGrossProfit = 1m,
            FeesTotal = 0m,
            BuySettlement = Settlement.CI,
            SellSettlement = Settlement.T24hs
        };

        var sw = Stopwatch.StartNew();
        await engine.ExecuteOpportunityAsync(strategy, opportunity);
        sw.Stop();

        Assert.True(risk.IsKillSwitchActive);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"Kill switch tardó {sw.ElapsedMilliseconds}ms");
    }

    private sealed class StubStrategy : IStrategy
    {
        private readonly Func<Task<OrderResult?>> _execution;

        public StubStrategy(Func<Task<OrderResult?>> execution)
        {
            _execution = execution;
        }

        public string Name => "Stub";
        public bool IsEnabled => true;
        public bool IsRunning => true;
        public int ExecuteCalls { get; private set; }

        public Task<Opportunity?> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult<Opportunity?>(null);

        public async Task<OrderResult?> ExecuteAsync(Opportunity opportunity, CancellationToken ct = default)
        {
            ExecuteCalls++;
            return await _execution();
        }
    }

    private sealed class StubExecutionQueue : IExecutionQueue
    {
        private readonly Func<TradeIntent, Task<OrderResult>> _handler;

        public StubExecutionQueue(Func<TradeIntent, Task<OrderResult>> handler)
        {
            _handler = handler;
        }

        public int Calls { get; private set; }
        public int PendingCount => 0;

        public async Task<OrderResult> EnqueueAsync(TradeIntent intent, CancellationToken ct = default)
        {
            Calls++;
            return await _handler(intent);
        }
    }
}
