using Microsoft.Extensions.Logging;
using FinancialTerminal.Core.Configuration;
using FinancialTerminal.Core.Interfaces;
using FinancialTerminal.Core.Models;
using FinancialTerminal.Strategies.Common;

namespace FinancialTerminal.Strategies.Caucion;

/// <summary>
/// Coloca caución colocadora automática cerca del cierre del mercado.
/// Basado en la lógica de ChuchoBot.
/// Derechos BYMA: 0.045%/90 por día. IVA 21%.
/// </summary>
public sealed class CaucionAutoStrategy : IntentBasedStrategy
{
    private readonly CaucionSettings _caucionSettings;
    private readonly TradingSettings _tradingSettings;
    private readonly MarketSchedule _marketSchedule;
    private readonly IAccountProvider _accountProvider;
    private readonly IClock _clock;

    private const decimal DerechosMercadoTasaDiaria = 0.045m / 100m / 90m;
    private const decimal IVAPorcentaje = 0.21m;

    public override string Name => "Caucion Auto";

    public CaucionAutoStrategy(
        IMarketDataProvider marketData,
        IAccountProvider accountProvider,
        AppSettings settings,
        ILogger<CaucionAutoStrategy> logger,
        IClock? clock = null)
        : base(marketData, logger)
    {
        _caucionSettings = settings.Caucion;
        _tradingSettings = settings.Trading;
        _marketSchedule = settings.Market;
        _accountProvider = accountProvider;
        _clock = clock ?? new SystemClock();
    }

    public override async Task<Opportunity?> ScanAsync(CancellationToken ct = default)
    {
        if (!_caucionSettings.EnableAutoAtClose || !IsEnabled) return null;

        var now = _clock.LocalTimeNow;
        if (now < _caucionSettings.CloseExecutionTime || now > _marketSchedule.AuctionCloseTime)
            return null;

        decimal availableCash;
        try
        {
            var balance = await _accountProvider.GetBalanceAsync(ct);
            availableCash = balance.AvailableARS;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Caucion] No se pudo obtener balance");
            return null;
        }

        if (availableCash <= 10_000m)
        {
            Logger.LogDebug("[Caucion] Cash insuficiente: ${Cash:N0}", availableCash);
            return null;
        }

        var tna = await GetBestCaucionRateAsync(ct);
        if (tna < _caucionSettings.MinRatePercent)
        {
            Logger.LogDebug("[Caucion] TNA {TNA:F2}% < mínima {Min:F2}%", tna, _caucionSettings.MinRatePercent);
            return null;
        }

        const int dias = 1;
        var tasa = tna / 100m * dias / 365m;
        var interes = availableCash * tasa;
        var importeConInteres = availableCash + interes;

        var arancel = importeConInteres * (_caucionSettings.ComisionColocadoraTNA / 100m * dias / 365m);
        var derechosMercado = importeConInteres * DerechosMercadoTasaDiaria * dias;
        var gastos = arancel + derechosMercado;
        var ivaGastos = gastos * IVAPorcentaje;
        var totalGastos = gastos + ivaGastos;
        var interesNeto = interes - totalGastos;

        if (interesNeto <= 0) return null;

        Logger.LogInformation(
            "[Caucion] ${Cash:N0} a TNA {TNA:F2}% → Neto ${Neto:N2} (bruto ${Bruto:N2}, gastos ${Gastos:N2})",
            availableCash, tna, interesNeto, interes, totalGastos);

        return new Opportunity
        {
            StrategyName = Name,
            Ticker = "CAUCION",
            BuyPrice = tna,
            SellPrice = 0,
            BuySettlement = Settlement.CI,
            SellSettlement = Settlement.CI,
            Quantity = availableCash,
            ExpectedNetProfit = Math.Round(interesNeto, 2),
            ExpectedGrossProfit = Math.Round(interes, 2),
            FeesTotal = Math.Round(totalGastos, 2),
            Details = new()
            {
                ["tna"] = tna,
                ["dias"] = dias,
                ["importeBruto"] = availableCash,
                ["interes"] = interes,
                ["interesNeto"] = interesNeto,
                ["totalGastos"] = totalGastos,
            }
        };
    }

    public override Task<IReadOnlyList<TradeIntent>> BuildTradeIntentsAsync(
        Opportunity opportunity, CancellationToken ct = default)
    {
        var intents = new List<TradeIntent>
        {
            new()
            {
                StrategyName = Name,
                Ticker = "CAUCION-1D",
                Side = OrderSide.Sell, // Colocadora = presto dinero
                Settlement = Settlement.CI,
                Quantity = opportunity.Quantity,
                Price = (decimal)opportunity.Details["tna"],
                AssetType = AssetType.Caucion,
                ExpectedNetProfit = opportunity.ExpectedNetProfit
            }
        };

        return Task.FromResult<IReadOnlyList<TradeIntent>>(intents);
    }

    private async Task<decimal> GetBestCaucionRateAsync(CancellationToken ct)
    {
        try
        {
            var quote = await MarketData.GetQuoteAsync("CAUCION-1D", Settlement.CI, ct);
            if (quote is not null && quote.Last > 0)
                return quote.Last;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Caucion] Error obteniendo quote de caucion, abortando scan");
        }

        // Safety: if no real market data, return 0 to prevent execution with fabricated rates
        return 0m;
    }
}
