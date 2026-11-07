using Microsoft.Extensions.Logging;
using FinancialTerminal.Core;
using FinancialTerminal.Core.Configuration;
using FinancialTerminal.Core.Interfaces;
using FinancialTerminal.Core.Models;
using FinancialTerminal.Strategies.Common;

namespace FinancialTerminal.Strategies.Arbitrage;

/// <summary>
/// Arbitraje MEP/CCL cross-bond.
/// Compara el tipo de cambio implícito entre pares de bonos (ej: AL30/AL30D vs GD30/GD30D).
/// Si comprar USD por un bono y vender por otro da ganancia neta, genera oportunidad.
/// Basado en la lógica de ChuchoBot DolarArbitrationProcessor.
/// </summary>
public sealed class MepCclArbitrageStrategy : IntentBasedStrategy
{
    private readonly MepCclSettings _mepSettings;
    private readonly TradingSettings _tradingSettings;
    private readonly IAccountProvider _accountProvider;

    public override string Name => "MEP/CCL Arbitrage";

    public MepCclArbitrageStrategy(
        IMarketDataProvider marketData,
        IAccountProvider accountProvider,
        AppSettings settings,
        ILogger<MepCclArbitrageStrategy> logger)
        : base(marketData, logger)
    {
        _mepSettings = settings.MepCcl;
        _tradingSettings = settings.Trading;
        _accountProvider = accountProvider;
    }

    public override async Task<Opportunity?> ScanAsync(CancellationToken ct = default)
    {
        if (!_mepSettings.Enabled || !IsEnabled) return null;

        var rates = new Dictionary<string, BondPairQuotes>();

        foreach (var bondBase in _mepSettings.BondBases)
        {
            if (ct.IsCancellationRequested) break;

            var arsTicker = bondBase;
            var usdTicker = bondBase + "D";

            try
            {
                var arsQuote = await MarketData.GetQuoteAsync(arsTicker, _mepSettings.ArsSettlement, ct);
                var usdQuote = await MarketData.GetQuoteAsync(usdTicker, _mepSettings.UsdSettlement, ct);

                if (arsQuote is null || usdQuote is null) continue;
                if (arsQuote.Ask <= 0 || arsQuote.Bid <= 0 || usdQuote.Ask <= 0 || usdQuote.Bid <= 0) continue;

                rates[bondBase] = new BondPairQuotes(
                    arsQuote.Ask, arsQuote.Bid,
                    usdQuote.Ask, usdQuote.Bid,
                    arsTicker, usdTicker);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[MEP/CCL] Error obteniendo quotes para {Bond}", bondBase);
            }
        }

        if (rates.Count < 2) return null;

        Opportunity? best = null;

        foreach (var (sellBond, sell) in rates)
        foreach (var (buyBond, buy) in rates)
        {
            if (sellBond == buyBond) continue;

            // Comprar USD vía buyBond (ArsAsk/UsdBid), vender USD vía sellBond (ArsBid/UsdAsk)
            var buyRate = buy.ArsAsk / buy.UsdBid;
            var sellRate = sell.ArsBid / sell.UsdAsk;
            var spread = (sellRate / buyRate) - 1m;

            if (spread * 100m < _mepSettings.MinSpreadPercent) continue;

            decimal availableCash;
            try
            {
                var balance = await _accountProvider.GetBalanceAsync(ct);
                availableCash = balance.AvailableARS > 0 ? balance.AvailableARS : _tradingSettings.InitialCapital;
            }
            catch
            {
                availableCash = _tradingSettings.InitialCapital;
            }

            var maxCapital = availableCash * (_tradingSettings.MaxCapitalPerOperationPercent / 100m);
            var quantity = (int)Math.Floor(maxCapital / buy.ArsAsk);
            if (quantity <= 0) continue;

            var arsLeg = (sell.ArsBid - buy.ArsAsk) * quantity;
            var usdLeg = (buy.UsdBid - sell.UsdAsk) * quantity;
            var impliedMep = (sell.ArsBid + buy.ArsAsk) / 2m / ((sell.UsdAsk + buy.UsdBid) / 2m);
            var grossProfit = arsLeg + usdLeg * impliedMep;

            var fees = FeesCalculator.CalculateRoundTripFees(buy.ArsAsk, sell.ArsBid, quantity, AssetType.Bono);
            var netProfit = grossProfit - fees.Total;

            if (netProfit <= 0 || netProfit < _tradingSettings.MinNetProfit) continue;

            Logger.LogInformation(
                "[MEP/CCL] Buy {BuyBond} → Sell {SellBond} | Spread={Spread:F4}% | Neta=${Net:N2}",
                buyBond, sellBond, spread * 100, netProfit);

            if (best is null || netProfit > best.ExpectedNetProfit)
            {
                best = new Opportunity
                {
                    StrategyName = Name,
                    Ticker = buyBond,
                    BuyPrice = buy.ArsAsk,
                    SellPrice = sell.ArsBid,
                    BuySettlement = _mepSettings.ArsSettlement,
                    SellSettlement = _mepSettings.ArsSettlement,
                    Quantity = quantity,
                    ExpectedNetProfit = Math.Round(netProfit, 2),
                    ExpectedGrossProfit = Math.Round(grossProfit, 2),
                    FeesTotal = fees.Total,
                    Details = new()
                    {
                        ["buyBond"] = buyBond,
                        ["sellBond"] = sellBond,
                        ["buyArsTicker"] = buy.ArsTicker,
                        ["buyUsdTicker"] = buy.UsdTicker,
                        ["sellArsTicker"] = sell.ArsTicker,
                        ["sellUsdTicker"] = sell.UsdTicker,
                        ["buyRate"] = buyRate,
                        ["sellRate"] = sellRate,
                        ["spread"] = spread,
                        ["impliedMep"] = impliedMep,
                        ["buyArsAsk"] = buy.ArsAsk,
                        ["sellArsBid"] = sell.ArsBid,
                        ["buyUsdBid"] = buy.UsdBid,
                        ["sellUsdAsk"] = sell.UsdAsk,
                    }
                };
            }
        }

        return best;
    }

