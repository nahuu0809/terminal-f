using Microsoft.Extensions.Logging;
using FinancialTerminal.Core;
using FinancialTerminal.Core.Configuration;
using FinancialTerminal.Core.Interfaces;
using FinancialTerminal.Core.Models;
using FinancialTerminal.Strategies.Common;

namespace FinancialTerminal.Strategies.Arbitrage;

/// <summary>
/// Bot 3: Spread Anomaly Hunter.
/// Detecta spreads anormalmente grandes en el libro de órdenes y aprovecha
/// órdenes mal colocadas (precio ask[0] significativamente menor que ask[1]).
///
/// Lógica:
///   Si ask[0] + ask[1] existen y la brecha (ask[1] - ask[0]) / ask[1] > MinAnomalyGapPercent
///   → alguien colocó una orden de venta por debajo del precio real del mercado.
///   El bot compra al ask[0] y apunta a vender cerca del ask[1].
///
/// Si ya tiene inventario, genera la pata de venta (limit) sin comprar más.
/// Inventario cero → genera pata de compra.
/// </summary>
public sealed class SpreadAnomalyHunterStrategy : IntentBasedStrategy
{
    private readonly SpreadAnomalyHunterSettings _settings;
    private readonly List<string> _tickers;

    // Inventario local para decidir si comprar o vender en cada ciclo.
    // RiskManager es el control de riesgo definitivo; este campo es solo para
    // evitar comprar más mientras ya hay una posición abierta esperando llenarse.
    private decimal _currentInventory;
    private string _inventoryTicker = "";
    private decimal _inventoryBuyPrice;

    public override string Name => "Spread Anomaly Hunter";

    public SpreadAnomalyHunterStrategy(
        IMarketDataProvider marketData,
        AppSettings settings,
        ILogger<SpreadAnomalyHunterStrategy> logger)
        : base(marketData, logger)
    {
        _settings = settings.SpreadAnomalyHunter;
        _tickers = _settings.Tickers.Select(t => t.ToUpper()).ToList();
    }

