using FinancialTerminal.Core.Interfaces;
using FinancialTerminal.Core.Models;

namespace FinancialTerminal.Data.Repositories;

public sealed class OrderRepository
{
    private readonly DatabaseRepository _db;

    public OrderRepository(DatabaseRepository db)
    {
        _db = db;
    }

    public Task SaveSnapshotAsync(ManagedOrderSnapshot snapshot) =>
        _db.UpsertManagedOrderAsync(snapshot);

    public Task AppendEventAsync(string executionId, string eventType, string message, string? payload = null) =>
        _db.AppendOrderEventAsync(executionId, eventType, message, payload);

    public Task SaveFillAsync(string executionId, OrderResult result) =>
        _db.InsertOrderFillAsync(executionId, result);

    public Task MarkStateAsync(string executionId, ManagedOrderState state, string? brokerOrderId = null, string? error = null) =>
        _db.MarkManagedOrderStateAsync(executionId, state, brokerOrderId, error);

    public Task<IReadOnlyList<ManagedOrderSnapshot>> GetOpenOrdersAsync() =>
        _db.GetActiveManagedOrdersAsync();

    public Task<bool> HasInFlightOrdersAsync() =>
        _db.HasInFlightManagedOrdersAsync();

    public Task<bool> HasTradeForOrderIdAsync(string? orderId) =>
        _db.HasTradeForOrderIdAsync(orderId);
}
