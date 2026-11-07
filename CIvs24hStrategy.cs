using Microsoft.Extensions.Logging;
using FinancialTerminal.Core;
using FinancialTerminal.Core.Configuration;
using FinancialTerminal.Core.Interfaces;
using FinancialTerminal.Core.Models;
using FinancialTerminal.Strategies.Common;

namespace FinancialTerminal.Strategies.Arbitrage;

/// <summary>
/// Estrategia de arbitraje CI vs 24hs.
/// Compra en CI (contado inmediato) y vende en 24hs cuando la TNA es atractiva.
/// Portado desde el bot Python ci_vs_24h.py.
/// </summary>
public sealed class CIvs24hStrategy : IntentBasedStrategy
{
    private readonly ArbitrageSettings _arbSettings;
    private readonly TradingSettings _tradingSettings;
    private readonly IAccountProvider _accountProvider;
    private readonly List<string> _tickers;

    private decimal _availableCash;

    public override string Name => "CI vs 24hs Arbitrage";

    public CIvs24hStrategy(
        IMarketDataProvider marketData,
        IAccountProvider accountProvider,
        AppSettings settings,
        ILogger<CIvs24hStrategy> logger)
        : base(marketData, logger)
    {
        _arbSettings = settings.Arbitrage;
        _tradingSettings = settings.Trading;
        _accountProvider = accountProvider;
        _tickers = _arbSettings.Tickers.Select(t => t.ToUpper()).ToList();
    }

    public override async Task<Opportunity?> ScanAsync(CancellationToken ct = default)
    {
        if (!_arbSettings.Enabled || !IsEnabled) return null;

        // Actualizar cash disponible
        try
        {
            var balance = await _accountProvider.GetBalanceAsync(ct);
            _availableCash = balance.AvailableARS > 0 ? balance.AvailableARS : _tradingSettings.InitialCapital;
        }
        catch
        {
            _availableCash = _tradingSettings.InitialCapital;
        }

        Opportunity? bestOpportunity = null;

        foreach (var ticker in _tickers)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var opp = await EvaluateTicker(ticker, ct);
                if (opp is not null && (bestOpportunity is null || opp.ExpectedNetProfit > bestOpportunity.ExpectedNetProfit))
                    bestOpportunity = opp;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[CI vs 24hs] Error analizando {Ticker}", ticker);
            }
        }

        return bestOpportunity;
    }

    private async Task<Opportunity?> EvaluateTicker(string ticker, CancellationToken ct)
    {
        // Obtener quotes CI y 24hs
        var quoteCI = await MarketData.GetQuoteAsync(ticker, Settlement.CI, ct);
        var quote24h = await MarketData.GetQuoteAsync(ticker, Settlement.T24hs, ct);

        if (quoteCI is null || quote24h is null) return null;

        var buyPrice = quoteCI.Ask;   // Compro en CI al ask
        var sellPrice = quote24h.Bid; // Vendo en 24hs al bid

        if (buyPrice <= 0 || sellPrice <= 0 || sellPrice <= buyPrice) return null;

        // Calcular TNA
        var tna = FeesCalculator.CalculateTNA(buyPrice, sellPrice);
        if (tna < _arbSettings.TasaMinimaTNA)
        {
            Logger.LogDebug("[CI vs 24hs] {Ticker}: TNA {TNA:F4} < mínima {Min:F4}",
                ticker, tna, _arbSettings.TasaMinimaTNA);
            return null;
        }

        // Calcular cantidad máxima (la validación de posición la hace RiskManager.EvaluateAsync)
        var quantity = FeesCalculator.CalculateMaxQuantity(
            _availableCash, _tradingSettings.MaxCapitalPerOperationPercent, buyPrice);
        if (quantity <= 0) return null;

        // Calcular profit neto
        var (grossProfit, netProfit, fees) = FeesCalculator.CalculateNetProfit(buyPrice, sellPrice, quantity);
        if (netProfit <= 0 || netProfit < _tradingSettings.MinNetProfit) return null;

        // Validar liquidez del book
        if (!await ValidateBookLiquidity(ticker, ct)) return null;

        Logger.LogInformation(
            "[CI vs 24hs] Oportunidad {Ticker} | CI={BuyPrice:F2} | 24hs={SellPrice:F2} | " +
            "TNA={TNA:F4} | Qty={Qty} | Neta=${NetProfit:N2}",
            ticker, buyPrice, sellPrice, tna, quantity, netProfit);

        return new Opportunity
        {
            StrategyName = Name,
            Ticker = ticker,
            BuyPrice = buyPrice,
            SellPrice = sellPrice,
            BuySettlement = Settlement.CI,
            SellSettlement = Settlement.T24hs,
            Quantity = quantity,
            ExpectedNetProfit = netProfit,
            ExpectedGrossProfit = grossProfit,
            FeesTotal = fees.Total,
            Details = new()
            {
                ["tna"] = tna,
                ["fees"] = fees,
                ["availableCash"] = _availableCash
            }
        };
    }

    private async Task<bool> ValidateBookLiquidity(string ticker, CancellationToken ct)
    {
        var nLevels = _tradingSettings.OrderBookLevelsToCheck;
        var minDepth = _tradingSettings.MinBookDepthArs;

        var bookCI = await MarketData.GetOrderBookAsync(ticker, Settlement.CI, ct);
        if (bookCI is null || bookCI.GetAskDepthArs(nLevels) < minDepth) return false;

        var book24h = await MarketData.GetOrderBookAsync(ticker, Settlement.T24hs, ct);
        if (book24h is null || book24h.GetBidDepthArs(nLevels) < minDepth) return false;

        return true;
    }
}
