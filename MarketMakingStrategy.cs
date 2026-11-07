using Microsoft.Extensions.Logging;
using FinancialTerminal.Core;
using FinancialTerminal.Core.Configuration;
using FinancialTerminal.Core.Interfaces;
using FinancialTerminal.Core.Models;
using FinancialTerminal.Strategies.Common;

namespace FinancialTerminal.Strategies.MarketMaking;

/// <summary>
/// Market making básico: cotiza bid/ask alrededor del mid price.
/// Coloca una pata a la vez para controlar riesgo.
/// Ajusta agresividad según posición acumulada.
/// </summary>
public sealed class MarketMakingStrategy : IntentBasedStrategy
{
    private readonly MarketMakingSettings _mmSettings;
    private readonly IAccountProvider _accountProvider;

    private decimal _currentPosition;
    private decimal _lastMid;

    public override string Name => "Market Making";

    public MarketMakingStrategy(
        IMarketDataProvider marketData,
        IAccountProvider accountProvider,
        AppSettings settings,
        ILogger<MarketMakingStrategy> logger)
        : base(marketData, logger)
    {
        _mmSettings = settings.MarketMaking;
        _accountProvider = accountProvider;
    }

    public override async Task<Opportunity?> ScanAsync(CancellationToken ct = default)
    {
        if (!_mmSettings.Enabled || !IsEnabled) return null;

        var ticker = _mmSettings.Ticker;
        var settlement = _mmSettings.Settlement;

        var book = await MarketData.GetOrderBookAsync(ticker, settlement, ct);
        if (book is null || book.Bids.Count == 0 || book.Asks.Count == 0) return null;

        var bestBid = book.Bids[0].Price;
        var bestAsk = book.Asks[0].Price;
        if (bestBid <= 0 || bestAsk <= 0 || bestAsk <= bestBid) return null;

        var mid = (bestBid + bestAsk) / 2m;
        var currentSpread = (bestAsk - bestBid) / mid * 100m;

        if (currentSpread < _mmSettings.MinSpread)
        {
            Logger.LogDebug("[MM] {Ticker} spread {Spread:F3}% < mín {Min:F3}%",
                ticker, currentSpread, _mmSettings.MinSpread);
            return null;
        }

        var tickSize = TickSizeHelper.GetTickSize(mid);
        if (_lastMid > 0 && Math.Abs(mid - _lastMid) > tickSize * _mmSettings.CancelThresholdTicks)
        {
            Logger.LogInformation("[MM] Mid se movió {Delta:F2} (>{Threshold} ticks), recotizando",
                Math.Abs(mid - _lastMid), _mmSettings.CancelThresholdTicks);
        }
        _lastMid = mid;

        try
        {
            var positions = await _accountProvider.GetPositionsAsync(ct);
            var pos = positions.FirstOrDefault(p =>
                p.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase));
            _currentPosition = pos?.NetQuantity ?? 0m;
        }
        catch
        {
            // keep previous
        }

        var halfSpread = _mmSettings.MinSpread / 100m * mid / 2m;
        var bidPrice = TickSizeHelper.RoundToTick(mid - halfSpread);
        var askPrice = TickSizeHelper.RoundToTickUp(mid + halfSpread);

        if (_currentPosition > 0)
            askPrice = TickSizeHelper.RoundToTick(mid + halfSpread * 0.5m);
        else if (_currentPosition < 0)
            bidPrice = TickSizeHelper.RoundToTickUp(mid - halfSpread * 0.5m);

        var canBuy = _currentPosition < _mmSettings.MaxPosition;
        var canSell = _currentPosition > -_mmSettings.MaxPosition;
        if (!canBuy && !canSell) return null;

        var orderSize = _mmSettings.OrderSize;
        var expectedProfit = (askPrice - bidPrice) * orderSize;
        var fees = FeesCalculator.CalculateRoundTripFees(bidPrice, askPrice, orderSize);
        var netProfit = expectedProfit - fees.Total;
        if (netProfit <= 0) return null;

        OrderSide side;
        decimal price;
        if (canBuy && canSell)
        {
            var bidDepth = book.GetBidDepthArs(3);
            var askDepth = book.GetAskDepthArs(3);
            side = bidDepth >= askDepth ? OrderSide.Buy : OrderSide.Sell;
            price = side == OrderSide.Buy ? bidPrice : askPrice;
        }
        else if (canBuy)
        {
            side = OrderSide.Buy;
            price = bidPrice;
        }
        else
        {
            side = OrderSide.Sell;
            price = askPrice;
        }

        Logger.LogInformation(
            "[MM] {Ticker} Mid={Mid:F2} Bid={Bid:F2} Ask={Ask:F2} Spread={Spread:F3}% Side={Side} Pos={Pos}",
            ticker, mid, bidPrice, askPrice, currentSpread, side, _currentPosition);

        return new Opportunity
        {
            StrategyName = Name,
            Ticker = ticker,
            BuyPrice = bidPrice,
            SellPrice = askPrice,
            BuySettlement = settlement,
            SellSettlement = settlement,
            Quantity = orderSize,
            ExpectedNetProfit = Math.Round(netProfit, 2),
            ExpectedGrossProfit = Math.Round(expectedProfit, 2),
            FeesTotal = fees.Total,
            Details = new()
            {
                ["mid"] = mid,
                ["currentSpread"] = currentSpread,
                ["position"] = _currentPosition,
                ["side"] = side.ToString(),
                ["bidPrice"] = bidPrice,
                ["askPrice"] = askPrice,
            }
        };
    }

    public override Task<IReadOnlyList<TradeIntent>> BuildTradeIntentsAsync(
        Opportunity opportunity, CancellationToken ct = default)
    {
        var side = Enum.Parse<OrderSide>((string)opportunity.Details["side"]);
        var price = side == OrderSide.Buy
            ? (decimal)opportunity.Details["bidPrice"]
            : (decimal)opportunity.Details["askPrice"];

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
                ExpectedNetProfit = opportunity.ExpectedNetProfit
            }
        };

        return Task.FromResult<IReadOnlyList<TradeIntent>>(intents);
    }
}
