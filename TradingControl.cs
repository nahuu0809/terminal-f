using System.Collections.ObjectModel;
using FinancialTerminal.Core.Models;

namespace FinancialTerminal.Core.Interfaces;

public sealed record RiskCheckRequest
{
    public required string StrategyName { get; init; }
    public required string Ticker { get; init; }
    public required OrderSide Side { get; init; }
    public required Settlement Settlement { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal Price { get; init; }
    public decimal ExpectedNetProfit { get; init; }
}

public sealed record RiskCheckResult(bool Allowed, string Reason)
{
    public static RiskCheckResult Approve() => new(true, "OK");
    public static RiskCheckResult Reject(string reason) => new(false, reason);
}

public interface IRiskManager
{
    bool IsKillSwitchActive { get; }
    string KillSwitchReason { get; }

    Task WarmupAsync(CancellationToken ct = default);
    Task<RiskCheckResult> EvaluateAsync(RiskCheckRequest request, CancellationToken ct = default);
    void RegisterExecution(OrderResult result);
    void ActivateKillSwitch(string reason);
    void ResetKillSwitch();
}

public interface ITradeExecutionEngine
{
    Task<OrderResult?> ExecuteOpportunityAsync(
        IStrategy strategy,
        Opportunity opportunity,
        CancellationToken ct = default);
}

public sealed record TradeIntent
{
    public required string StrategyName { get; init; }
    public required string Ticker { get; init; }
    public required OrderSide Side { get; init; }
    public required Settlement Settlement { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal Price { get; init; }
    public decimal ExpectedNetProfit { get; init; }
    public AssetType AssetType { get; init; } = AssetType.Accion;
    public Dictionary<string, object> Metadata { get; init; } = [];
}

public interface IExecutionQueue
{
    int PendingCount { get; }
    Task<OrderResult> EnqueueAsync(TradeIntent intent, CancellationToken ct = default);
}

public sealed record TradingModeGuardResult(bool Allowed, string Reason)
{
    public static TradingModeGuardResult Allow() => new(true, "OK");
    public static TradingModeGuardResult Block(string reason) => new(false, reason);
}

public interface ITradingModeGuard
{
    Task<TradingModeGuardResult> CanTogglePaperModeAsync(int activeBotsCount, CancellationToken ct = default);
}

public sealed record OperationalMetricsSnapshot
{
    public DateTime LastHeartbeatUtc { get; init; } = DateTime.UtcNow;
    public long Executions { get; init; }
    public double AverageLatencyMs { get; init; }
    public double AverageSlippageBps { get; init; }
    public decimal ExposureArs { get; init; }
    public decimal ExposureUsd { get; init; }
}

public interface IOperationalMetrics
{
    void RecordExecution(TradeIntent intent, OrderResult result, long latencyMs);
    OperationalMetricsSnapshot Snapshot();
}

public sealed record OrderReconciliationReport
{
    public int Scanned { get; init; }
    public int Updated { get; init; }
    public int Failed { get; init; }
    public int Filled { get; init; }
    public int Cancelled { get; init; }
    public int Pending { get; init; }
}

public interface IOrderReconciliator
{
    Task<OrderReconciliationReport> ReconcileOpenOrdersAsync(bool queryBroker, CancellationToken ct = default);
}

// ─── Capital Reservation ────────────────────────────────────────────────────

public sealed record ReservationToken(Guid Id, decimal Amount, string StrategyName, DateTime CreatedAtUtc);

public interface ICapitalReservoir
{
    decimal TotalCapital { get; }
    decimal ReservedCapital { get; }
    decimal AvailableCapital { get; }

    ReservationToken? TryReserve(decimal amount, string strategyName);
    void Release(ReservationToken token);
}

public enum ManagedOrderState
{
    Created,
    RiskApproved,
    RiskRejected,
    Submitted,
    PartiallyFilled,
    Filled,
    Failed,
    Cancelled,
}

public enum ManagedOrderSignal
{
    ApproveRisk,
    RejectRisk,
    Submit,
    PartialFill,
    Fill,
    Fail,
    Cancel,
}

public sealed record ManagedOrderSnapshot
{
    public required string ExecutionId { get; init; }
    public required string StrategyName { get; init; }
    public required string Ticker { get; init; }
    public required OrderSide Side { get; init; }
    public required Settlement Settlement { get; init; }
    public decimal Quantity { get; init; }
    public decimal Price { get; init; }
    public ManagedOrderState State { get; init; } = ManagedOrderState.Created;
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
    public string? BrokerOrderId { get; init; }
    public string? Error { get; init; }

    public ManagedOrderSnapshot Transition(
        ManagedOrderSignal signal,
        DateTime timestampUtc,
        string? brokerOrderId = null,
        string? error = null)
    {
        var next = OrderStateMachine.Transition(State, signal);
        return this with
        {
            State = next,
            UpdatedAtUtc = timestampUtc,
            BrokerOrderId = brokerOrderId ?? BrokerOrderId,
            Error = error ?? Error,
        };
    }
}

public static class OrderStateMachine
{
    private static readonly ReadOnlyDictionary<ManagedOrderState, HashSet<ManagedOrderSignal>> Transitions =
        new(new Dictionary<ManagedOrderState, HashSet<ManagedOrderSignal>>
        {
            [ManagedOrderState.Created] = [ManagedOrderSignal.ApproveRisk, ManagedOrderSignal.RejectRisk, ManagedOrderSignal.Cancel],
            [ManagedOrderState.RiskApproved] = [ManagedOrderSignal.Submit, ManagedOrderSignal.Cancel],
            [ManagedOrderState.RiskRejected] = [],
            [ManagedOrderState.Submitted] = [ManagedOrderSignal.PartialFill, ManagedOrderSignal.Fill, ManagedOrderSignal.Fail, ManagedOrderSignal.Cancel],
            [ManagedOrderState.PartiallyFilled] = [ManagedOrderSignal.PartialFill, ManagedOrderSignal.Fill, ManagedOrderSignal.Fail, ManagedOrderSignal.Cancel],
            [ManagedOrderState.Filled] = [],
            [ManagedOrderState.Failed] = [],
            [ManagedOrderState.Cancelled] = [],
        });

    public static ManagedOrderState Transition(ManagedOrderState current, ManagedOrderSignal signal)
    {
        if (!Transitions.TryGetValue(current, out var validSignals) || !validSignals.Contains(signal))
        {
            throw new InvalidOperationException($"Invalid transition: {current} -> {signal}");
        }

        return signal switch
        {
            ManagedOrderSignal.ApproveRisk => ManagedOrderState.RiskApproved,
            ManagedOrderSignal.RejectRisk => ManagedOrderState.RiskRejected,
            ManagedOrderSignal.Submit => ManagedOrderState.Submitted,
            ManagedOrderSignal.PartialFill => ManagedOrderState.PartiallyFilled,
            ManagedOrderSignal.Fill => ManagedOrderState.Filled,
            ManagedOrderSignal.Fail => ManagedOrderState.Failed,
            ManagedOrderSignal.Cancel => ManagedOrderState.Cancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(signal), signal, null)
        };
    }
}
