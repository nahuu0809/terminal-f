using Microsoft.Extensions.Logging;
using FinancialTerminal.Core.Configuration;
using FinancialTerminal.Core.Interfaces;
using FinancialTerminal.Core.Models;
using FinancialTerminal.Strategies.Common;

namespace FinancialTerminal.Strategies.Options;

/// <summary>
/// Arbitraje de PUTs bajo la par.
/// Basado en BOTArbitradorPUTs (Tinchex Capital).
///   costoArmar = (PUT_Ask * 1.0037) + (Spot_Ask * 1.0022)
///   ejercer    = strike * 1.0022
///   ratio      = (ejercer / costoArmar - 1) * 100
/// </summary>
public sealed class PutsArbitrageStrategy : IntentBasedStrategy
{
    private readonly OptionsSettings _optSettings;
    private readonly TradingSettings _tradingSettings;

    public override string Name => "PUTs bajo la par";

    public PutsArbitrageStrategy(
        IMarketDataProvider marketData,
        AppSettings settings,
        ILogger<PutsArbitrageStrategy> logger)
        : base(marketData, logger)
    {
        _optSettings = settings.Options;
        _tradingSettings = settings.Trading;
    }

    public override async Task<Opportunity?> ScanAsync(CancellationToken ct = default)
    {
        if (!_optSettings.Enabled || !IsEnabled) return null;
        if (_optSettings.PutTickers.Count == 0)
        {
            Logger.LogDebug("[PUTs] Sin tickers de PUTs configurados");
            return null;
        }

        Opportunity? best = null;

        foreach (var underlying in _optSettings.Underlyings)
        {
            if (ct.IsCancellationRequested) break;

            var spotQuote = await MarketData.GetQuoteAsync(underlying, Settlement.T24hs, ct);
            if (spotQuote is null || spotQuote.Ask <= 0) continue;

            var spotAsk = spotQuote.Ask;
            var costoSpot = spotAsk * (1m + _optSettings.ComisionAccionesPct / 100m);

            var putTickers = _optSettings.PutTickers
                .Where(t => IsPutForUnderlying(t, underlying))
                .ToList();

            foreach (var putTicker in putTickers)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var putQuote = await MarketData.GetQuoteAsync(putTicker, Settlement.T24hs, ct);
                    if (putQuote is null || putQuote.Ask <= 0) continue;

                    var putAsk = putQuote.Ask;
                    var costoPut = putAsk * (1m + _optSettings.ComisionOpcionesPct / 100m);

                    var strike = ExtractStrike(putTicker);
                    if (strike <= 0) continue;

                    var costoEjercer = strike * (1m + _optSettings.ComisionAccionesPct / 100m);
                    var costoArmar = costoPut + costoSpot;
                    if (costoArmar <= 0) continue;

                    var ratio = (costoEjercer / costoArmar - 1m) * 100m;
                    if (ratio < _optSettings.MinRatioPct) continue;

                    var netoPerLot = Math.Round(costoEjercer - costoArmar, 2);

                    Logger.LogInformation(
                        "[PUTs] {Put} | Spot={Spot:F2} | PUT={PutPx:F2} | Strike={Strike:F0} | " +
                        "Armar={Armar:F2} | Ejercer={Ejercer:F2} | Ratio={Ratio:F2}%",
                        putTicker, spotAsk, putAsk, strike, costoArmar, costoEjercer, ratio);

                    var maxCapital = _tradingSettings.InitialCapital *
                                     (_tradingSettings.MaxCapitalPerOperationPercent / 100m);
                    var lotes = (int)Math.Floor(maxCapital / costoArmar);
                    if (lotes <= 0) continue;

                    var totalNeto = netoPerLot * lotes;
                    if (totalNeto < _tradingSettings.MinNetProfit) continue;

                    if (best is null || totalNeto > best.ExpectedNetProfit)
                    {
                        var totalFees = (costoPut - putAsk + costoSpot - spotAsk) * lotes;
                        best = new Opportunity
                        {
                            StrategyName = Name,
                            Ticker = putTicker,
                            BuyPrice = putAsk,
                            SellPrice = costoEjercer,
                            BuySettlement = Settlement.T24hs,
                            SellSettlement = Settlement.T24hs,
                            Quantity = lotes,
                            ExpectedNetProfit = Math.Round(totalNeto, 2),
                            ExpectedGrossProfit = Math.Round((costoEjercer - costoArmar) * lotes, 2),
                            FeesTotal = Math.Round(totalFees, 2),
                            Details = new()
                            {
                                ["underlying"] = underlying,
                                ["putTicker"] = putTicker,
                                ["spotAsk"] = spotAsk,
                                ["putAsk"] = putAsk,
                                ["strike"] = strike,
                                ["costoArmar"] = costoArmar,
                                ["costoEjercer"] = costoEjercer,
                                ["ratio"] = ratio,
                                ["netoPerLot"] = netoPerLot,
                            }
                        };
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "[PUTs] Error analizando {Put}", putTicker);
                }
            }
        }

        return best;
    }

    public override Task<IReadOnlyList<TradeIntent>> BuildTradeIntentsAsync(
        Opportunity opportunity, CancellationToken ct = default)
    {
        var d = opportunity.Details;
        var lotes = opportunity.Quantity;

        var intents = new List<TradeIntent>
        {
            new()
            {
                StrategyName = Name,
                Ticker = (string)d["putTicker"],
                Side = OrderSide.Buy,
                Settlement = Settlement.T24hs,
                Quantity = lotes,
                Price = (decimal)d["putAsk"],
                AssetType = AssetType.Opcion
            },
            new()
            {
                StrategyName = Name,
                Ticker = (string)d["underlying"],
                Side = OrderSide.Buy,
                Settlement = Settlement.T24hs,
                Quantity = lotes,
                Price = (decimal)d["spotAsk"],
                AssetType = AssetType.Accion,
                ExpectedNetProfit = opportunity.ExpectedNetProfit
            },
        };

        return Task.FromResult<IReadOnlyList<TradeIntent>>(intents);
    }

    private static bool IsPutForUnderlying(string putTicker, string underlying)
    {
        var prefix = underlying switch
        {
            "GGAL" => "GFGV",
            "YPFD" => "YPFV",
            "PAMP" => "PAMV",
            _ => underlying[..Math.Min(3, underlying.Length)] + "V"
        };
        return putTicker.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// GFGV75054F → 7505.4 (5+ dígitos → último es decimal).
    /// GFGV7505F → 7505.
    /// </summary>
    private static decimal ExtractStrike(string putTicker)
    {
        var i = 0;
        while (i < putTicker.Length && !char.IsDigit(putTicker[i])) i++;

        var start = i;
        while (i < putTicker.Length && char.IsDigit(putTicker[i])) i++;

        var digitSpan = putTicker.AsSpan(start, i - start);
        if (digitSpan.Length == 0 || !decimal.TryParse(digitSpan, out var strike))
            return 0;

        return digitSpan.Length >= 5 ? strike / 10m : strike;
    }
}
