# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Terminal F is a Bloomberg-style WPF financial terminal for the Argentine market (BYMA/MAV). It streams real-time market data from Primary (WebSocket) and executes orders through PPI (REST). It includes automated trading strategies, risk controls, and paper trading mode.

## Build & Run Commands

```bash
# Restore dependencies
dotnet restore

# Build the entire solution
dotnet build FinancialTerminal.sln

# Run the WPF application
dotnet run --project FinancialTerminal.App.csproj

# Run all tests
dotnet test FinancialTerminal.Tests.csproj

# Run tests with detailed output
dotnet test FinancialTerminal.Tests.csproj --logger "console;verbosity=detailed"

# Run a specific test class
dotnet test FinancialTerminal.Tests.csproj --filter "FullyQualifiedName~RiskManagerTests"
```

## Solution Structure (7 projects)

All `.cs` source files live **flat in the solution root** — no physical subdirectories. Projects are logical groupings via `.csproj` file references.

| Project | Purpose |
|---------|---------|
| `FinancialTerminal.App` | WPF entry point, DI wiring, UI/ViewModels |
| `FinancialTerminal.Core` | Domain models, interfaces, config schema |
| `FinancialTerminal.Data` | SQLite persistence via Dapper |
| `FinancialTerminal.Brokers.Primary` | WebSocket + REST market data client |
| `FinancialTerminal.Brokers.PPI` | REST order execution + account queries |
| `FinancialTerminal.Strategies` | Trading bot strategies |
| `FinancialTerminal.Tests` | xUnit + Moq test suites |

## Architecture

### Execution Pipeline
```
TradeIntent (strategy)
  → ExecutionQueue (Channel-based, single consumer)
  → RiskManager (kill switch, daily P&L limit, position/notional limits)
  → IOrderExecutor (routes to PPIClient or SimulatedBroker)
  → DatabaseRepository (fill persistence)
  → TradeExecutionEngine (coordinates all above)
```

### Broker Roles
- **PrimaryClient**: Market data only — WebSocket subscriptions + REST fallback polling. No order execution.
- **PPIClient**: Full broker — order execution, account positions, balances. Handles token refresh + rate limiting (60 req/min).
- **SimulatedBroker** (PaperTrading.cs): In-memory fill simulation for paper mode.

### Strategy Framework
Each strategy implements `IBotStrategy` (in Interfaces.cs):
- `GetSignalAsync()` — detects opportunity
- `BuildTradeIntentsAsync()` — returns list of `TradeIntent` objects

`BotRunner` wraps strategies with on/off toggle, logging, and error isolation. Strategies are registered and started in `App.xaml.cs`.

### Configuration & Credentials
- `appsettings.json` — all runtime config; credentials stored DPAPI-encrypted.
- `AppSettings.cs` — full schema with ~15 nested settings classes.
- Toggle paper/live: `appsettings.json → PaperTrading.Enabled`
- Toggle sandbox/prod: `appsettings.json → Mode` (`"sandbox"` | `"production"`)

### Key Domain Types (Models.cs)
- `Settlement`: `CI`, `T24hs`, `T48hs`
- `AssetType`: `Accion`, `Cedear`, `Bono`, `ON`, `Opcion`, `Caucion`, `Letra`, `FCI`
- `OrderRequest`: includes `AssetType` — required for PPI's `MapInstrumentType()`
- `TradeIntent`: includes `AssetType` — passed through ExecutionQueue to OrderRequest

### ViewModels
- `MainViewModel.cs` — market data display, connectivity status, bond prices cache, MEP/CCL rates (`RecalcDolarRates()`)
- `BotsViewModel.cs` — bot on/off toggles, portfolio positions, P&L display
- `SettingsViewModel.cs` — runtime settings editor
- All use CommunityToolkit.Mvvm (`ObservableObject`, `[RelayCommand]`, `[ObservableProperty]`)

## Testing Patterns

Tests use Moq for broker interfaces. `TestSupport.cs` contains shared fixtures and fake implementations. Key mocks: `IMarketDataProvider`, `IOrderExecutor`, `IAccountProvider`, `IClock`.

`IClock` is injected everywhere time is needed — use `FakeClock` in tests to control market hours.

## Important Conventions

- **AssetType must be set on every TradeIntent/OrderRequest** — PPIClient uses it to map to the correct instrument type endpoint.
- **RiskManager is the single source of truth for position limits** — strategies must not track positions internally.
- **Paper mode is runtime-togglable** — `TradingModeGuard` wraps all live calls; no code-level if/else needed.
- **Market hours**: `MarketCalendar` (Utilities.cs) defines Argentine holidays and session windows. Strategies check `IsMarketOpen()` before acting.
- **MEP** = AL30 ARS / AL30D USD; **CCL** = GD30 ARS / GD30D USD (calculated in MainViewModel).
- **CEDEAR ratios** are configured in `AppSettings.InstrumentsSettings.CedearRatios`.