    public override async Task<Opportunity?> ScanAsync(CancellationToken ct = default)
    {
        if (!_settings.Enabled || !IsEnabled) return null;

        // Si ya tenemos inventario, intentar vender antes de abrir nueva posición.
        if (_currentInventory > 0 && !string.IsNullOrEmpty(_inventoryTicker))
        {
            var sellOpp = await TryBuildSellOpportunity(_inventoryTicker, ct);
            if (sellOpp is not null) return sellOpp;
        }

        // Escanear nuevas anomalías (solo si inventario = 0).
        if (_currentInventory > 0) return null;

        Opportunity? best = null;

        foreach (var ticker in _tickers)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var opp = await EvaluateTicker(ticker, ct);
                if (opp is not null && (best is null || opp.ExpectedNetProfit > best.ExpectedNetProfit))
                    best = opp;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[SAH] Error analizando {Ticker}", ticker);
            }
        }

        return best;
    }

    private async Task<Opportunity?> EvaluateTicker(string ticker, CancellationToken ct)
    {
        var book = await MarketData.GetOrderBookAsync(ticker, _settings.Settlement, ct);
        if (book is null || book.Bids.Count == 0 || book.Asks.Count < 2) return null;

        var bid = book.Bids[0].Price;
        var anomalousAsk = book.Asks[0];   // posible orden mal colocada
        var realAsk = book.Asks[1];        // nivel de precio "real" del mercado

        if (bid <= 0 || anomalousAsk.Price <= 0 || realAsk.Price <= 0) return null;

        // Condición 1: spread bid-ask general debe ser anormalmente grande.
        var mid = (bid + anomalousAsk.Price) / 2m;
        var spreadPct = (anomalousAsk.Price - bid) / mid * 100m;
        if (spreadPct < _settings.MinSpreadPercent)
        {
            Logger.LogDebug("[SAH] {Ticker}: spread {Spread:F2}% < mínimo {Min:F2}%",
                ticker, spreadPct, _settings.MinSpreadPercent);
            return null;
        }

        // Condición 2: brecha entre ask[0] y ask[1] debe ser significativa (anomalía real).
        var askGapPct = (realAsk.Price - anomalousAsk.Price) / realAsk.Price * 100m;
        if (askGapPct < _settings.MinAnomalyGapPercent)
        {
            Logger.LogDebug("[SAH] {Ticker}: brecha ask {Gap:F2}% < mínima {Min:F2}%",
                ticker, askGapPct, _settings.MinAnomalyGapPercent);
            return null;
        }

        // Condición 3: liquidez del lado bid para poder salir.
        var bidDepth = book.GetBidDepthArs(3);
        if (bidDepth < _settings.MinBidDepthArs)
        {
            Logger.LogDebug("[SAH] {Ticker}: profundidad bid ARS {Depth:N0} < mínima {Min:N0}",
                ticker, bidDepth, _settings.MinBidDepthArs);
            return null;
        }

        // Cantidad: mínimo entre lo disponible en ask[0] y el límite de capital.
        var maxQtyByCapital = (int)Math.Floor(_settings.MaxCapitalPerOpArs / anomalousAsk.Price);
        var maxQtyByInventory = (int)Math.Floor(_settings.MaxInventoryUnits);
        var quantity = Math.Min(maxQtyByCapital, Math.Min(maxQtyByInventory, (int)anomalousAsk.Quantity));
        if (quantity <= 0) return null;

        // Target de venta: captura parcial del gap hacia ask[1].
        var targetSell = anomalousAsk.Price + (realAsk.Price - anomalousAsk.Price) * _settings.TargetGapCaptureRatio;
        targetSell = TickSizeHelper.RoundToTick(targetSell);

        var (grossProfit, netProfit, fees) = FeesCalculator.CalculateNetProfit(
            anomalousAsk.Price, targetSell, quantity);

        if (netProfit <= 0)
        {
            Logger.LogDebug("[SAH] {Ticker}: profit neto negativo ({Net:N2}) después de fees {Fees:N2}",
                ticker, netProfit, fees.Total);
            return null;
        }

        Logger.LogInformation(
            "[SAH] Anomalía detectada {Ticker} | bid={Bid:F2} ask[0]={Ask0:F2} ask[1]={Ask1:F2} | " +
            "gap={Gap:F2}% spread={Spread:F2}% | comprar={Qty} @ {BuyP:F2} → vender @ {SellP:F2} | neto=${Net:N2}",
            ticker, bid, anomalousAsk.Price, realAsk.Price,
            askGapPct, spreadPct, quantity, anomalousAsk.Price, targetSell, netProfit);

        return new Opportunity
        {
            StrategyName = Name,
            Ticker = ticker,
            BuyPrice = anomalousAsk.Price,
            SellPrice = targetSell,
            BuySettlement = _settings.Settlement,
            SellSettlement = _settings.Settlement,
            Quantity = quantity,
            ExpectedNetProfit = netProfit,
            ExpectedGrossProfit = grossProfit,
            FeesTotal = fees.Total,
            Details = new()
            {
                ["phase"] = "buy",
                ["askGapPct"] = askGapPct,
                ["spreadPct"] = spreadPct,
                ["targetSell"] = targetSell,
            }
        };
    }

    private async Task<Opportunity?> TryBuildSellOpportunity(string ticker, CancellationToken ct)
    {
        var book = await MarketData.GetOrderBookAsync(ticker, _settings.Settlement, ct);
        if (book is null || book.Bids.Count == 0) return null;

        var bestBid = book.Bids[0].Price;
        if (bestBid <= 0) return null;

        // Target mínimo de venta: al menos recuperar el costo de compra + fees.
        var minSell = TickSizeHelper.RoundToTickUp(_inventoryBuyPrice * 1.002m);
        var sellPrice = Math.Max(minSell, bestBid);
        sellPrice = TickSizeHelper.RoundToTick(sellPrice);

        // Verificar que la venta genere profit positivo.
        var (_, netProfit, _) = FeesCalculator.CalculateNetProfit(_inventoryBuyPrice, sellPrice, _currentInventory);
        if (netProfit <= 0)
        {
            Logger.LogDebug("[SAH] Venta de inventario {Ticker}: precio bid {Bid:F2} no cubre costo {Cost:F2}",
                ticker, bestBid, _inventoryBuyPrice);
            return null;
        }

        Logger.LogInformation(
            "[SAH] Vendiendo inventario {Ticker} qty={Qty} @ {Price:F2} | comprado @ {BuyP:F2} | neto=${Net:N2}",
            ticker, _currentInventory, sellPrice, _inventoryBuyPrice, netProfit);

        return new Opportunity
        {
            StrategyName = Name,
            Ticker = ticker,
            BuyPrice = _inventoryBuyPrice,
            SellPrice = sellPrice,
            BuySettlement = _settings.Settlement,
            SellSettlement = _settings.Settlement,
            Quantity = _currentInventory,
            ExpectedNetProfit = netProfit,
            ExpectedGrossProfit = (sellPrice - _inventoryBuyPrice) * _currentInventory,
            FeesTotal = 0m,
            Details = new()
            {
                ["phase"] = "sell",
                ["sellPrice"] = sellPrice,
            }
        };
    }

    public override Task<IReadOnlyList<TradeIntent>> BuildTradeIntentsAsync(
        Opportunity opportunity, CancellationToken ct = default)
    {
        var phase = (string)opportunity.Details["phase"];
        OrderSide side;
        decimal price;

        if (phase == "buy")
        {
            side = OrderSide.Buy;
            price = opportunity.BuyPrice;
            // Registrar inventario esperado para el próximo ciclo.
            _inventoryTicker = opportunity.Ticker;
            _inventoryBuyPrice = opportunity.BuyPrice;
            _currentInventory = opportunity.Quantity;
        }
        else
        {
            side = OrderSide.Sell;
            price = (decimal)opportunity.Details["sellPrice"];
            // Limpiar inventario al vender.
            _currentInventory = 0;
            _inventoryTicker = "";
            _inventoryBuyPrice = 0;
        }

        var intents = new List<TradeIntent>
        {
            new()
            {
                StrategyName = Name,
                Ticker = opportunity.Ticker,
                Side = side,
                Settlement = opportunity.BuySettlement,
                Quantity = opportunity.Quantity,
                Price = price,
                AssetType = AssetType.Accion,
                ExpectedNetProfit = opportunity.ExpectedNetProfit
            }
        };

        return Task.FromResult<IReadOnlyList<TradeIntent>>(intents);
    }
}
