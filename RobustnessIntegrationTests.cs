using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using FinancialTerminal.App.Trading;
using FinancialTerminal.Core.Interfaces;
using FinancialTerminal.Core.Models;
using FinancialTerminal.Data.Repositories;
using Xunit;

namespace FinancialTerminal.Tests;

public sealed class RobustnessIntegrationTests
{
    [Fact]
    public async Task ExecuteOpportunityAsync_PersistsTwoLegFills_AndBroker()
    {
        var settings = TestSupport.CreateSettings();
        var dbPath = TestSupport.CreateTempDbPath();

        await using var db = new DatabaseRepository(dbPath);
        var risk = new RiskManager(settings, db, NullLogger<RiskManager>.Instance);
        var orders = new OrderRepository(db);
        var queue = new DeterministicExecutionQueue();
        var reservoir = new CapitalReservoir(settings, NullLogger<CapitalReservoir>.Instance);
        var engine = new TradeExecutionEngine(
            riskManager: risk,
            capitalReservoir: reservoir,
            executionQueue: queue,
            metrics: new OperationalMetrics(),
            orders: orders,
            db: db,
            settings: settings,
            logger: NullLogger<TradeExecutionEngine>.Instance);

        var strategy = new TwoLegStrategy();
        var opportunity = new Opportunity
        {
            StrategyName = strategy.Name,
            Ticker = "GGAL",
            BuyPrice = 1000m,
            SellPrice = 1010m,
            Quantity = 5m,
            ExpectedNetProfit = 50m,
            ExpectedGrossProfit = 60m,
            FeesTotal = 10m,
            BuySettlement = Settlement.CI,
            SellSettlement = Settlement.T24hs
        };

        var result = await engine.ExecuteOpportunityAsync(strategy, opportunity);

        Assert.NotNull(result);
        Assert.True(result!.Success);

        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();

        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(1) FROM order_fills";
        var fillsCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
        Assert.Equal(2, fillsCount);

        await using var brokerCmd = conn.CreateCommand();
        brokerCmd.CommandText = "SELECT COUNT(1) FROM trades WHERE broker = 'Paper'";
        var paperTrades = Convert.ToInt32(await brokerCmd.ExecuteScalarAsync());
        Assert.Equal(2, paperTrades);
    }

    [Fact]
    public async Task ReconcileOpenOrdersAsync_MarksSubmittedAsFilled_WhenBrokerReportsFilled()
    {
        var settings = TestSupport.CreateSettings();
        var dbPath = TestSupport.CreateTempDbPath();

        await using var db = new DatabaseRepository(dbPath);
        var orders = new OrderRepository(db);
        var risk = new RiskManager(settings, db, NullLogger<RiskManager>.Instance);
        var executor = new ReconciliationOrderExecutor();

        var snapshot = new ManagedOrderSnapshot
        {
            ExecutionId = "exec-reconcile-1",
            StrategyName = "Test",
            Ticker = "GGAL",
            Side = OrderSide.Buy,
            Settlement = Settlement.CI,
            Quantity = 1,
            Price = 1000,
            State = ManagedOrderState.Submitted,
            BrokerOrderId = "ord-reconcile-1",
            UpdatedAtUtc = DateTime.UtcNow
        };

        await orders.SaveSnapshotAsync(snapshot);

        var reconciliator = new OrderReconciliator(
            orders,
            db,
            executor,
            risk,
            NullLogger<OrderReconciliator>.Instance);

        var report = await reconciliator.ReconcileOpenOrdersAsync(queryBroker: true);

        Assert.Equal(1, report.Filled);

        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();

        await using var stateCmd = conn.CreateCommand();
        stateCmd.CommandText = "SELECT state FROM active_orders WHERE execution_id = 'exec-reconcile-1'";
        var state = (string?)await stateCmd.ExecuteScalarAsync();
        Assert.Equal("Filled", state);

        await using var brokerCmd = conn.CreateCommand();
        brokerCmd.CommandText = "SELECT broker FROM trades WHERE order_id = 'ord-reconcile-1' LIMIT 1";
        var broker = (string?)await brokerCmd.ExecuteScalarAsync();
        Assert.Equal("Paper", broker);
    }

    [Fact]
    public async Task TradingModeGuard_BlocksWhenQueuePending_OrInFlightOrders()
    {
        var dbPath = TestSupport.CreateTempDbPath();
        await using var db = new DatabaseRepository(dbPath);
        var orders = new OrderRepository(db);

        var guardWithQueue = new TradingModeGuard(orders, new QueueStub(pendingCount: 2));
        var blockedByQueue = await guardWithQueue.CanTogglePaperModeAsync(activeBotsCount: 0);
        Assert.False(blockedByQueue.Allowed);

        await orders.SaveSnapshotAsync(new ManagedOrderSnapshot
        {
            ExecutionId = "exec-mode-1",
            StrategyName = "Test",
            Ticker = "GGAL",
            Side = OrderSide.Buy,
            Settlement = Settlement.CI,
            Quantity = 1,
            Price = 100,
            State = ManagedOrderState.Submitted,
            BrokerOrderId = "ord-mode-1",
            UpdatedAtUtc = DateTime.UtcNow
        });

        var guardWithInflight = new TradingModeGuard(orders, new QueueStub(pendingCount: 0));
        var blockedByInFlight = await guardWithInflight.CanTogglePaperModeAsync(activeBotsCount: 0);
        Assert.False(blockedByInFlight.Allowed);

        var blockedByBots = await guardWithInflight.CanTogglePaperModeAsync(activeBotsCount: 1);
        Assert.False(blockedByBots.Allowed);
    }

