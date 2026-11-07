using Microsoft.Extensions.Logging.Abstractions;
using FinancialTerminal.App.Trading;
using FinancialTerminal.Core.Configuration;
using FinancialTerminal.Core.Models;
using Xunit;

namespace FinancialTerminal.Tests;

public sealed class SimulatedBrokerTests
{
    [Fact]
    public async Task SendOrderAsync_UsesDeterministicSlippage()
    {
        var settings = TestSupport.CreateSettings();
        settings.PaperTrading.InitialCashArs = 1_000_000m;
        var broker = new SimulatedBroker(settings.PaperTrading, settings.Instruments, NullLogger<SimulatedBroker>.Instance);
        await broker.ConnectAsync();

        var order = new OrderRequest
        {
            Ticker = "GGAL",
            Side = OrderSide.Buy,
            Settlement = Settlement.CI,
            Quantity = 10,
            Price = 1000m
        };

        var first = await broker.SendOrderAsync(order);
        var second = await broker.SendOrderAsync(order);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(first.ExecutedPrice, second.ExecutedPrice);
    }

    [Fact]
    public async Task SendOrderAsync_RejectsSellWithoutPosition()
    {
        var settings = TestSupport.CreateSettings();
        var broker = new SimulatedBroker(settings.PaperTrading, settings.Instruments, NullLogger<SimulatedBroker>.Instance);
        await broker.ConnectAsync();

        var result = await broker.SendOrderAsync(new OrderRequest
        {
            Ticker = "GGAL",
            Side = OrderSide.Sell,
            Settlement = Settlement.CI,
            Quantity = 1,
            Price = 1000m
        });

        Assert.False(result.Success);
        Assert.Contains("posicion", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
