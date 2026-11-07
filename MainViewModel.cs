using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using FinancialTerminal.Core;
using FinancialTerminal.Core.Configuration;
using FinancialTerminal.Core.Interfaces;
using FinancialTerminal.Core.Models;
using FinancialTerminal.Brokers.Primary;
using FinancialTerminal.Brokers.PPI;

namespace FinancialTerminal.App.ViewModels;

/// <summary>
/// ViewModel principal: maneja conexiones, status bar, navegación.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly PrimaryClient _primary;
    private readonly PPIClient _ppi;
    private readonly IOrderExecutor _orderExecutor;
    private readonly AppSettings _settings;
    private readonly IOrderReconciliator _reconciliator;
    private readonly ITradingModeGuard _tradingModeGuard;
    private readonly ILogger<MainViewModel> _logger;

    public MarketViewModel Market { get; }
    public BotsViewModel Bots { get; }
    public PortfolioViewModel Portfolio { get; }

    [ObservableProperty] private bool _primaryConnected;
    [ObservableProperty] private bool _ppiConnected;
    [ObservableProperty] private string _statusMessage = "Desconectado";
    [ObservableProperty] private string _currentTime = "--:--:--";
    [ObservableProperty] private string _mode = "SANDBOX";
    [ObservableProperty] private bool _paperModeEnabled;

    private Timer? _clockTimer;

    public MainViewModel(
        PrimaryClient primary, PPIClient ppi, IOrderExecutor orderExecutor, AppSettings settings,
        MarketViewModel market, BotsViewModel bots, PortfolioViewModel portfolio,
        IOrderReconciliator reconciliator,
        ITradingModeGuard tradingModeGuard,
        ILogger<MainViewModel> logger)
    {
        _primary = primary;
        _ppi = ppi;
        _orderExecutor = orderExecutor;
        _settings = settings;
        _reconciliator = reconciliator;
        _tradingModeGuard = tradingModeGuard;
        _logger = logger;
        Market = market;
        Bots = bots;
        Portfolio = portfolio;
        PaperModeEnabled = settings.PaperTrading.Enabled;
        RefreshModeLabel();

        _clockTimer = new Timer(_ =>
        {
            CurrentTime = DateTime.Now.ToString("HH:mm:ss");
        }, null, 0, 1000);
    }

    [RelayCommand]
    private async Task ConnectAll()
    {
        StatusMessage = "Conectando...";

        // Primary (market data)
        PrimaryConnected = await _primary.ConnectAsync();
        if (PrimaryConnected)
            _logger.LogInformation("Primary conectado");

        if (PaperModeEnabled)
        {
            PpiConnected = await _orderExecutor.ConnectAsync();
            _logger.LogWarning("Paper mode activo: ejecucion real deshabilitada");
        }
        else
        {
            // PPI (operaciones reales)
            PpiConnected = await _ppi.ConnectAsync();
            if (PpiConnected)
                _logger.LogInformation("PPI conectado");
        }

        var execLabel = PaperModeEnabled ? "PAPER" : (PpiConnected ? "OK" : "FAIL");
        StatusMessage = $"Primary: {(PrimaryConnected ? "OK" : "FAIL")} | Exec: {execLabel}";

        if (PrimaryConnected)
            await Market.StartDataFeedAsync();

        if (PpiConnected)
        {
            var report = await _reconciliator.ReconcileOpenOrdersAsync(queryBroker: !PaperModeEnabled);
            _logger.LogWarning(
                "Reconciliación post-conexión: scanned={Scanned}, updated={Updated}, failed={Failed}, filled={Filled}, cancelled={Cancelled}, pending={Pending}",
                report.Scanned, report.Updated, report.Failed, report.Filled, report.Cancelled, report.Pending);
            await Portfolio.RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task DisconnectAll()
    {
        Market.StopDataFeed();
        await _primary.DisconnectAsync();
        await _orderExecutor.DisconnectAsync();
        await _ppi.DisconnectAsync();
        PrimaryConnected = false;
        PpiConnected = false;
        StatusMessage = "Desconectado";
    }

    [RelayCommand]
    private async Task TogglePaperMode()
    {
        var guard = await _tradingModeGuard.CanTogglePaperModeAsync(Bots.ActiveBotsCount);
        if (!guard.Allowed)
        {
            StatusMessage = guard.Reason;
            return;
        }

        _settings.PaperTrading.Enabled = !_settings.PaperTrading.Enabled;
        PaperModeEnabled = _settings.PaperTrading.Enabled;
        RefreshModeLabel();
        StatusMessage = PaperModeEnabled
            ? "Paper Mode activo: ejecucion real bloqueada"
            : "Paper Mode desactivado: ejecucion real habilitada";
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var settingsWindow = new Views.SettingsWindow(_settings)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        settingsWindow.ShowDialog();
        // Refresh mode label in case Mode changed
        PaperModeEnabled = _settings.PaperTrading.Enabled;
        RefreshModeLabel();
    }

    private void RefreshModeLabel()
    {
        var env = _settings.IsSandbox ? "SANDBOX" : "PROD";
        Mode = PaperModeEnabled ? $"PAPER-{env}" : env;
    }
}

/// <summary>
/// ViewModel de market data: CEDEARs, acciones, bonos, caucion.
/// Alimenta todas las grillas de la pestaña "Mercado".
/// </summary>
public partial class MarketViewModel : ObservableObject
{
    private readonly IMarketDataProvider _marketData;
    private readonly AppSettings _settings;
    private readonly ILogger<MarketViewModel> _logger;
    private CancellationTokenSource? _feedCts;

    // Cache de precios de bonos para calcular MEP/CCL
    // AL30 = precio en ARS, AL30D = precio en USD → MEP = AL30/AL30D
    // GD30 = precio en ARS, GD30D = precio en USD → CCL = GD30/GD30D
    private readonly Dictionary<string, decimal> _bondPriceCache = new(StringComparer.OrdinalIgnoreCase);

    // ─── Market Status ───────────────────────────────────────────────────
    [ObservableProperty] private string _marketState = "CLOSED";
    [ObservableProperty] private decimal _mervalValue;
    [ObservableProperty] private decimal _mervalChange;
    [ObservableProperty] private decimal _sp500Value;
    [ObservableProperty] private decimal _sp500Change;
    [ObservableProperty] private decimal _dolarMep;
    [ObservableProperty] private decimal _dolarCcl;
    [ObservableProperty] private string _lastUpdate = "Sin datos";

    // ─── Settlement & Currency Toggle ────────────────────────────────────
    [ObservableProperty] private Settlement _selectedSettlement = Settlement.CI;
    [ObservableProperty] private bool _showInUsd;

    // ─── Watchlist Data ──────────────────────────────────────────────────
    public ObservableCollection<CedearDisplayRow> Cedears { get; } = [];
    public ObservableCollection<AccionDisplayRow> Acciones { get; } = [];
    public ObservableCollection<BondRow> Bonos { get; } = [];
    public ObservableCollection<CaucionRow> Cauciones { get; } = [];

    public MarketViewModel(IMarketDataProvider marketData, AppSettings settings, ILogger<MarketViewModel> logger)
    {
        _marketData = marketData;
        _settings = settings;
        _logger = logger;
    }

    public async Task StartDataFeedAsync()
    {
        _feedCts?.Cancel();
        _feedCts = new CancellationTokenSource();
        var ct = _feedCts.Token;

        // Suscribir a WebSocket si es Primary
        var allTickers = _settings.Instruments.AllTickers.ToList();
        await _marketData.SubscribeQuotesAsync(allTickers, SelectedSettlement, ct);

        _marketData.OnQuoteUpdate -= OnQuoteReceived;
        _marketData.OnQuoteUpdate += OnQuoteReceived;

        // Polling loop para datos que no vienen por WS
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                await RefreshAllDataAsync(ct);
                await Task.Delay(_settings.Trading.PollingIntervalMs, ct);
            }
        }, ct);
    }

    public void StopDataFeed()
    {
        _feedCts?.Cancel();
        _feedCts?.Dispose();
        _feedCts = null;
        _marketData.OnQuoteUpdate -= OnQuoteReceived;
    }

    private void OnQuoteReceived(object? sender, QuoteUpdateEventArgs e)
    {
        // Actualizar la fila correspondiente en la UI thread
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            UpdateQuoteInCollection(e.Quote);
            LastUpdate = $"Hace {(DateTime.UtcNow - e.Quote.Timestamp).TotalSeconds:F0}s";
        });
    }

    private async Task RefreshAllDataAsync(CancellationToken ct)
    {
        try
        {
            var tickers = _settings.Instruments.CedearTickers
                .Concat(_settings.Instruments.BymaLocalTickers)
                .Concat(_settings.Instruments.BondTickers)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var quotes = await _marketData.GetQuotesBatchAsync(tickers, SelectedSettlement, ct);
            var byTicker = quotes
                .GroupBy(q => q.Ticker, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Timestamp).First(), StringComparer.OrdinalIgnoreCase);

            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                foreach (var ticker in _settings.Instruments.CedearTickers)
                {
                    if (byTicker.TryGetValue(ticker, out var quote))
                        UpdateCedearRow(ticker, quote);
                }

                foreach (var ticker in _settings.Instruments.BymaLocalTickers)
                {
                    if (byTicker.TryGetValue(ticker, out var quote))
                        UpdateAccionRow(ticker, quote);
                }

                // Actualizar precios de bonos para MEP/CCL
                foreach (var ticker in _settings.Instruments.BondTickers)
                {
                    if (byTicker.TryGetValue(ticker, out var quote) && quote.Last > 0)
                        _bondPriceCache[ticker] = quote.Last;
                }
                RecalcDolarRates();

                MarketState = _settings.Market.IsMarketOpen() ? "OPEN" : "CLOSED";
                LastUpdate = DateTime.Now.ToString("HH:mm:ss");
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refrescando market data");
        }
    }

    private void UpdateQuoteInCollection(Quote quote)
    {
        // Buscar si es CEDEAR, acción, bono y actualizar la colección correspondiente
        var cedear = Cedears.FirstOrDefault(c => c.Ticker == quote.Ticker);
        if (cedear is not null)
        {
            cedear.PriceARS = quote.Last;
            cedear.Bid = quote.Bid;
            cedear.Ask = quote.Ask;
            cedear.Volume = quote.Volume;
            cedear.VarDay = quote.ChangePercent;
            cedear.Spread = quote.Spread;
            if (DolarMep > 0 && cedear.Ratio > 0)
                cedear.TcImplicit = (quote.Last * cedear.Ratio) / (cedear.PriceUSD > 0 ? cedear.PriceUSD : 1);
            return;
        }

        var accion = Acciones.FirstOrDefault(a => a.Ticker == quote.Ticker);
        if (accion is not null)
        {
            accion.Price = quote.Last;
            accion.Bid = quote.Bid;
            accion.Ask = quote.Ask;
            accion.Volume = quote.Volume;
            accion.VarDay = quote.ChangePercent;
            accion.Spread = quote.Spread;
            return;
        }

        // Capturar precios de bonos para calcular MEP/CCL
        if (_settings.Instruments.BondTickers.Contains(quote.Ticker, StringComparer.OrdinalIgnoreCase)
            && quote.Last > 0)
        {
            _bondPriceCache[quote.Ticker] = quote.Last;
            RecalcDolarRates();
        }
    }

    /// <summary>
    /// Calcula Dólar MEP y CCL a partir de pares de bonos ARS/USD.
    /// MEP  = AL30 (ARS) / AL30D (USD) — liquidación en el país.
    /// CCL  = GD30 (ARS) / GD30D (USD) — o AL30/AL30D con cuenta en el exterior.
    /// En Primary, los precios de bonos "D" se reportan en USD por lámina nominal.
    /// </summary>
    private void RecalcDolarRates()
    {
        // MEP: usamos AL30/AL30D como primer par, fallback GD30/GD30D
        if (_bondPriceCache.TryGetValue("AL30", out var al30Ars) &&
            _bondPriceCache.TryGetValue("AL30D", out var al30Usd) &&
            al30Usd > 0)
        {
            DolarMep = Math.Round(al30Ars / al30Usd, 2);
        }
        else if (_bondPriceCache.TryGetValue("GD30", out var gd30Ars) &&
                 _bondPriceCache.TryGetValue("GD30D", out var gd30Usd) &&
                 gd30Usd > 0)
        {
            DolarMep = Math.Round(gd30Ars / gd30Usd, 2);
        }

        // CCL: GD30/GD30D como referencia principal (ley extranjera)
        if (_bondPriceCache.TryGetValue("GD30", out var gd30ArsC) &&
            _bondPriceCache.TryGetValue("GD30D", out var gd30UsdC) &&
            gd30UsdC > 0)
        {
            DolarCcl = Math.Round(gd30ArsC / gd30UsdC, 2);
        }
        else
        {
            DolarCcl = DolarMep; // Fallback si no hay datos de GD30
        }
    }

    private void UpdateCedearRow(string ticker, Quote quote)
    {
        var existing = Cedears.FirstOrDefault(c => c.Ticker == ticker);
        if (existing is null)
        {
            existing = new CedearDisplayRow { Ticker = ticker };
            if (_settings.Instruments.CedearNames.TryGetValue(ticker, out var name))
                existing.Name = name;
            if (_settings.Instruments.CedearRatios.TryGetValue(ticker, out var ratio))
                existing.Ratio = ratio;
            Cedears.Add(existing);
        }
        existing.PriceARS = quote.Last;
        existing.Bid = quote.Bid;
        existing.Ask = quote.Ask;
        existing.Volume = quote.Volume;
        existing.VarDay = quote.ChangePercent;
        existing.Spread = quote.Spread;

        // Recalcular TC implícito cuando ya tenemos precio USD
        if (existing.PriceUSD > 0 && existing.Ratio > 0)
            existing.TcImplicit = existing.PriceARS * existing.Ratio / existing.PriceUSD;
        if (DolarMep > 0)
            existing.TcRef = DolarMep;
        if (existing.TcRef > 0 && existing.TcImplicit > 0)
            existing.Diff = (existing.TcImplicit / existing.TcRef - 1m) * 100m;
    }

    private void UpdateAccionRow(string ticker, Quote quote)
    {
        var existing = Acciones.FirstOrDefault(a => a.Ticker == ticker);
        if (existing is null)
        {
            existing = new AccionDisplayRow { Ticker = ticker };
            if (_settings.Instruments.LocalNames.TryGetValue(ticker, out var localName))
                existing.Name = localName;
            Acciones.Add(existing);
        }
        existing.Price = quote.Last;
        existing.Bid = quote.Bid;
        existing.Ask = quote.Ask;
        existing.Volume = quote.Volume;
        existing.VarDay = quote.ChangePercent;
        existing.Spread = quote.Spread;
    }

    [RelayCommand]
    private void ToggleSettlement()
    {
        SelectedSettlement = SelectedSettlement == Settlement.CI ? Settlement.T24hs : Settlement.CI;
        _ = StartDataFeedAsync();
    }
}

// ─── Display Models (bindable) ───────────────────────────────────────────────

public partial class CedearDisplayRow : ObservableObject
{
    [ObservableProperty] private string _ticker = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private decimal _priceARS;
    [ObservableProperty] private decimal _priceUSD;
    [ObservableProperty] private decimal _tcImplicit;
    [ObservableProperty] private decimal _tcRef;
    [ObservableProperty] private decimal _diff;
    [ObservableProperty] private decimal _spread;
    [ObservableProperty] private decimal _volume;
    [ObservableProperty] private decimal _varDay;
    [ObservableProperty] private decimal _bid;
    [ObservableProperty] private decimal _ask;
    [ObservableProperty] private decimal _ratio = 1;
}

public partial class AccionDisplayRow : ObservableObject
{
    [ObservableProperty] private string _ticker = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private decimal _price;
    [ObservableProperty] private decimal _bid;
    [ObservableProperty] private decimal _ask;
    [ObservableProperty] private decimal _volume;
    [ObservableProperty] private decimal _varDay;
    [ObservableProperty] private decimal _spread;
}
