using FinancialTerminal.Core.Models;
using FinancialTerminal.Data.Repositories;
using Xunit;

namespace FinancialTerminal.Tests;

public sealed class PortfolioSnapshotTests
{
    [Fact]
    public async Task SaveDailyPortfolioSnapshotAsync_WritesAccountAndPositions()
    {
        var dbPath = TestSupport.CreateTempDbPath();
        await using var db = new DatabaseRepository(dbPath);

        var balance = new AccountBalance
        {
            AvailableARS = 12_000m,
            AvailableUSD = 150m,
            Broker = BrokerType.PPI
        };

        var positions = new List<Position>
        {
            new() { Ticker = "GGAL", Settlement = Settlement.CI, BuySize = 10m, BuyPrice = 100m, SellSize = 0m, SellPrice = 0m, TotalDailyDiff = 5m, TotalDiff = 40m },
            new() { Ticker = "YPFD", Settlement = Settlement.T24hs, BuySize = 4m, BuyPrice = 200m, SellSize = 0m, SellPrice = 0m, TotalDailyDiff = -3m, TotalDiff = -20m },
        };

        await db.SaveDailyPortfolioSnapshotAsync(balance, positions);
        var health = await db.GetTodayPortfolioSnapshotHealthAsync(BrokerType.PPI);

        Assert.True(health.HasAccountSnapshot);
        Assert.Equal(2, health.PositionCount);
    }

    [Fact]
    public async Task SaveDailyPortfolioSnapshotAsync_ReplacesPositionSnapshotForSameDay()
    {
        var dbPath = TestSupport.CreateTempDbPath();
        await using var db = new DatabaseRepository(dbPath);

        var balance = new AccountBalance
        {
            AvailableARS = 10_000m,
            AvailableUSD = 0m,
            Broker = BrokerType.PPI
        };

        await db.SaveDailyPortfolioSnapshotAsync(balance, new List<Position>
        {
            new() { Ticker = "GGAL", Settlement = Settlement.CI, BuySize = 10m, BuyPrice = 100m, SellSize = 0m, SellPrice = 0m },
            new() { Ticker = "YPFD", Settlement = Settlement.CI, BuySize = 8m, BuyPrice = 200m, SellSize = 0m, SellPrice = 0m },
        });

        await db.SaveDailyPortfolioSnapshotAsync(balance, new List<Position>
        {
            new() { Ticker = "GGAL", Settlement = Settlement.CI, BuySize = 5m, BuyPrice = 100m, SellSize = 0m, SellPrice = 0m },
        });

        var health = await db.GetTodayPortfolioSnapshotHealthAsync(BrokerType.PPI);
        Assert.True(health.HasAccountSnapshot);
        Assert.Equal(1, health.PositionCount);
    }
}
