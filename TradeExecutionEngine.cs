using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using FinancialTerminal.Core.Configuration;
using FinancialTerminal.Core.Interfaces;
using FinancialTerminal.Core.Models;
using FinancialTerminal.Data.Repositories;

namespace FinancialTerminal.App.Trading;

public sealed class TradeExecutionEngine : ITradeExecutionEngine
{
    private readonly IRiskManager _riskManager;
    private readonly ICapitalReservoir _capitalReservoir;
    private readonly IExecutionQueue _executionQueue;
    private readonly IOperationalMetrics _metrics;
    private readonly OrderRepository _orders;
    private readonly DatabaseRepository _db;
    private readonly AppSettings _settings;
    private readonly ILogger<TradeExecutionEngine> _logger;
    private int _consecutiveFailures;

    public TradeExecutionEngine(
        IRiskManager riskManager,
        ICapitalReservoir capitalReservoir,
        IExecutionQueue executionQueue,
        IOperationalMetrics metrics,
        OrderRepository orders,
        DatabaseRepository db,
        AppSettings settings,
        ILogger<TradeExecutionEngine> logger)
    {
        _riskManager = riskManager;
        _capitalReservoir = capitalReservoir;
        _executionQueue = executionQueue;
        _metrics = metrics;
        _orders = orders;
        _db = db;
        _settings = settings;
        _logger = logger;
    }

