using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using FinancialTerminal.Brokers.PPI;
using FinancialTerminal.Core.Configuration;
using FinancialTerminal.Core.Interfaces;
using FinancialTerminal.Core.Models;

namespace FinancialTerminal.App.Trading;

public sealed class SimulatedBroker : IOrderExecutor, IAccountProvider
{
    private readonly PaperTradingSettings _paper;
    private readonly InstrumentsSettings _instruments;
    private readonly ILogger<SimulatedBroker> _logger;
    private readonly ConcurrentDictionary<string, PaperOrder> _orders = new();
    private readonly Dictionary<(string Ticker, Settlement Settlement), PositionState> _positions = new();
    private readonly object _stateLock = new();
    private long _sequence;

    private decimal _cashArs;
    private decimal _cashUsd;

    public SimulatedBroker(PaperTradingSettings paper, InstrumentsSettings instruments, ILogger<SimulatedBroker> logger)
    {
        _paper = paper;
        _instruments = instruments;
        _logger = logger;
        _cashArs = paper.InitialCashArs;
        _cashUsd = paper.InitialCashUsd;
    }

    public BrokerType BrokerType => BrokerType.Paper;
    public bool IsConnected { get; private set; }

    public Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        IsConnected = true;
        return Task.FromResult(true);
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task<OrderResult> SendOrderAsync(OrderRequest order, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!IsConnected)
        {
            return Task.FromResult(new OrderResult
            {
                Success = false,
                Broker = BrokerType.Paper,
                Ticker = order.Ticker,
                Side = order.Side,
                Settlement = order.Settlement,
                Status = OrderStatus.Rejected,
                ErrorMessage = "Paper broker desconectado"
            });
        }

        var quantity = Math.Max(1m, decimal.Round(order.Quantity, 0, MidpointRounding.AwayFromZero));
        var basePrice = order.Price > 0 ? order.Price : 1m;
        var slippageBps = ComputeSlippageBps(order, basePrice * quantity);

        var signedSlippage = order.Side == OrderSide.Buy
            ? (1m + slippageBps / 10_000m)
            : (1m - slippageBps / 10_000m);
        var executedPrice = decimal.Round(basePrice * signedSlippage, 4, MidpointRounding.AwayFromZero);
        var notional = decimal.Round(executedPrice * quantity, 2, MidpointRounding.AwayFromZero);

        lock (_stateLock)
        {
            var key = (order.Ticker.ToUpperInvariant(), order.Settlement);
            var pos = _positions.GetValueOrDefault(key) ?? new PositionState();

            if (order.Side == OrderSide.Buy)
            {
                if (_cashArs < notional)
                {
                    return Task.FromResult(new OrderResult
                    {
                        Success = false,
                        Broker = BrokerType.Paper,
                        Ticker = order.Ticker,
                        Side = order.Side,
                        Settlement = order.Settlement,
                        Status = OrderStatus.Rejected,
                        ErrorMessage = $"Fondos insuficientes en paper broker. Requerido={notional:N2}, disponible={_cashArs:N2}"
                    });
                }

                _cashArs -= notional;
                pos.ApplyBuy(quantity, executedPrice);
            }
            else
            {
                if (pos.Quantity < quantity)
                {
                    return Task.FromResult(new OrderResult
                    {
                        Success = false,
                        Broker = BrokerType.Paper,
                        Ticker = order.Ticker,
                        Side = order.Side,
                        Settlement = order.Settlement,
                        Status = OrderStatus.Rejected,
                        ErrorMessage = $"No hay posicion suficiente para vender en paper broker. Disponible={pos.Quantity:N0}, requerida={quantity:N0}"
                    });
                }

                _cashArs += notional;
                pos.ApplySell(quantity);
            }

            if (pos.Quantity <= 0)
                _positions.Remove(key);
            else
                _positions[key] = pos;
        }

        var orderId = $"paper-{Interlocked.Increment(ref _sequence):D10}";
        var result = new OrderResult
        {
            Success = true,
            Broker = BrokerType.Paper,
            OrderId = orderId,
            Ticker = order.Ticker,
            Side = order.Side,
            Settlement = order.Settlement,
            ExecutedPrice = executedPrice,
            ExecutedQuantity = quantity,
            Status = OrderStatus.Filled,
            Timestamp = DateTime.UtcNow
        };