    public override Task<IReadOnlyList<TradeIntent>> BuildTradeIntentsAsync(
        Opportunity opportunity, CancellationToken ct = default)
    {
        var d = opportunity.Details;
        var qty = opportunity.Quantity;

        var intents = new List<TradeIntent>
        {
            // Pata 1: Compro bono ARS del par barato
            new()
            {
                StrategyName = Name,
                Ticker = (string)d["buyArsTicker"],
                Side = OrderSide.Buy,
                Settlement = _mepSettings.ArsSettlement,
                Quantity = qty,
                Price = (decimal)d["buyArsAsk"],
                AssetType = AssetType.Bono
            },
            // Pata 2: Vendo bono D del par barato (recibo USD)
            new()
            {
                StrategyName = Name,
                Ticker = (string)d["buyUsdTicker"],
                Side = OrderSide.Sell,
                Settlement = _mepSettings.UsdSettlement,
                Quantity = qty,
                Price = (decimal)d["buyUsdBid"],
                AssetType = AssetType.Bono
            },
            // Pata 3: Compro bono D del par caro (entrego USD)
            new()
            {
                StrategyName = Name,
                Ticker = (string)d["sellUsdTicker"],
                Side = OrderSide.Buy,
                Settlement = _mepSettings.UsdSettlement,
                Quantity = qty,
                Price = (decimal)d["sellUsdAsk"],
                AssetType = AssetType.Bono
            },
            // Pata 4: Vendo bono ARS del par caro (recibo más pesos)
            new()
            {
                StrategyName = Name,
                Ticker = (string)d["sellArsTicker"],
                Side = OrderSide.Sell,
                Settlement = _mepSettings.ArsSettlement,
                Quantity = qty,
                Price = (decimal)d["sellArsBid"],
                AssetType = AssetType.Bono,
                ExpectedNetProfit = opportunity.ExpectedNetProfit
            },
        };

        return Task.FromResult<IReadOnlyList<TradeIntent>>(intents);
    }

    private sealed record BondPairQuotes(
        decimal ArsAsk, decimal ArsBid,
        decimal UsdAsk, decimal UsdBid,
        string ArsTicker, string UsdTicker);
}
