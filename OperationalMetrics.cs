using System.Collections.Concurrent;
using FinancialTerminal.Core.Interfaces;
using FinancialTerminal.Core.Models;

namespace FinancialTerminal.App.Trading;

public sealed class OperationalMetrics : IOperationalMetrics
{
    private long _executions;
    private long _latencyMsTotal;
    private long _slippageBpsTotal;
    private long _heartbeatTicks = DateTime.UtcNow.Ticks;
    private decimal _exposureArs;
    private decimal _exposureUsd;
    private readonly object _exposureLock = new();

    public void RecordExecution(TradeIntent intent, OrderResult result, long latencyMs)
    {
        var currentExecutions = Interlocked.Increment(ref _executions);
        Interlocked.Add(ref _latencyMsTotal, Math.Max(0, latencyMs));

        if (result.ExecutedPrice > 0 && intent.Price > 0)
        {
            var slip = Math.Abs((result.ExecutedPrice - intent.Price) / intent.Price) * 10_000m;
            Interlocked.Add(ref _slippageBpsTotal, (long)Math.Round(slip, MidpointRounding.AwayFromZero));
        }

        var signedNotional = result.ExecutedQuantity * result.ExecutedPrice * (result.Side == OrderSide.Buy ? 1m : -1m);
        lock (_exposureLock)
        {
            if (result.Ticker?.EndsWith("D", StringComparison.OrdinalIgnoreCase) == true)
                _exposureUsd += signedNotional;
            else
                _exposureArs += signedNotional;
        }

        if (currentExecutions > 0)
        {
            Interlocked.Exchange(ref _heartbeatTicks, DateTime.UtcNow.Ticks);
        }
    }

    public OperationalMetricsSnapshot Snapshot()
    {
        var executions = Math.Max(0, Interlocked.Read(ref _executions));
        var totalLatency = Math.Max(0, Interlocked.Read(ref _latencyMsTotal));
        var totalSlippage = Math.Max(0, Interlocked.Read(ref _slippageBpsTotal));

        decimal exposureArs;
        decimal exposureUsd;
        lock (_exposureLock)
        {
            exposureArs = _exposureArs;
            exposureUsd = _exposureUsd;
        }

        return new OperationalMetricsSnapshot
        {
            LastHeartbeatUtc = new DateTime(Interlocked.Read(ref _heartbeatTicks), DateTimeKind.Utc),
            Executions = executions,
            AverageLatencyMs = executions == 0 ? 0 : (double)totalLatency / executions,
            AverageSlippageBps = executions == 0 ? 0 : (double)totalSlippage / executions,
            ExposureArs = exposureArs,
            ExposureUsd = exposureUsd
        };
    }
}