        _orders[orderId] = new PaperOrder(result);
        _logger.LogInformation("Paper fill: {Ticker} {Side} qty={Qty} px={Price} slip={SlippageBps}bps", order.Ticker, order.Side, quantity, executedPrice, slippageBps);
        return Task.FromResult(result);
    }

    public Task<bool> CancelOrderAsync(string orderId, CancellationToken ct = default)
    {
        if (!_orders.TryGetValue(orderId, out var existing))
            return Task.FromResult(false);

        if (existing.Result.Status is OrderStatus.Filled or OrderStatus.Rejected or OrderStatus.Cancelled)
            return Task.FromResult(false);

        var cancelled = existing.Result with { Status = OrderStatus.Cancelled };
        _orders[orderId] = new PaperOrder(cancelled);
        return Task.FromResult(true);
    }

    public Task<OrderResult?> GetOrderStatusAsync(string orderId, CancellationToken ct = default)
    {
        return Task.FromResult(_orders.TryGetValue(orderId, out var result) ? result.Result : null);
    }

    public Task<IReadOnlyList<OrderResult>> GetActiveOrdersAsync(CancellationToken ct = default)
    {
        var active = _orders.Values
            .Select(o => o.Result)
            .Where(r => r.Status is OrderStatus.New or OrderStatus.PendingNew or OrderStatus.PendingCancel or OrderStatus.PartiallyFilled)
            .ToList();
        return Task.FromResult<IReadOnlyList<OrderResult>>(active);
    }

    public Task<AccountBalance> GetBalanceAsync(CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            return Task.FromResult(new AccountBalance
            {
                AvailableARS = _cashArs,
                AvailableUSD = _cashUsd,
                Broker = BrokerType.Paper,
                Timestamp = DateTime.UtcNow
            });
        }
    }

    public Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            var positions = _positions.Select(kvp => new Position
            {
                Ticker = kvp.Key.Ticker,
                Settlement = kvp.Key.Settlement,
                BuySize = kvp.Value.Quantity,
                BuyPrice = kvp.Value.AveragePrice,
                SellSize = 0,
                SellPrice = 0,
                TotalDailyDiff = 0,
                TotalDiff = 0
            }).ToList();

            return Task.FromResult<IReadOnlyList<Position>>(positions);
        }
    }

    private decimal ComputeSlippageBps(OrderRequest order, decimal notional)
    {
        var ticker = order.Ticker.ToUpperInvariant();
        var bps = _paper.DefaultSlippageBps;

        if (_instruments.CedearTickers.Contains(ticker, StringComparer.OrdinalIgnoreCase))
            bps = _paper.CedearSlippageBps;
        else if (ticker.EndsWith("D", StringComparison.OrdinalIgnoreCase) || _instruments.BondTickers.Contains(ticker, StringComparer.OrdinalIgnoreCase))
            bps = _paper.BondSlippageBps;

        if (_paper.LargeOrderThresholdArs > 0 && notional >= _paper.LargeOrderThresholdArs)
            bps += _paper.LargeOrderExtraSlippageBps;

        return bps + DeterministicNoiseBps(order);
    }

    private decimal DeterministicNoiseBps(OrderRequest order)
    {
        var seed = string.Join("|",
            _paper.DeterministicSeed,
            order.Ticker.ToUpperInvariant(),
            order.Side,
            order.Settlement,
            decimal.Round(order.Quantity, 4),
            decimal.Round(order.Price, 4));

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
        var value = BitConverter.ToUInt32(hash, 0) % 5; // [0..4]
        return value - 2; // [-2..2] bps
    }

    private sealed record PaperOrder(OrderResult Result);

    private sealed class PositionState
    {
        public decimal Quantity { get; private set; }
        public decimal AveragePrice { get; private set; }

        public void ApplyBuy(decimal quantity, decimal price)
        {
            var totalCost = AveragePrice * Quantity + price * quantity;
            Quantity += quantity;
            AveragePrice = Quantity <= 0 ? 0 : totalCost / Quantity;
        }

        public void ApplySell(decimal quantity)
        {
            Quantity = Math.Max(0, Quantity - quantity);
            if (Quantity == 0)
                AveragePrice = 0;
        }
    }
}

public sealed class RoutingOrderExecutor : IOrderExecutor
{
    private readonly AppSettings _settings;
    private readonly PPIClient _real;
    private readonly SimulatedBroker _paper;

    public RoutingOrderExecutor(AppSettings settings, PPIClient real, SimulatedBroker paper)
    {
        _settings = settings;
        _real = real;
        _paper = paper;
    }

    public BrokerType BrokerType => _settings.PaperTrading.Enabled ? BrokerType.Paper : BrokerType.PPI;
    public bool IsConnected => _settings.PaperTrading.Enabled ? _paper.IsConnected : _real.IsConnected;

    public Task<bool> ConnectAsync(CancellationToken ct = default) =>
        _settings.PaperTrading.Enabled ? _paper.ConnectAsync(ct) : _real.ConnectAsync(ct);

    public Task DisconnectAsync() =>
        _settings.PaperTrading.Enabled ? _paper.DisconnectAsync() : _real.DisconnectAsync();

    public Task<OrderResult> SendOrderAsync(OrderRequest order, CancellationToken ct = default) =>
        _settings.PaperTrading.Enabled ? _paper.SendOrderAsync(order, ct) : _real.SendOrderAsync(order, ct);

    public Task<bool> CancelOrderAsync(string orderId, CancellationToken ct = default) =>
        _settings.PaperTrading.Enabled ? _paper.CancelOrderAsync(orderId, ct) : _real.CancelOrderAsync(orderId, ct);

    public Task<OrderResult?> GetOrderStatusAsync(string orderId, CancellationToken ct = default) =>
        _settings.PaperTrading.Enabled ? _paper.GetOrderStatusAsync(orderId, ct) : _real.GetOrderStatusAsync(orderId, ct);

    public Task<IReadOnlyList<OrderResult>> GetActiveOrdersAsync(CancellationToken ct = default) =>
        _settings.PaperTrading.Enabled ? _paper.GetActiveOrdersAsync(ct) : _real.GetActiveOrdersAsync(ct);
}

public sealed class RoutingAccountProvider : IAccountProvider
{
    private readonly AppSettings _settings;
    private readonly PPIClient _real;
    private readonly SimulatedBroker _paper;

    public RoutingAccountProvider(AppSettings settings, PPIClient real, SimulatedBroker paper)
    {
        _settings = settings;
        _real = real;
        _paper = paper;
    }

    public BrokerType BrokerType => _settings.PaperTrading.Enabled ? BrokerType.Paper : BrokerType.PPI;

    public Task<AccountBalance> GetBalanceAsync(CancellationToken ct = default) =>
        _settings.PaperTrading.Enabled ? _paper.GetBalanceAsync(ct) : _real.GetBalanceAsync(ct);

    public Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct = default) =>
        _settings.PaperTrading.Enabled ? _paper.GetPositionsAsync(ct) : _real.GetPositionsAsync(ct);
}
