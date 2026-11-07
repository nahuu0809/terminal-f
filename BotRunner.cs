using Microsoft.Extensions.Logging;
using FinancialTerminal.Core.Interfaces;
using FinancialTerminal.Core.Models;

namespace FinancialTerminal.Strategies.Common;

/// <summary>
/// Base para todas las estrategias automatizadas.
/// </summary>
public abstract class BaseStrategy : IStrategy
{
    protected readonly IMarketDataProvider MarketData;
    protected readonly ILogger Logger;

    public abstract string Name { get; }
    public bool IsEnabled { get; set; } = true;
    public bool IsRunning { get; protected set; }

    protected BaseStrategy(IMarketDataProvider marketData, ILogger logger)
    {
        MarketData = marketData;
        Logger = logger;
    }

    public abstract Task<Opportunity?> ScanAsync(CancellationToken ct = default);

    internal void SetRunning(bool running) => IsRunning = running;

    public virtual Task<OrderResult?> ExecuteAsync(Opportunity opportunity, CancellationToken ct = default)
    {
        Logger.LogError("[{Strategy}] ExecuteAsync directo no permitido. Debe pasar por ITradeExecutionEngine.", Name);
        return Task.FromResult<OrderResult?>(new OrderResult
        {
            Success = false,
            Ticker = opportunity.Ticker,
            Side = OrderSide.Buy,
            Settlement = opportunity.BuySettlement,
            Status = OrderStatus.Rejected,
            ErrorMessage = "Direct strategy execution disabled"
        });
    }
}

public abstract class IntentBasedStrategy : BaseStrategy, ITradeIntentStrategy
{
    protected IntentBasedStrategy(IMarketDataProvider marketData, ILogger logger) : base(marketData, logger)
    {
    }

    public virtual Task<IReadOnlyList<TradeIntent>> BuildTradeIntentsAsync(Opportunity opportunity, CancellationToken ct = default)
    {
        var intents = new List<TradeIntent>
        {
            new()
            {
                StrategyName = Name,
                Ticker = opportunity.Ticker,
                Side = OrderSide.Buy,
                Settlement = opportunity.BuySettlement,
                Quantity = opportunity.Quantity,
                Price = opportunity.BuyPrice,
                ExpectedNetProfit = opportunity.ExpectedNetProfit
            },
            new()
            {
                StrategyName = Name,
                Ticker = opportunity.Ticker,
                Side = OrderSide.Sell,
                Settlement = opportunity.SellSettlement,
                Quantity = opportunity.Quantity,
                Price = opportunity.SellPrice,
                ExpectedNetProfit = opportunity.ExpectedNetProfit
            }
        };

        return Task.FromResult<IReadOnlyList<TradeIntent>>(intents);
    }
}

/// <summary>
/// Runner generico que ejecuta cualquier IStrategy en loop con on/off.
/// </summary>
public sealed class BotRunner : IBotRunner
{
    private readonly IStrategy _strategy;
    private readonly ILogger<BotRunner> _logger;
    private readonly ITradeExecutionEngine _executionEngine;
    private readonly int _pollingIntervalMs;

    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public string StrategyName => _strategy.Name;
    public bool IsRunning => _runTask is not null && !_runTask.IsCompleted;
    public DateTime? LastScanTime { get; private set; }
    public Opportunity? LastOpportunity { get; private set; }

    public event EventHandler<BotLogEventArgs>? OnLog;
    public event EventHandler<Opportunity>? OnOpportunityFound;
    public event EventHandler<OrderResult>? OnOrderExecuted;

    public BotRunner(
        IStrategy strategy,
        ILogger<BotRunner> logger,
        ITradeExecutionEngine executionEngine,
        int pollingIntervalMs = 5000)
    {
        _strategy = strategy;
        _logger = logger;
        _executionEngine = executionEngine;
        _pollingIntervalMs = pollingIntervalMs;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runTask = Task.Run(() => RunLoop(_cts.Token), _cts.Token);
        if (_strategy is BaseStrategy baseStrategy)
            baseStrategy.SetRunning(true);

        EmitLog($"Bot '{StrategyName}' iniciado", BotLogEventArgs.LogLevel.Info);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;

        _cts?.Cancel();
        try
        {
            if (_runTask is not null) await _runTask;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_strategy is BaseStrategy baseStrategy)
                baseStrategy.SetRunning(false);
            _cts?.Dispose();
            _cts = null;
            _runTask = null;
        }

        EmitLog($"Bot '{StrategyName}' detenido", BotLogEventArgs.LogLevel.Info);
    }

    private async Task RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                LastScanTime = DateTime.UtcNow;

                var opportunity = await _strategy.ScanAsync(ct);
                if (opportunity is not null)
                {
                    LastOpportunity = opportunity;
                    OnOpportunityFound?.Invoke(this, opportunity);
                    EmitLog(
                        $"Oportunidad: {opportunity.Ticker} | Compra={opportunity.BuyPrice:F2} " +
                        $"Venta={opportunity.SellPrice:F2} | Neta=${opportunity.ExpectedNetProfit:N2}",
                        BotLogEventArgs.LogLevel.Info);

                    var result = await _executionEngine.ExecuteOpportunityAsync(_strategy, opportunity, ct);
                    if (result is not null)
                    {
                        OnOrderExecuted?.Invoke(this, result);
                        EmitLog(
                            result.Success
                                ? $"Orden ejecutada: {result.Ticker} {result.Side} id={result.OrderId}"
                                : $"Orden fallo: {result.ErrorMessage}",
                            result.Success ? BotLogEventArgs.LogLevel.Info : BotLogEventArgs.LogLevel.Error);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                EmitLog($"Error en scan loop: {ex.Message}", BotLogEventArgs.LogLevel.Error);
                _logger.LogError(ex, "Error en BotRunner loop para {Strategy}", StrategyName);
            }

            await Task.Delay(_pollingIntervalMs, ct);
        }
    }

    private void EmitLog(string message, BotLogEventArgs.LogLevel level)
    {
        OnLog?.Invoke(this, new BotLogEventArgs { Message = message, Level = level });
    }
}
