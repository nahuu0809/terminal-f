using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FinancialTerminal.Core.Interfaces;
using FinancialTerminal.Core.Models;
using FinancialTerminal.Strategies.Arbitrage;
using FinancialTerminal.Strategies.MarketMaking;
using FinancialTerminal.Strategies.Common;
// Fase avanzada — deshabilitadas temporalmente
// using FinancialTerminal.Strategies.Options;
// using FinancialTerminal.Strategies.Caucion;
using FinancialTerminal.Data.Repositories;

namespace FinancialTerminal.App.ViewModels;

/// <summary>
/// ViewModel del panel de bots. Controla start/stop de cada estrategia
/// y muestra logs en vivo.
/// </summary>
public partial class BotsViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly DatabaseRepository _db;
    private readonly ILogger<BotsViewModel> _logger;
    private readonly IRiskManager _riskManager;
    private readonly IOrderReconciliator _reconciliator;
    private readonly IOperationalMetrics _metrics;
    private readonly Dictionary<string, BotRunner> _runners = [];
    private readonly Timer _metricsTimer;
    private bool _heartbeatAlertRaised;

    public ObservableCollection<BotStatusRow> BotStatuses { get; } = [];
    public ObservableCollection<BotLogEntry> Logs { get; } = [];

    [ObservableProperty] private decimal _todayPnL;
    [ObservableProperty] private int _todayTradeCount;
    [ObservableProperty] private int _activeBotsCount;
    [ObservableProperty] private bool _killSwitchActive;
    [ObservableProperty] private string _killSwitchReason = "";
    [ObservableProperty] private double _averageLatencyMs;
    [ObservableProperty] private double _averageSlippageBps;
    [ObservableProperty] private decimal _exposureArs;
    [ObservableProperty] private decimal _exposureUsd;
    [ObservableProperty] private string _heartbeat = "-";

    public BotsViewModel(
        IServiceProvider services,
        DatabaseRepository db,
        ILogger<BotsViewModel> logger,
        IRiskManager riskManager,
        IOrderReconciliator reconciliator,
        IOperationalMetrics metrics)
    {
        _services = services;
        _db = db;
        _logger = logger;
        _riskManager = riskManager;
        _reconciliator = reconciliator;
        _metrics = metrics;
        KillSwitchActive = riskManager.IsKillSwitchActive;
        KillSwitchReason = riskManager.KillSwitchReason;

        InitializeBots();
        _metricsTimer = new Timer(_ => RefreshMetrics(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    private void InitializeBots()
    {
        // Bots conservadores activos
        RegisterBot("CI vs 24hs", () => _services.GetRequiredService<CIvs24hStrategy>());
        RegisterBot("Market Making", () => _services.GetRequiredService<MarketMakingStrategy>());
        RegisterBot("Spread Anomaly Hunter", () => _services.GetRequiredService<SpreadAnomalyHunterStrategy>());
        // Fase avanzada — deshabilitadas temporalmente hasta consolidar los bots conservadores
        // RegisterBot("MEP/CCL Arbitrage", () => _services.GetRequiredService<MepCclArbitrageStrategy>());
        // RegisterBot("PUTs bajo la par", () => _services.GetRequiredService<PutsArbitrageStrategy>());
        // RegisterBot("Caución Auto", () => _services.GetRequiredService<CaucionAutoStrategy>());
    }

    private void RegisterBot(string name, Func<IStrategy> strategyFactory)
    {
        var status = new BotStatusRow
        {
            Name = name,
            Status = "Detenido",
            IsRunning = false,
        };

        status.ToggleCommand = new RelayCommand(async () => await ToggleBotAsync(name, strategyFactory));
        BotStatuses.Add(status);
    }

    private async Task ToggleBotAsync(string name, Func<IStrategy> strategyFactory)
    {
        var status = BotStatuses.FirstOrDefault(b => b.Name == name);
        if (status is null) return;

        KillSwitchActive = _riskManager.IsKillSwitchActive;
        KillSwitchReason = _riskManager.KillSwitchReason;

        if (_runners.TryGetValue(name, out var existingRunner) && existingRunner.IsRunning)
        {
            // Detener
            await existingRunner.StopAsync();
            _runners.Remove(name);
            status.IsRunning = false;
            status.Status = "Detenido";
            AddLog(name, "Bot detenido", "INFO");
        }
        else
        {
            // Iniciar
            try
            {
                var strategy = strategyFactory();
                var runner = new BotRunner(strategy,
                    _services.GetRequiredService<ILogger<BotRunner>>(),
                    _services.GetRequiredService<ITradeExecutionEngine>(),
                    5000);

                runner.OnLog += (_, e) =>
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        AddLog(name, e.Message, e.Level.ToString());
                        _ = _db.InsertBotLogAsync(name, e.Level.ToString(), e.Message);
                    });
                };

                runner.OnOpportunityFound += (_, opp) =>
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        status.LastOpportunity = $"{opp.Ticker}: ${opp.ExpectedNetProfit:N0}";
                        status.LastScan = DateTime.Now.ToString("HH:mm:ss");
                    });
                };

                runner.OnOrderExecuted += (_, result) =>
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        status.TradeCount++;
                        TodayTradeCount++;
                        _ = RefreshPnLAsync();
                    });
                };

                _runners[name] = runner;
                await runner.StartAsync();

                status.IsRunning = true;
                status.Status = "Corriendo";
                AddLog(name, "Bot iniciado", "INFO");
            }
            catch (Exception ex)
            {
                status.Status = $"Error: {ex.Message}";
                AddLog(name, $"Error al iniciar: {ex.Message}", "ERROR");
            }
        }

        ActiveBotsCount = _runners.Count(r => r.Value.IsRunning);
        KillSwitchActive = _riskManager.IsKillSwitchActive;
        KillSwitchReason = _riskManager.KillSwitchReason;
    }

    private void AddLog(string strategy, string message, string level)
    {
        var entry = new BotLogEntry
        {
            Timestamp = DateTime.Now.ToString("HH:mm:ss"),
            Strategy = strategy,
            Message = message,
            Level = level
        };

        Logs.Insert(0, entry);
        if (Logs.Count > 500) Logs.RemoveAt(Logs.Count - 1); // Cap at 500
    }

    private async Task RefreshPnLAsync()
    {
        var (totalNet, count) = await _db.GetTodayPnLAsync();
        TodayPnL = totalNet;
        TodayTradeCount = count;
    }

    private void RefreshMetrics()
    {
        var snapshot = _metrics.Snapshot();
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            AverageLatencyMs = Math.Round(snapshot.AverageLatencyMs, 1);
            AverageSlippageBps = Math.Round(snapshot.AverageSlippageBps, 2);
            ExposureArs = decimal.Round(snapshot.ExposureArs, 2);
            ExposureUsd = decimal.Round(snapshot.ExposureUsd, 2);
            Heartbeat = snapshot.LastHeartbeatUtc.ToLocalTime().ToString("HH:mm:ss");

            var staleHeartbeat = DateTime.UtcNow - snapshot.LastHeartbeatUtc > TimeSpan.FromSeconds(30);
            if (staleHeartbeat && ActiveBotsCount > 0 && !_heartbeatAlertRaised)
            {
                _heartbeatAlertRaised = true;
                AddLog("AUDIT", "Heartbeat stale > 30s con bots activos", "ERROR");
            }
            else if (!staleHeartbeat)
            {
                _heartbeatAlertRaised = false;
            }
        });
    }

    [RelayCommand]
    private async Task StopAllBots()
    {
        foreach (var (name, runner) in _runners)
        {
            await runner.StopAsync();
            var status = BotStatuses.FirstOrDefault(b => b.Name == name);
            if (status is not null)
            {
                status.IsRunning = false;
                status.Status = "Detenido";
            }
        }
        _runners.Clear();
        ActiveBotsCount = 0;
        AddLog("SYSTEM", "Todos los bots detenidos", "WARNING");
    }

    [RelayCommand]
    private void ActivateKillSwitch()
    {
        _riskManager.ActivateKillSwitch("Activado desde panel de bots");
        KillSwitchActive = _riskManager.IsKillSwitchActive;
        KillSwitchReason = _riskManager.KillSwitchReason;
        AddLog("RISK", $"Kill switch activado: {KillSwitchReason}", "ERROR");
    }

    [RelayCommand]
    private void ResetKillSwitch()
    {
        _riskManager.ResetKillSwitch();
        KillSwitchActive = _riskManager.IsKillSwitchActive;
        KillSwitchReason = _riskManager.KillSwitchReason;
        AddLog("RISK", "Kill switch desactivado", "WARNING");
    }

    [RelayCommand]
    private async Task ReconcileOpenOrdersAsync()
    {
        var report = await _reconciliator.ReconcileOpenOrdersAsync(queryBroker: true);
        AddLog("SYSTEM",
            $"Reconciliación: scanned={report.Scanned}, updated={report.Updated}, filled={report.Filled}, failed={report.Failed}, cancelled={report.Cancelled}, pending={report.Pending}",
            "INFO");
        await RefreshPnLAsync();
    }

    [RelayCommand]
    private async Task RebuildPnlFromDatabaseAsync()
    {
        await _db.RebuildDailyPnlFromTradesAsync();
        await RefreshPnLAsync();
        AddLog("SYSTEM", "PnL diario reconstruido desde tabla trades", "WARNING");
    }

    [RelayCommand]
    private async Task CheckPortfolioSnapshotHealthAsync()
    {
        var (hasAccountSnapshot, positionCount) = await _db.GetTodayPortfolioSnapshotHealthAsync(BrokerType.PPI);
        AddLog("SYSTEM",
            $"Snapshot portfolio hoy: account={(hasAccountSnapshot ? "OK" : "MISSING")}, positions={positionCount}",
            hasAccountSnapshot ? "INFO" : "ERROR");
    }
}

