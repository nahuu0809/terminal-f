using FinancialTerminal.Core.Interfaces;
using FinancialTerminal.Data.Repositories;

namespace FinancialTerminal.App.Trading;

public sealed class TradingModeGuard : ITradingModeGuard
{
    private readonly OrderRepository _orders;
    private readonly IExecutionQueue _queue;

    public TradingModeGuard(OrderRepository orders, IExecutionQueue queue)
    {
        _orders = orders;
        _queue = queue;
    }

    public async Task<TradingModeGuardResult> CanTogglePaperModeAsync(int activeBotsCount, CancellationToken ct = default)
    {
        if (activeBotsCount > 0)
            return TradingModeGuardResult.Block("No se puede cambiar Paper Mode con bots activos");

        if (_queue.PendingCount > 0)
            return TradingModeGuardResult.Block("No se puede cambiar Paper Mode con la cola de ejecucion pendiente");

        if (await _orders.HasInFlightOrdersAsync())
            return TradingModeGuardResult.Block("No se puede cambiar Paper Mode con ordenes en estado Submitted/PartiallyFilled");

        return TradingModeGuardResult.Allow();
    }
}
