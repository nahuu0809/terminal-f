using Microsoft.Extensions.Logging;
using FinancialTerminal.Core.Interfaces;
using FinancialTerminal.Core.Models;
using FinancialTerminal.Data.Repositories;

namespace FinancialTerminal.App.Trading;

public sealed class OrderReconciliator : IOrderReconciliator
{
    private readonly OrderRepository _orders;
    private readonly DatabaseRepository _db;
    private readonly IOrderExecutor _orderExecutor;
    private readonly IRiskManager _riskManager;
    private readonly ILogger<OrderReconciliator> _logger;

    public OrderReconciliator(
        OrderRepository orders,
        DatabaseRepository db,
        IOrderExecutor orderExecutor,
        IRiskManager riskManager,
        ILogger<OrderReconciliator> logger)
    {
        _orders = orders;
        _db = db;
        _orderExecutor = orderExecutor;
        _riskManager = riskManager;
        _logger = logger;
    }

    public async Task<OrderReconciliationReport> ReconcileOpenOrdersAsync(bool queryBroker, CancellationToken ct = default)
    {
        var open = await _orders.GetOpenOrdersAsync();
        var report = new OrderReconciliationReport { Scanned = open.Count };

        foreach (var snapshot in open)
        {
            ct.ThrowIfCancellationRequested();

            switch (snapshot.State)
            {
                case ManagedOrderState.Created:
                    await RejectStaleCreatedAsync(snapshot, queryBroker);
                    report = report with { Updated = report.Updated + 1, Failed = report.Failed + 1 };
                    break;
                case ManagedOrderState.RiskApproved:
                    await CancelStaleRiskApprovedAsync(snapshot, queryBroker);
                    report = report with { Updated = report.Updated + 1, Cancelled = report.Cancelled + 1 };
                    break;
                case ManagedOrderState.Submitted:
                case ManagedOrderState.PartiallyFilled:
                    var outcome = await ReconcileSubmittedAsync(snapshot, queryBroker, ct);
                    report = outcome switch
                    {
                        ManagedOrderState.Filled => report with { Updated = report.Updated + 1, Filled = report.Filled + 1 },
                        ManagedOrderState.Failed => report with { Updated = report.Updated + 1, Failed = report.Failed + 1 },
                        ManagedOrderState.Cancelled => report with { Updated = report.Updated + 1, Cancelled = report.Cancelled + 1 },
                        _ => report with { Pending = report.Pending + 1 },
                    };
                    break;
            }
        }

        await _riskManager.WarmupAsync(ct);
        _logger.LogWarning(
            "Reconciliación órdenes: scanned={Scanned} updated={Updated} filled={Filled} failed={Failed} cancelled={Cancelled} pending={Pending}",
            report.Scanned, report.Updated, report.Filled, report.Failed, report.Cancelled, report.Pending);
        return report;
    }

    private async Task RejectStaleCreatedAsync(ManagedOrderSnapshot snapshot, bool queryBroker)
    {
        var reason = queryBroker
            ? "Orden en estado Created al reconectar. Marcada como no ejecutable."
            : "Orden en estado Created al iniciar. Marcada para revisión.";

        var next = snapshot.Transition(ManagedOrderSignal.RejectRisk, DateTime.UtcNow, error: reason);
        await _orders.SaveSnapshotAsync(next);
        await _orders.AppendEventAsync(snapshot.ExecutionId, "reconciled_reject", reason);
    }

    private async Task CancelStaleRiskApprovedAsync(ManagedOrderSnapshot snapshot, bool queryBroker)
    {
        var reason = queryBroker
            ? "Orden RiskApproved sin envío broker al reconectar. Cancelada."
            : "Orden RiskApproved al iniciar. Cancelada por seguridad.";

        var next = snapshot.Transition(ManagedOrderSignal.Cancel, DateTime.UtcNow, error: reason);
        await _orders.SaveSnapshotAsync(next);
        await _orders.AppendEventAsync(snapshot.ExecutionId, "reconciled_cancel", reason);
    }

