using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using FinancialTerminal.Core.Configuration;
using FinancialTerminal.Core.Interfaces;
using FinancialTerminal.Core.Models;
using FinancialTerminal.Data.Repositories;

namespace FinancialTerminal.App.Trading;

public sealed class RiskManager : IRiskManager
{
    private readonly AppSettings _settings;
    private readonly DatabaseRepository _db;
    private readonly ILogger<RiskManager> _logger;
    private readonly ConcurrentDictionary<string, decimal> _netPositions = new(StringComparer.OrdinalIgnoreCase);
    private string _killSwitchReason = string.Empty;

    public bool IsKillSwitchActive => !string.IsNullOrWhiteSpace(_killSwitchReason);
    public string KillSwitchReason => _killSwitchReason;

    public RiskManager(AppSettings settings, DatabaseRepository db, ILogger<RiskManager> logger)
    {
        _settings = settings;
        _db = db;
        _logger = logger;
    }

    public async Task WarmupAsync(CancellationToken ct = default)
    {
        var snapshot = await _db.GetNetPositionsSnapshotAsync();
        _netPositions.Clear();
        foreach (var (ticker, qty) in snapshot)
        {
            _netPositions[ticker] = qty;
        }
        _logger.LogInformation("Risk warmup completo. Tickers cargados: {Count}", _netPositions.Count);
    }

    public async Task<RiskCheckResult> EvaluateAsync(RiskCheckRequest request, CancellationToken ct = default)
    {
        if (IsKillSwitchActive)
            return RiskCheckResult.Reject($"Kill switch activo: {KillSwitchReason}");

        if (_settings.Risk.StopTradingFlag)
            return RiskCheckResult.Reject("StopTradingFlag habilitado en configuración");

        if (request.Quantity <= 0 || request.Price <= 0)
            return RiskCheckResult.Reject("Cantidad/precio inválidos");

        var notional = request.Quantity * request.Price;
        var maxNotional = _settings.Trading.InitialCapital * (_settings.Trading.MaxCapitalPerOperationPercent / 100m);
        if (maxNotional > 0 && notional > maxNotional)
            return RiskCheckResult.Reject($"Capital por trade excedido ({notional:N2} > {maxNotional:N2})");

        // Max loss per trade check: reject if worst-case loss exceeds configured limit
        var maxLossPerTrade = _settings.Risk.MaxLossPerTradeArs;
        if (maxLossPerTrade > 0 && notional > maxLossPerTrade)
            return RiskCheckResult.Reject($"Pérdida máxima por trade excedida (notional {notional:N2} > maxLoss {maxLossPerTrade:N2})");

        var currentNet = _netPositions.GetValueOrDefault(request.Ticker, 0m);
        var delta = request.Side == OrderSide.Buy ? request.Quantity : -request.Quantity;
        var projectedNet = currentNet + delta;
        if (Math.Abs(projectedNet) > _settings.Risk.MaxPositionPerTicker)
            return RiskCheckResult.Reject($"Exposición por ticker excedida ({projectedNet:N2})");

        var (todayNet, _) = await _db.GetTodayPnLAsync();
        var lossLimitByPct = _settings.Trading.InitialCapital * (_settings.Risk.MaxDailyLossPercent / 100m);
        var configuredAbsLoss = _settings.Risk.MaxDailyLossArs;
        var maxDailyLoss = configuredAbsLoss > 0 && lossLimitByPct > 0
            ? Math.Min(configuredAbsLoss, lossLimitByPct)
            : Math.Max(configuredAbsLoss, lossLimitByPct);

        if (maxDailyLoss > 0 && todayNet <= -maxDailyLoss)
        {
            ActivateKillSwitch($"Límite de pérdida diaria alcanzado ({todayNet:N2})");
            return RiskCheckResult.Reject(KillSwitchReason);
        }

        return RiskCheckResult.Approve();
    }

    public void RegisterExecution(OrderResult result)
    {
        if (!result.Success || string.IsNullOrWhiteSpace(result.Ticker) || result.ExecutedQuantity <= 0)
            return;

        var signedQty = result.Side == OrderSide.Buy ? result.ExecutedQuantity : -result.ExecutedQuantity;
        _netPositions.AddOrUpdate(result.Ticker, signedQty, (_, current) => current + signedQty);
    }

    public void ActivateKillSwitch(string reason)
    {
        _killSwitchReason = string.IsNullOrWhiteSpace(reason) ? "Activado manualmente" : reason;
        _logger.LogCritical("Kill switch activado: {Reason}", _killSwitchReason);
    }

    public void ResetKillSwitch()
    {
        _killSwitchReason = string.Empty;
        _logger.LogWarning("Kill switch reseteado manualmente");
    }
}

public sealed class CapitalReservoir : ICapitalReservoir
{
    private readonly object _lock = new();
    private readonly AppSettings _settings;
    private readonly ILogger<CapitalReservoir> _logger;
    private readonly ConcurrentDictionary<Guid, ReservationToken> _activeReservations = new();

    public decimal TotalCapital => _settings.Trading.InitialCapital;
    public decimal ReservedCapital { get { lock (_lock) { return _activeReservations.Values.Sum(t => t.Amount); } } }
    public decimal AvailableCapital => TotalCapital - ReservedCapital;

    public CapitalReservoir(AppSettings settings, ILogger<CapitalReservoir> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public ReservationToken? TryReserve(decimal amount, string strategyName)
    {
        if (amount <= 0) return null;

        lock (_lock)
        {
            var available = TotalCapital - _activeReservations.Values.Sum(t => t.Amount);
            if (amount > available)
            {
                _logger.LogWarning("Capital reservation denied for {Strategy}: requested {Amount:N2}, available {Available:N2}",
                    strategyName, amount, available);
                return null;
            }

            var token = new ReservationToken(Guid.NewGuid(), amount, strategyName, DateTime.UtcNow);
            _activeReservations[token.Id] = token;
            _logger.LogDebug("Capital reserved: {Amount:N2} for {Strategy} (available after: {Available:N2})",
                amount, strategyName, available - amount);
            return token;
        }
    }

    public void Release(ReservationToken token)
    {
        if (_activeReservations.TryRemove(token.Id, out _))
        {
            _logger.LogDebug("Capital released: {Amount:N2} from {Strategy}", token.Amount, token.StrategyName);
        }
    }
}