    public async Task<OrderResult?> ExecuteOpportunityAsync(
        IStrategy strategy,
        Opportunity opportunity,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var hadExecutionFailure = false;
        var lastError = string.Empty;

        var executionId = $"exec-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        var snapshot = new ManagedOrderSnapshot
        {
            ExecutionId = executionId,
            StrategyName = strategy.Name,
            Ticker = opportunity.Ticker,
            Side = OrderSide.Buy,
            Settlement = opportunity.BuySettlement,
            Quantity = opportunity.Quantity,
            Price = opportunity.BuyPrice,
            State = ManagedOrderState.Created,
            UpdatedAtUtc = DateTime.UtcNow,
        };

        await _orders.SaveSnapshotAsync(snapshot);
        await _orders.AppendEventAsync(executionId, "created", "Opportunity registrada", JsonSerializer.Serialize(opportunity));

        var intents = await BuildIntentsAsync(strategy, opportunity, ct);
        if (intents.Count == 0)
        {
            snapshot = snapshot.Transition(ManagedOrderSignal.Fail, DateTime.UtcNow, error: "Sin intents para ejecutar");
            await _orders.SaveSnapshotAsync(snapshot);
            await _orders.AppendEventAsync(executionId, "failed", "No se generaron trade intents");
            return new OrderResult
            {
                Success = false,
                Ticker = opportunity.Ticker,
                Side = OrderSide.Buy,
                Settlement = opportunity.BuySettlement,
                Status = OrderStatus.Rejected,
                ErrorMessage = "No trade intents generated"
            };
        }

        foreach (var intent in intents)
        {
            var riskResult = await _riskManager.EvaluateAsync(new RiskCheckRequest
            {
                StrategyName = intent.StrategyName,
                Ticker = intent.Ticker,
                Side = intent.Side,
                Settlement = intent.Settlement,
                Quantity = intent.Quantity,
                Price = intent.Price,
                ExpectedNetProfit = intent.ExpectedNetProfit
            }, ct);

            if (!riskResult.Allowed)
            {
                snapshot = snapshot.Transition(ManagedOrderSignal.RejectRisk, DateTime.UtcNow, error: riskResult.Reason);
                await _orders.SaveSnapshotAsync(snapshot);
                await _orders.AppendEventAsync(executionId, "risk_rejected", riskResult.Reason);
                return new OrderResult
                {
                    Success = false,
                    Ticker = intent.Ticker,
                    Side = intent.Side,
                    Settlement = intent.Settlement,
                    Status = OrderStatus.Rejected,
                    ErrorMessage = riskResult.Reason,
                };
            }
        }

        snapshot = snapshot.Transition(ManagedOrderSignal.ApproveRisk, DateTime.UtcNow);
        await _orders.SaveSnapshotAsync(snapshot);
        await _orders.AppendEventAsync(executionId, "risk_approved", $"Risk checks OK. intents={intents.Count}");

        // Reserve capital for all intents to prevent double-commitment across strategies
        var totalNotional = intents.Sum(i => i.Quantity * i.Price);
        var reservation = _capitalReservoir.TryReserve(totalNotional, intents[0].StrategyName);
        if (reservation is null)
        {
            snapshot = snapshot.Transition(ManagedOrderSignal.Cancel, DateTime.UtcNow, error: "Capital insuficiente (reservado por otras estrategias)");
            await _orders.SaveSnapshotAsync(snapshot);
            await _orders.AppendEventAsync(executionId, "capital_rejected", $"No se pudo reservar {totalNotional:N2}. Disponible: {_capitalReservoir.AvailableCapital:N2}");
            return new OrderResult
            {
                Success = false,
                Ticker = opportunity.Ticker,
                Side = OrderSide.Buy,
                Settlement = opportunity.BuySettlement,
                Status = OrderStatus.Rejected,
                ErrorMessage = $"Capital insuficiente: necesario {totalNotional:N2}, disponible {_capitalReservoir.AvailableCapital:N2}"
            };
        }

        snapshot = snapshot.Transition(ManagedOrderSignal.Submit, DateTime.UtcNow);
        await _orders.SaveSnapshotAsync(snapshot);
        await _orders.AppendEventAsync(executionId, "submitted", $"Enqueued intents para ejecucion. Capital reservado: {totalNotional:N2}");

        var results = new List<OrderResult>(intents.Count);
        try
        {
            for (var i = 0; i < intents.Count; i++)
            {
                var intent = intents[i];
                await _orders.AppendEventAsync(executionId, "intent_queued", $"{intent.Side} {intent.Ticker} qty={intent.Quantity} px={intent.Price}");

                var intentStopwatch = Stopwatch.StartNew();
                var result = await _executionQueue.EnqueueAsync(intent, ct);
                intentStopwatch.Stop();
                results.Add(result);
                await _orders.SaveFillAsync(executionId, result);
                _metrics.RecordExecution(intent, result, intentStopwatch.ElapsedMilliseconds);

                if (result.Success)
                {
                    if (result.Status == OrderStatus.PartiallyFilled)
                    {
                        snapshot = snapshot.Transition(ManagedOrderSignal.PartialFill, DateTime.UtcNow, brokerOrderId: result.OrderId);
                        await _orders.AppendEventAsync(executionId, "partial_fill", $"Intent {i + 1}/{intents.Count} parcial. Qty={result.ExecutedQuantity}");
                    }

                    (decimal Gross, decimal Net, decimal Fees) pnlAllocation = i == intents.Count - 1
                        ? ScaleExpectedPnL(opportunity, result.ExecutedQuantity)
                        : (0m, 0m, 0m);

                    var tradeId = await _db.InsertTradeAsync(intent.StrategyName, result, pnlAllocation.Gross, pnlAllocation.Net, pnlAllocation.Fees);
                    if (tradeId == 0 && !string.IsNullOrWhiteSpace(result.OrderId))
                    {
                        await _orders.AppendEventAsync(executionId, "trade_deduplicated", $"Trade existente para order_id={result.OrderId}");
                    }

                    _riskManager.RegisterExecution(result);
                }
                else
                {
                    hadExecutionFailure = true;
                    lastError = result.ErrorMessage ?? "Error de ejecucion";
                    snapshot = snapshot.Transition(ManagedOrderSignal.Fail, DateTime.UtcNow, brokerOrderId: result.OrderId, error: lastError);
                    await _orders.AppendEventAsync(executionId, "failed", $"Intent {i + 1}/{intents.Count}: {lastError}");
                    await _orders.SaveSnapshotAsync(snapshot);
                    break;
                }

                await _orders.SaveSnapshotAsync(snapshot);
            }

            var final = BuildFinalResult(opportunity, results);
            if (final.Success)
            {
                if (results.Any(r => r.Status == OrderStatus.PartiallyFilled))
                {
                    if (snapshot.State != ManagedOrderState.PartiallyFilled)
                    {
                        snapshot = snapshot.Transition(ManagedOrderSignal.PartialFill, DateTime.UtcNow, brokerOrderId: final.OrderId);
                    }
                }
                else
                {
                    snapshot = snapshot.Transition(ManagedOrderSignal.Fill, DateTime.UtcNow, brokerOrderId: final.OrderId);
                }

                await _orders.AppendEventAsync(executionId, "filled", "Todos los intents ejecutados");
                RegisterExecutionSuccess();
            }

            await _orders.SaveSnapshotAsync(snapshot);
            return final;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ejecutando oportunidad {ExecutionId}", executionId);
            snapshot = snapshot.Transition(ManagedOrderSignal.Fail, DateTime.UtcNow, error: ex.Message);
            await _orders.SaveSnapshotAsync(snapshot);
            await _orders.AppendEventAsync(executionId, "exception", ex.Message);

            hadExecutionFailure = true;
            lastError = ex.Message;
            return new OrderResult
            {
                Success = false,
                Ticker = opportunity.Ticker,
                Side = OrderSide.Buy,
                Settlement = opportunity.BuySettlement,
                Status = OrderStatus.Rejected,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            _capitalReservoir.Release(reservation);
            stopwatch.Stop();
            ApplyLatencyGuard(stopwatch.ElapsedMilliseconds, executionId);
            if (hadExecutionFailure)
            {
                RegisterExecutionFailure(lastError, executionId);
            }
        }
    }

    private static Task<IReadOnlyList<TradeIntent>> BuildIntentsAsync(IStrategy strategy, Opportunity opportunity, CancellationToken ct)
    {
        if (strategy is ITradeIntentStrategy intentStrategy)
            return intentStrategy.BuildTradeIntentsAsync(opportunity, ct);

        var fallback = new List<TradeIntent>
        {
            new()
            {
                StrategyName = strategy.Name,
                Ticker = opportunity.Ticker,
                Side = OrderSide.Buy,
                Settlement = opportunity.BuySettlement,
                Quantity = opportunity.Quantity,
                Price = opportunity.BuyPrice,
                ExpectedNetProfit = opportunity.ExpectedNetProfit
            },
            new()
            {
                StrategyName = strategy.Name,
                Ticker = opportunity.Ticker,
                Side = OrderSide.Sell,
                Settlement = opportunity.SellSettlement,
                Quantity = opportunity.Quantity,
                Price = opportunity.SellPrice,
                ExpectedNetProfit = opportunity.ExpectedNetProfit
            }
        };

        return Task.FromResult<IReadOnlyList<TradeIntent>>(fallback);
    }

    private static OrderResult BuildFinalResult(Opportunity opportunity, IReadOnlyList<OrderResult> results)
    {
        if (results.Count == 0)
        {
            return new OrderResult
            {
                Success = false,
                Ticker = opportunity.Ticker,
                Side = OrderSide.Buy,
                Settlement = opportunity.BuySettlement,
                Status = OrderStatus.Rejected,
                ErrorMessage = "No se ejecuto ningun intent"
            };
        }

        var last = results[^1];
        if (results.Any(r => !r.Success))
            return last;

        return last with
        {
            Success = true,
            Status = results.Any(r => r.Status == OrderStatus.PartiallyFilled)
                ? OrderStatus.PartiallyFilled
                : OrderStatus.Filled
        };
    }

    private (decimal Gross, decimal Net, decimal Fees) ScaleExpectedPnL(Opportunity opportunity, decimal executedQuantity)
    {
        if (opportunity.Quantity <= 0 || executedQuantity <= 0)
            return (0, 0, 0);

        var ratio = Math.Min(1m, executedQuantity / opportunity.Quantity);
        return (
            Math.Round(opportunity.ExpectedGrossProfit * ratio, 2),
            Math.Round(opportunity.ExpectedNetProfit * ratio, 2),
            Math.Round(opportunity.FeesTotal * ratio, 2));
    }

    private void RegisterExecutionSuccess()
    {
        Interlocked.Exchange(ref _consecutiveFailures, 0);
    }

    private void RegisterExecutionFailure(string reason, string executionId)
    {
        var streak = Interlocked.Increment(ref _consecutiveFailures);
        _logger.LogError("Execution failure [{ExecutionId}] streak={Streak}: {Reason}", executionId, streak, reason);

        var threshold = _settings.Risk.MaxConsecutiveExecutionFailures;
        if (threshold > 0 && streak >= threshold && !_riskManager.IsKillSwitchActive)
        {
            _riskManager.ActivateKillSwitch(
                $"Auto kill-switch por {streak} fallas consecutivas de ejecucion (umbral={threshold}). Ultimo error: {reason}");
        }
    }

    private void ApplyLatencyGuard(long latencyMs, string executionId)
    {
        if (!_settings.Risk.EnableLatencyKillSwitch)
            return;

        var thresholdMs = _settings.Risk.MaxExecutionLatencyMs;
        if (thresholdMs <= 0 || latencyMs <= thresholdMs)
            return;

        _logger.LogError("Execution latency breach [{ExecutionId}]: {LatencyMs}ms > {ThresholdMs}ms", executionId, latencyMs, thresholdMs);
        if (!_riskManager.IsKillSwitchActive)
        {
            _riskManager.ActivateKillSwitch(
                $"Auto kill-switch por latencia de ejecucion {latencyMs}ms (umbral={thresholdMs}ms).");
        }
    }
}