// ─── Display Models ──────────────────────────────────────────────────────────

public partial class BotStatusRow : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _status = "Detenido";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _lastOpportunity = "-";
    [ObservableProperty] private string _lastScan = "-";
    [ObservableProperty] private int _tradeCount;

    public IRelayCommand? ToggleCommand { get; set; }
}

public partial class BotLogEntry : ObservableObject
{
    [ObservableProperty] private string _timestamp = "";
    [ObservableProperty] private string _strategy = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _level = "INFO";
}

/// <summary>
/// Portfolio ViewModel - posiciones y balance.
/// </summary>
public partial class PortfolioViewModel : ObservableObject
{
    private readonly IAccountProvider _accountProvider;
    private readonly DatabaseRepository _db;
    private readonly ILogger<PortfolioViewModel> _logger;

    public ObservableCollection<Position> Positions { get; } = [];

    [ObservableProperty] private decimal _availableARS;
    [ObservableProperty] private decimal _availableUSD;
    [ObservableProperty] private decimal _totalARS;

    public PortfolioViewModel(
        IAccountProvider accountProvider,
        DatabaseRepository db,
        ILogger<PortfolioViewModel> logger)
    {
        _accountProvider = accountProvider;
        _db = db;
        _logger = logger;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            var balance = await _accountProvider.GetBalanceAsync();
            AvailableARS = balance.AvailableARS;
            AvailableUSD = balance.AvailableUSD;
            TotalARS = balance.TotalARS;

            var positions = await _accountProvider.GetPositionsAsync();
            await _db.SaveDailyPortfolioSnapshotAsync(balance, positions);
            Positions.Clear();
            foreach (var pos in positions)
                Positions.Add(pos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refrescando portfolio");
        }
    }
}