    private sealed class TwoLegStrategy : IStrategy, ITradeIntentStrategy
    {
        public string Name => "TwoLeg";
        public bool IsEnabled => true;
        public bool IsRunning => true;

        public Task<Opportunity?> ScanAsync(CancellationToken ct = default) => Task.FromResult<Opportunity?>(null);

        public Task<OrderResult?> ExecuteAsync(Opportunity opportunity, CancellationToken ct = default) =>
            Task.FromResult<OrderResult?>(null);

        public Task<IReadOnlyList<TradeIntent>> BuildTradeIntentsAsync(Opportunity opportunity, CancellationToken ct = default)
        {
            IReadOnlyList<TradeIntent> intents =
            [
                new TradeIntent
                {
                    StrategyName = Name,
                    Ticker = opportunity.Ticker,
                    Side = OrderSide.Buy,
                    Settlement = opportunity.BuySettlement,
                    Quantity = opportunity.Quantity,
                    Price = opportunity.BuyPrice,
                    ExpectedNetProfit = opportunity.ExpectedNetProfit
                },
                new TradeIntent
                {
                    StrategyName = Name,
                    Ticker = opportunity.Ticker,
                    Side = OrderSide.Sell,
                    Settlement = opportunity.SellSettlement,
                    Quantity = opportunity.Quantity,
                    Price = opportunity.SellPrice,
                    ExpectedNetProfit = opportunity.ExpectedNetProfit
                }
            ];

            return Task.FromResult(intents);
        }
    }

    private sealed class DeterministicExecutionQueue : IExecutionQueue
    {
        private int _sequence;
        public int PendingCount => 0;

        public Task<OrderResult> EnqueueAsync(TradeIntent intent, CancellationToken ct = default)
        {
            var id = Interlocked.Increment(ref _sequence);
            return Task.FromResult(new OrderResult
            {
                Success = true,
                Broker = BrokerType.Paper,
                OrderId = $"paper-int-{id}",
                Ticker = intent.Ticker,
                Side = intent.Side,
                Settlement = intent.Settlement,
                ExecutedPrice = intent.Price,
                ExecutedQuantity = intent.Quantity,
                Status = OrderStatus.Filled
            });
        }
    }

    private sealed class ReconciliationOrderExecutor : IOrderExecutor
    {
        public BrokerType BrokerType => BrokerType.Paper;
        public bool IsConnected => true;

        public Task<bool> ConnectAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task DisconnectAsync() => Task.CompletedTask;

        public Task<OrderResult> SendOrderAsync(OrderRequest order, CancellationToken ct = default) =>
            Task.FromResult(new OrderResult
            {
                Success = true,
                Broker = BrokerType.Paper,
                OrderId = "unused",
                Ticker = order.Ticker,
                Side = order.Side,
                Settlement = order.Settlement,
                ExecutedPrice = order.Price,
                ExecutedQuantity = order.Quantity,
                Status = OrderStatus.Filled
            });

        public Task<bool> CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<OrderResult?> GetOrderStatusAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<OrderResult?>(new OrderResult
            {
                Success = true,
                Broker = BrokerType.Paper,
                OrderId = orderId,
                Ticker = "GGAL",
                Side = OrderSide.Buy,
                Settlement = Settlement.CI,
                ExecutedPrice = 1000,
                ExecutedQuantity = 1,
                Status = OrderStatus.Filled
            });

        public Task<IReadOnlyList<OrderResult>> GetActiveOrdersAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OrderResult>>(Array.Empty<OrderResult>());
    }

    private sealed class QueueStub : IExecutionQueue
    {
        public QueueStub(int pendingCount)
        {
            PendingCount = pendingCount;
        }

        public int PendingCount { get; }

        public Task<OrderResult> EnqueueAsync(TradeIntent intent, CancellationToken ct = default) =>
            Task.FromResult(new OrderResult
            {
                Success = true,
                Broker = BrokerType.Paper,
                OrderId = "stub",
                Ticker = intent.Ticker,
                Side = intent.Side,
                Settlement = intent.Settlement,
                ExecutedPrice = intent.Price,
                ExecutedQuantity = intent.Quantity,
                Status = OrderStatus.Filled
            });
    }
}
