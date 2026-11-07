using Microsoft.Extensions.Logging.Abstractions;
using FinancialTerminal.App.Trading;
using FinancialTerminal.Core.Interfaces;
using FinancialTerminal.Core.Models;
using FinancialTerminal.Data.Repositories;
using Xunit;

namespace FinancialTerminal.Tests;

public sealed class RiskManagerTests
{
    [Fact]
    public async Task EvaluateAsync_Rejects_WhenCapitalPerTradeExceeded()
    {
        var settings = TestSupport.CreateSettings();
        settings.Trading.InitialCapital = 10_000m;
        settings.Trading.MaxCapitalPerOperationPercent = 10m; // 1,000 max per trade
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
            Quantity = 100m,
            Price = 25m // 2,500 > 1,000
        });

        Assert.False(result.Allowed);
        Assert.Contains("Capital por trade", result.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_ActivatesKillSwitch_WhenDailyLossLimitReached()
    {
        var settings = TestSupport.CreateSettings();
        settings.Risk.MaxDailyLossArs = 100m;
        settings.Risk.MaxDailyLossPercent = 100m;
        var dbPath = TestSupport.CreateTempDbPath();

        await using var db = new DatabaseRepository(dbPath);
        var risk = new RiskManager(settings, db, NullLogger<RiskManager>.Instance);

        await db.InsertTradeAsync(
            "TEST",
            new OrderResult
            {
                Success = true,
                OrderId = "ord-risk-loss",
                Ticker = "GGAL",
                Side = OrderSide.Buy,
                Settlement = Settlement.CI,
                ExecutedPrice = 100m,
                ExecutedQuantity = 1m,
                Status = OrderStatus.Filled
            },
            grossProfit: -200m,
            netProfit: -200m,
            fees: 0m);

        var result = await risk.EvaluateAsync(new RiskCheckRequest
        {
            StrategyName = "TEST",
            Ticker = "GGAL",
            Side = OrderSide.Buy,
            Settlement = Settlement.CI,
            Quantity = 1m,
            Price = 100m
        });

        Assert.False(result.Allowed);
        Assert.True(risk.IsKillSwitchActive);
        Assert.Contains("Límite de pérdida diaria", risk.KillSwitchReason);
    }

    [Fact]
    public async Task EvaluateAsync_Rejects_WhenKillSwitchAlreadyActive()
    {
        var settings = TestSupport.CreateSettings();
        var dbPath = TestSupport.CreateTempDbPath();
        await using var db = new DatabaseRepository(dbPath);
        var risk = new RiskManager(settings, db, NullLogger<RiskManager>.Instance);

        risk.ActivateKillSwitch("test reason");

        var result = await risk.EvaluateAsync(new RiskCheckRequest
        {
            StrategyName = "TEST",
            Ticker = "GGAL",
            Side = OrderSide.Buy,
            Settlement = Settlement.CI,
            Quantity = 1m,
            Price = 100m
        });

        Assert.False(result.Allowed);
        Assert.Contains("Kill switch activo", result.Reason);
    }
}
