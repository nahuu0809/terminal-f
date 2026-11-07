namespace FinancialTerminal.Core.Models;

// ─── Enums ───────────────────────────────────────────────────────────────────

public enum Settlement { CI, T24hs, T48hs }

public enum AssetType { Accion, Cedear, Bono, ON, Opcion, Caucion, Letra, FCI }

public enum OrderSide { Buy, Sell }

public enum OrderType { Limit, Market }

public enum OrderTimeInForce { Day, GTC, IOC, FOK }

public enum OrderStatus { New, PendingNew, PartiallyFilled, Filled, Cancelled, Rejected, PendingCancel }

public enum MarketState { PreOpen, Open, Closed, Auction }

public enum BrokerType { Primary, PPI, Paper }

// ─── Instrument ──────────────────────────────────────────────────────────────

public sealed record Instrument
{
    public required string Ticker { get; init; }
    public required string Market { get; init; }          // MERV, ROFX
    public string? Name { get; init; }
    public AssetType AssetType { get; init; } = AssetType.Accion;
    public string? UnderlyingTicker { get; init; }        // Para CEDEARs: ticker USA
    public decimal Ratio { get; init; } = 1m;             // Ratio CEDEAR (ej: 10:1)
    public string? Currency { get; init; }                 // ARS, USD, USD-C

    /// <summary>Genera el símbolo completo Primary: "MERV - XMEV - GGAL - CI"</summary>
    public string ToPrimarySymbol(Settlement settlement) =>
        $"MERV - XMEV - {Ticker} - {settlement switch
        {
            Settlement.CI => "CI",
            Settlement.T24hs => "24hs",
            Settlement.T48hs => "48hs",
            _ => "CI"
        }}";
}

// ─── Market Data ─────────────────────────────────────────────────────────────

public sealed record Quote
{
    public required string Ticker { get; init; }
    public required Settlement Settlement { get; init; }
    public decimal Bid { get; init; }
    public decimal Ask { get; init; }
    public decimal Last { get; init; }
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public decimal Volume { get; init; }
    public decimal VolumeAmount { get; init; }
    public decimal Change { get; init; }
    public decimal ChangePercent { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public decimal Spread => Ask > 0 && Bid > 0 ? (Ask - Bid) / Bid * 100m : 0m;
    public decimal Mid => Bid > 0 && Ask > 0 ? (Bid + Ask) / 2m : Last;
}

public sealed record BookLevel(decimal Price, decimal Quantity)
{
    public decimal Amount => Price * Quantity;
}

public sealed record OrderBook
{
    public required string Ticker { get; init; }
    public required Settlement Settlement { get; init; }
    public IReadOnlyList<BookLevel> Bids { get; init; } = [];
    public IReadOnlyList<BookLevel> Asks { get; init; } = [];
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public decimal GetBidDepthArs(int levels) =>
        Bids.Take(levels).Sum(b => b.Amount);

    public decimal GetAskDepthArs(int levels) =>
        Asks.Take(levels).Sum(a => a.Amount);
}

// ─── CEDEAR Enriched Row ─────────────────────────────────────────────────────

public sealed record CedearRow
{
    public required string Ticker { get; init; }
    public string? Name { get; init; }
    public decimal PriceARS { get; init; }
    public decimal PriceUSD { get; init; }
    public decimal TcImplicit { get; init; }
    public decimal TcRef { get; init; }
    public decimal Diff { get; init; }           // % diff vs TC ref
    public decimal Spread { get; init; }
    public decimal Volume { get; init; }
    public decimal VarDay { get; init; }
    public decimal Ratio { get; init; }
}

// ─── Bond / ON Row ───────────────────────────────────────────────────────────

public sealed record BondRow
{
    public required string Ticker { get; init; }
    public string? Name { get; init; }
    public decimal Price { get; init; }
    public decimal Parity { get; init; }
    public decimal TIR { get; init; }
    public decimal Duration { get; init; }
    public string? NextCoupon { get; init; }
    public string Currency { get; init; } = "ARS";
    public string Law { get; init; } = "Local";
    public decimal Volume { get; init; }
}

// ─── Caucion Row ─────────────────────────────────────────────────────────────

public sealed record CaucionRow
{
    public int Plazo { get; init; }
    public decimal TNA { get; init; }
    public decimal TEA { get; init; }
    public decimal Amount { get; init; }
    public Settlement Settlement { get; init; }
}

// ─── Orders ──────────────────────────────────────────────────────────────────

public sealed record OrderRequest
{
    public required string Ticker { get; init; }
    public required OrderSide Side { get; init; }
    public required Settlement Settlement { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal Price { get; init; }
    public OrderType Type { get; init; } = OrderType.Limit;
    public OrderTimeInForce TimeInForce { get; init; } = OrderTimeInForce.Day;
    public BrokerType TargetBroker { get; init; } = BrokerType.PPI;
    public AssetType AssetType { get; init; } = AssetType.Accion;
}

public sealed record OrderResult
{
    public bool Success { get; init; }
    public BrokerType Broker { get; init; } = BrokerType.PPI;
    public string? OrderId { get; init; }
    public string? Ticker { get; init; }
    public OrderSide Side { get; init; }
    public Settlement Settlement { get; init; }
    public decimal ExecutedPrice { get; init; }
    public decimal ExecutedQuantity { get; init; }
    public OrderStatus Status { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

// ─── Positions ───────────────────────────────────────────────────────────────

public sealed record Position
{
    public required string Ticker { get; init; }
    public Settlement Settlement { get; init; }
    public decimal BuySize { get; init; }
    public decimal BuyPrice { get; init; }
    public decimal SellSize { get; init; }
    public decimal SellPrice { get; init; }
    public decimal NetQuantity => BuySize - SellSize;
    public decimal TotalDailyDiff { get; init; }
    public decimal TotalDiff { get; init; }
}

// ─── Account ─────────────────────────────────────────────────────────────────

public sealed record AccountBalance
{
    public decimal AvailableARS { get; init; }
    public decimal BlockedARS { get; init; }
    public decimal TotalARS => AvailableARS + BlockedARS;
    public decimal AvailableUSD { get; init; }
    public BrokerType Broker { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

// ─── Market Status ───────────────────────────────────────────────────────────

public sealed record MarketIndex(decimal Value, decimal Change);

public sealed record MarketStatus
{
    public MarketState State { get; init; } = MarketState.Closed;
    public DateTime Time { get; init; }
    public MarketIndex Merval { get; init; } = new(0, 0);
    public MarketIndex SP500 { get; init; } = new(0, 0);
    public decimal DolarMep { get; init; }
    public decimal DolarCcl { get; init; }
    public decimal DolarOficial { get; init; }
}

// ─── Fees ────────────────────────────────────────────────────────────────────

public sealed record OperationFees
{
    public decimal DerechosMercado { get; init; }
    public decimal ComisionBroker { get; init; }
    public decimal IVA { get; init; }
    public decimal Total => DerechosMercado + ComisionBroker + IVA;
}

// ─── Tick Size ───────────────────────────────────────────────────────────────

public sealed record TickRange(decimal Min, decimal? Max, decimal Tick);
