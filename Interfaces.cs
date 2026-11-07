using FinancialTerminal.Core.Models;

namespace FinancialTerminal.Core.Interfaces;

/// <summary>
/// Abstracción de reloj para testabilidad de estrategias time-dependent.
/// </summary>
public interface IClock
{
    DateTime UtcNow { get; }
    TimeOnly LocalTimeNow { get; }
    DateOnly TodayLocal { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
    public TimeOnly LocalTimeNow => TimeOnly.FromDateTime(DateTime.Now);
    public DateOnly TodayLocal => DateOnly.FromDateTime(DateTime.Now);
}

/// <summary>
/// Interfaz unificada de market data. Primary la implementa con WebSocket + REST,
/// PPI con REST polling.
/// </summary>
public interface IMarketDataProvider
{
    BrokerType ProviderType { get; }
    bool IsConnected { get; }

    Task<bool> ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();

    Task<Quote?> GetQuoteAsync(string ticker, Settlement settlement, CancellationToken ct = default);
    Task<OrderBook?> GetOrderBookAsync(string ticker, Settlement settlement, CancellationToken ct = default);
    Task<IReadOnlyList<Quote>> GetQuotesBatchAsync(IEnumerable<string> tickers, Settlement settlement, CancellationToken ct = default);

    /// <summary>Suscripción WebSocket (Primary) o polling loop (PPI).</summary>
    Task SubscribeQuotesAsync(IEnumerable<string> tickers, Settlement settlement, CancellationToken ct = default);

    /// <summary>Evento disparado cada vez que hay un update de market data.</summary>
    event EventHandler<QuoteUpdateEventArgs>? OnQuoteUpdate;
}

/// <summary>
/// Interfaz para ejecución de órdenes. PPI es el broker principal para operar.
/// Primary se mantiene como fallback futuro.
/// </summary>
public interface IOrderExecutor
{
    BrokerType BrokerType { get; }
    bool IsConnected { get; }

    Task<bool> ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();

    Task<OrderResult> SendOrderAsync(OrderRequest order, CancellationToken ct = default);
    Task<bool> CancelOrderAsync(string orderId, CancellationToken ct = default);
    Task<OrderResult?> GetOrderStatusAsync(string orderId, CancellationToken ct = default);
    Task<IReadOnlyList<OrderResult>> GetActiveOrdersAsync(CancellationToken ct = default);
}

/// <summary>
/// Interfaz para consultas de cuenta y posiciones.
/// </summary>
public interface IAccountProvider
{
    BrokerType BrokerType { get; }

    Task<AccountBalance> GetBalanceAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct = default);
}

/// <summary>
/// Broker completo = market data + ordenes + cuenta.
/// Primary implementa solo IMarketDataProvider (+ IAccountProvider futuro).
/// PPI implementa las tres.
/// </summary>
public interface IBroker : IMarketDataProvider, IOrderExecutor, IAccountProvider
{
    string Name { get; }
}

// ─── Event Args ──────────────────────────────────────────────────────────────

public sealed class QuoteUpdateEventArgs : EventArgs
{
    public required Quote Quote { get; init; }
    public BrokerType Source { get; init; }
}

// ─── Strategy Interfaces ─────────────────────────────────────────────────────

public sealed record Opportunity
{
    public required string StrategyName { get; init; }
    public required string Ticker { get; init; }
    public decimal BuyPrice { get; init; }
    public decimal SellPrice { get; init; }
    public decimal Quantity { get; init; }
    public decimal ExpectedNetProfit { get; init; }
    public decimal ExpectedGrossProfit { get; init; }
    public decimal FeesTotal { get; init; }
    public Settlement BuySettlement { get; init; }
    public Settlement SellSettlement { get; init; }
    public Dictionary<string, object> Details { get; init; } = [];
    public DateTime DetectedAt { get; init; } = DateTime.UtcNow;
}

public interface IStrategy
{
    string Name { get; }
    bool IsEnabled { get; }
    bool IsRunning { get; }

    Task<Opportunity?> ScanAsync(CancellationToken ct = default);
    Task<OrderResult?> ExecuteAsync(Opportunity opportunity, CancellationToken ct = default);
}

public interface ITradeIntentStrategy
{
    Task<IReadOnlyList<TradeIntent>> BuildTradeIntentsAsync(Opportunity opportunity, CancellationToken ct = default);
}

/// <summary>
/// Bot runner que ejecuta una estrategia en loop con on/off.
/// </summary>
public interface IBotRunner
{
    string StrategyName { get; }
    bool IsRunning { get; }
    DateTime? LastScanTime { get; }
    Opportunity? LastOpportunity { get; }

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();

    event EventHandler<BotLogEventArgs>? OnLog;
    event EventHandler<Opportunity>? OnOpportunityFound;
    event EventHandler<OrderResult>? OnOrderExecuted;
}

public sealed class BotLogEventArgs : EventArgs
{
    public required string Message { get; init; }
    public LogLevel Level { get; init; } = LogLevel.Info;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public enum LogLevel { Debug, Info, Warning, Error }
}
