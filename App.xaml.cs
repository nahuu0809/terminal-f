using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using FinancialTerminal.Core.Configuration;
using FinancialTerminal.Core.Interfaces;
using FinancialTerminal.Brokers.Primary;
using FinancialTerminal.Brokers.PPI;
using FinancialTerminal.Strategies.Arbitrage;
using FinancialTerminal.Strategies.MarketMaking;
using FinancialTerminal.Strategies.Common;
// Fase avanzada — deshabilitadas temporalmente hasta consolidar los bots conservadores
// using FinancialTerminal.Strategies.Options;
// using FinancialTerminal.Strategies.Caucion;
using FinancialTerminal.Data.Repositories;
using FinancialTerminal.App.ViewModels;
using FinancialTerminal.App.Trading;

namespace FinancialTerminal.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static AppSettings Settings { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ─── Load Config ─────────────────────────────────────────────────
        Settings = ConfigLoader.Load();

        // ─── Serilog ─────────────────────────────────────────────────────
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File("logs/terminal-.log", rollingInterval: RollingInterval.Day,
                          outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // ─── DI Container ────────────────────────────────────────────────
        var services = new ServiceCollection();

        // Logging
        services.AddLogging(builder =>
        {
            builder.AddSerilog(Log.Logger, dispose: true);
        });

        // Configuration
        services.AddSingleton(Settings);
        services.AddSingleton(Settings.Primary);
        services.AddSingleton(Settings.PPI);
        services.AddSingleton(Settings.Trading);
        services.AddSingleton(Settings.PaperTrading);
        services.AddSingleton(Settings.Risk);
        services.AddSingleton(Settings.Instruments);
        services.AddSingleton(Settings.Arbitrage);
        services.AddSingleton(Settings.MarketMaking);
        services.AddSingleton(Settings.SpreadAnomalyHunter);
        // Fase avanzada — deshabilitadas temporalmente
        // services.AddSingleton(Settings.Caucion);
        // services.AddSingleton(Settings.MepCcl);
        // services.AddSingleton(Settings.Options);
        services.AddSingleton(Settings.Resilience);

        // Database
        services.AddSingleton<DatabaseRepository>();
        services.AddSingleton<OrderRepository>();

        // Brokers
        services.AddSingleton<PrimaryClient>();
        services.AddSingleton<PPIClient>();
        services.AddSingleton<SimulatedBroker>();
        services.AddSingleton<RoutingOrderExecutor>();
        services.AddSingleton<RoutingAccountProvider>();

        // Register Primary as market data provider, PPI as order executor
        services.AddSingleton<IMarketDataProvider>(sp => sp.GetRequiredService<PrimaryClient>());
        services.AddSingleton<IOrderExecutor>(sp => sp.GetRequiredService<RoutingOrderExecutor>());
        services.AddSingleton<IAccountProvider>(sp => sp.GetRequiredService<RoutingAccountProvider>());

        // Clock
        services.AddSingleton<IClock, SystemClock>();

        // Robust execution core
        services.AddSingleton<IRiskManager, RiskManager>();
        services.AddSingleton<ICapitalReservoir, CapitalReservoir>();
        services.AddSingleton<IOperationalMetrics, OperationalMetrics>();
        services.AddSingleton<IExecutionQueue, ExecutionQueue>();
        services.AddSingleton<ITradingModeGuard, TradingModeGuard>();
        services.AddSingleton<ITradeExecutionEngine, TradeExecutionEngine>();
        services.AddSingleton<IOrderReconciliator, OrderReconciliator>();

        // Strategies — bots conservadores activos
        services.AddSingleton<CIvs24hStrategy>();
        services.AddSingleton<MarketMakingStrategy>();
        services.AddSingleton<SpreadAnomalyHunterStrategy>();
        // Fase avanzada — deshabilitadas temporalmente
        // services.AddSingleton<MepCclArbitrageStrategy>();
        // services.AddSingleton<PutsArbitrageStrategy>();
        // services.AddSingleton<CaucionAutoStrategy>();

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MarketViewModel>();
        services.AddSingleton<BotsViewModel>();
        services.AddSingleton<PortfolioViewModel>();

        Services = services.BuildServiceProvider();

        // Startup safety: rebuild risk snapshot and reconcile orphaned in-flight orders.
        var risk = Services.GetRequiredService<IRiskManager>();
        risk.WarmupAsync().GetAwaiter().GetResult();

        var reconciliator = Services.GetRequiredService<IOrderReconciliator>();
        _ = reconciliator.ReconcileOpenOrdersAsync(queryBroker: false).GetAwaiter().GetResult();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.CloseAndFlush();
        ConfigLoader.Save(Settings);

        if (Services is IAsyncDisposable asyncDisposable)
        {
            asyncDisposable.DisposeAsync().GetAwaiter().GetResult();
        }
        else if (Services is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnExit(e);
    }
}