    private async Task<ManagedOrderState> ReconcileSubmittedAsync(ManagedOrderSnapshot snapshot, bool queryBroker, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(snapshot.BrokerOrderId))
        {
            var fail = snapshot.Transition(ManagedOrderSignal.Fail, DateTime.UtcNow, error: "Submitted sin broker_order_id");
            await _orders.SaveSnapshotAsync(fail);
            await _orders.AppendEventAsync(snapshot.ExecutionId, "reconciled_fail", "Submitted sin broker_order_id");
            return fail.State;
        }

        if (!queryBroker)
        {
            await _orders.AppendEventAsync(snapshot.ExecutionId, "reconciled_pending", "Validación con broker omitida en startup");
            return snapshot.State;
        }

        OrderResult? brokerStatus;
        try
        {
            brokerStatus = await _orderExecutor.GetOrderStatusAsync(snapshot.BrokerOrderId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error consultando estado de orden {OrderId}", snapshot.BrokerOrderId);
            await _orders.AppendEventAsync(snapshot.ExecutionId, "reconciled_error", ex.Message);
            return snapshot.State;
        }

        if (brokerStatus is null)
        {
            await _orders.AppendEventAsync(snapshot.ExecutionId, "reconciled_pending", "Broker sin estado para la orden");
            return snapshot.State;
        }

        if (brokerStatus.Status is OrderStatus.New or OrderStatus.PendingNew)
        {
            await _orders.AppendEventAsync(snapshot.ExecutionId, "reconciled_pending", $"Broker status={brokerStatus.Status}");
            return snapshot.State;
        }

        if (brokerStatus.Status == OrderStatus.PartiallyFilled)
        {
            var partialSnapshot = snapshot.State == ManagedOrderState.PartiallyFilled
                ? snapshot with { UpdatedAtUtc = DateTime.UtcNow }
                : snapshot.Transition(ManagedOrderSignal.PartialFill, DateTime.UtcNow, brokerOrderId: snapshot.BrokerOrderId);

            await _orders.SaveSnapshotAsync(partialSnapshot);
            await _orders.SaveFillAsync(snapshot.ExecutionId, brokerStatus);

            if (!await _orders.HasTradeForOrderIdAsync(snapshot.BrokerOrderId))
            {
                await _db.InsertTradeAsync(snapshot.StrategyName, brokerStatus);
            }

            _riskManager.RegisterExecution(brokerStatus);
            await _orders.AppendEventAsync(snapshot.ExecutionId, "reconciled_partial", $"Orden parcial. Qty={brokerStatus.ExecutedQuantity}");
            return partialSnapshot.State;
        }

        if (brokerStatus.Status == OrderStatus.Filled)
        {
            var filled = snapshot.Transition(ManagedOrderSignal.Fill, DateTime.UtcNow, brokerOrderId: snapshot.BrokerOrderId);
            await _orders.SaveSnapshotAsync(filled);
            await _orders.SaveFillAsync(snapshot.ExecutionId, brokerStatus);

            if (!await _orders.HasTradeForOrderIdAsync(snapshot.BrokerOrderId))
            {
                await _db.InsertTradeAsync(snapshot.StrategyName, brokerStatus);
            }

            _riskManager.RegisterExecution(brokerStatus);
            await _orders.AppendEventAsync(snapshot.ExecutionId, "reconciled_filled", "Orden marcada Filled tras reconciliación");
            return filled.State;
        }

        if (brokerStatus.Status == OrderStatus.Cancelled)
        {
            var cancelled = snapshot.Transition(ManagedOrderSignal.Cancel, DateTime.UtcNow, brokerOrderId: snapshot.BrokerOrderId, error: "Cancelada en broker");
            await _orders.SaveSnapshotAsync(cancelled);
            await _orders.SaveFillAsync(snapshot.ExecutionId, brokerStatus);
            await _orders.AppendEventAsync(snapshot.ExecutionId, "reconciled_cancelled", "Orden cancelada en broker");
            return cancelled.State;
        }

        var failed = snapshot.Transition(ManagedOrderSignal.Fail, DateTime.UtcNow, brokerOrderId: snapshot.BrokerOrderId, error: $"Status broker={brokerStatus.Status}");
        await _orders.SaveSnapshotAsync(failed);
        await _orders.SaveFillAsync(snapshot.ExecutionId, brokerStatus);
        await _orders.AppendEventAsync(snapshot.ExecutionId, "reconciled_failed", $"Orden marcada failed por status={brokerStatus.Status}");
        return failed.State;
    }
}
