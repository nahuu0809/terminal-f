using FinancialTerminal.Core.Configuration;
using FinancialTerminal.Core.Models;

namespace FinancialTerminal.Core;

/// <summary>
/// Calcula comisiones y derechos de mercado BYMA.
/// Portado desde el bot Python y ChuchoBot.
/// </summary>
public static class FeesCalculator
{
    // Derechos de mercado BYMA
    private const decimal DerechosAccionesCedears = 0.0008m;  // 0.08%
    private const decimal DerechosLetras = 0.00001m;           // 0.001%
    private const decimal DerechosBonos = 0.0001m;             // 0.01%
    private const decimal IVARate = 0.21m;
    private const decimal DefaultComisionBroker = 0.001m;      // 0.10%

    public static decimal GetDerechosMercado(AssetType assetType) => assetType switch
    {
        AssetType.Accion or AssetType.Cedear => DerechosAccionesCedears,
        AssetType.Letra => DerechosLetras,
        _ => DerechosBonos
    };

    /// <summary>
    /// Calcula fees para una operación de compra + venta (round-trip).
    /// </summary>
    public static OperationFees CalculateRoundTripFees(
        decimal buyPrice, decimal sellPrice, decimal quantity,
        AssetType assetType = AssetType.Accion,
        decimal? comisionBroker = null)
    {
        var comision = comisionBroker ?? DefaultComisionBroker;
        var derechosRate = GetDerechosMercado(assetType);

        var buyAmount = buyPrice * quantity;
        var sellAmount = sellPrice * quantity;
        var totalOperated = buyAmount + sellAmount;

        var derechos = totalOperated * derechosRate;
        var brokerFee = totalOperated * comision;
        var subtotal = derechos + brokerFee;
        var iva = subtotal * IVARate;

        return new OperationFees
        {
            DerechosMercado = Math.Round(derechos, 2),
            ComisionBroker = Math.Round(brokerFee, 2),
            IVA = Math.Round(iva, 2)
        };
    }

    /// <summary>
    /// Calcula ganancia neta de una operación de arbitraje.
    /// </summary>
    public static (decimal GrossProfit, decimal NetProfit, OperationFees Fees) CalculateNetProfit(
        decimal buyPrice, decimal sellPrice, decimal quantity,
        AssetType assetType = AssetType.Accion)
    {
        var gross = (sellPrice - buyPrice) * quantity;
        var fees = CalculateRoundTripFees(buyPrice, sellPrice, quantity, assetType);
        var net = gross - fees.Total;
        return (Math.Round(gross, 2), Math.Round(net, 2), fees);
    }

    /// <summary>
    /// Calcula cantidad máxima de papeles según capital disponible.
    /// </summary>
    public static int CalculateMaxQuantity(decimal availableCapital, decimal maxCapitalPercent, decimal price)
    {
        if (price <= 0) return 0;
        var maxCapital = availableCapital * (maxCapitalPercent / 100m);
        return (int)Math.Floor(maxCapital / price);
    }

    /// <summary>
    /// Calcula TNA de una operación CI vs 24hs.
    /// </summary>
    public static decimal CalculateTNA(decimal buyPriceCI, decimal sellPrice24hs)
    {
        if (buyPriceCI <= 0) return 0;
        return ((sellPrice24hs / buyPriceCI) - 1m) * 365m;
    }
}

/// <summary>
/// Manejo de tick sizes según tabla BYMA.
/// </summary>
public static class TickSizeHelper
{
    private static readonly List<TickRange> DefaultTable =
    [
        new(0m, 10m, 0.01m),
        new(10m, 100m, 0.05m),
        new(100m, 1000m, 0.10m),
        new(1000m, null, 0.50m)
    ];

    public static decimal GetTickSize(decimal price, IReadOnlyList<TickRange>? table = null)
    {
        var tickTable = table ?? DefaultTable;
        foreach (var range in tickTable)
        {
            if (price >= range.Min && (range.Max is null || price < range.Max))
                return range.Tick;
        }
        return 0.01m;
    }

    /// <summary>Redondea un precio al tick más cercano hacia abajo.</summary>
    public static decimal RoundToTick(decimal price, IReadOnlyList<TickRange>? table = null)
    {
        var tick = GetTickSize(price, table);
        return Math.Floor(price / tick) * tick;
    }

    /// <summary>Redondea un precio al tick más cercano hacia arriba.</summary>
    public static decimal RoundToTickUp(decimal price, IReadOnlyList<TickRange>? table = null)
    {
        var tick = GetTickSize(price, table);
        return Math.Ceiling(price / tick) * tick;
    }
}

/// <summary>
/// Calendario de mercado argentino.
/// </summary>
public static class MarketCalendar
{
    // Feriados 2025/2026 - actualizar periódicamente
    private static readonly HashSet<DateOnly> Holidays =
    [
        // 2025
        new(2025, 1, 1), new(2025, 3, 3), new(2025, 3, 4), new(2025, 3, 24),
        new(2025, 4, 2), new(2025, 4, 18), new(2025, 5, 1), new(2025, 5, 25),
        new(2025, 6, 16), new(2025, 6, 20), new(2025, 7, 9), new(2025, 8, 17),
        new(2025, 10, 12), new(2025, 11, 24), new(2025, 12, 8), new(2025, 12, 25),
        // 2026
        new(2026, 1, 1), new(2026, 2, 16), new(2026, 2, 17), new(2026, 3, 24),
        new(2026, 4, 2), new(2026, 4, 3), new(2026, 5, 1), new(2026, 5, 25),
        new(2026, 6, 15), new(2026, 6, 20), new(2026, 7, 9), new(2026, 8, 17),
        new(2026, 10, 12), new(2026, 11, 23), new(2026, 12, 8), new(2026, 12, 25),
    ];

    public static bool IsHoliday(DateOnly date) => Holidays.Contains(date);

    public static bool IsTradingDay(DateOnly date) =>
        date.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday && !IsHoliday(date);

    /// <summary>Días hábiles entre CI y 24hs (normalmente 1).</summary>
    public static int DaysToSettlement24hs(DateOnly fromDate)
    {
        var current = fromDate.AddDays(1);
        while (!IsTradingDay(current))
            current = current.AddDays(1);
        return (current.ToDateTime(TimeOnly.MinValue) - fromDate.ToDateTime(TimeOnly.MinValue)).Days;
    }
}
