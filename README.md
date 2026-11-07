# Terminal F

Terminal financiera de escritorio, estilo Bloomberg, para el mercado argentino (BYMA/MAV). Recibe precios en tiempo real por **Primary** (WebSocket) y envía órdenes por **PPI** (REST). Incluye bots de estrategia, controles de riesgo y un modo de operación simulada (paper trading).

> **Aviso:** proyecto personal. No es asesoramiento financiero. Arranca en `sandbox` y con `PaperTrading.Enabled = true`. Probalo así antes de pensar en una cuenta real.

## Stack

WPF + MVVM (CommunityToolkit.Mvvm) sobre .NET 8, inyección de dependencias con `Microsoft.Extensions.DependencyInjection`, SQLite con Dapper, Serilog para logs, `HttpClient` y `ClientWebSocket` nativos con reintentos, circuit breaker y límite de pedidos por minuto. Tests con xUnit y Moq.

## Estructura

Todos los archivos `.cs` están en la raíz y los proyectos son agrupaciones lógicas definidas por cada `.csproj`.

| Proyecto | Responsabilidad |
| --- | --- |
| `FinancialTerminal.App` | Entrada WPF, inyección de dependencias, vistas y ViewModels |
| `FinancialTerminal.Core` | Modelos, interfaces y esquema de configuración |
| `FinancialTerminal.Data` | Persistencia SQLite (operaciones, logs, P&L diario) |
| `FinancialTerminal.Brokers.Primary` | Datos de mercado: WebSocket y respaldo por REST |
| `FinancialTerminal.Brokers.PPI` | Ejecución de órdenes y consulta de cuenta |
| `FinancialTerminal.Strategies` | Estrategias automáticas |
| `FinancialTerminal.Tests` | Pruebas unitarias y de robustez |

### Flujo de una orden

```
Estrategia ─► TradeIntent ─► ExecutionQueue ─► RiskManager ─► IOrderExecutor ─► DatabaseRepository
                                (un solo         (kill switch,   (PPI real o
                                consumidor)      pérdida diaria,  broker simulado)
                                                 límites)
```

### Estrategias

| Estrategia | Descripción |
| --- | --- |
| CI vs 24 hs | Compra en contado inmediato y vende a 24 hs cuando la tasa supera el mínimo |
| Market making | Cotiza compra y venta alrededor del precio medio |
| Caución automática | Coloca caución antes del cierre |
| MEP/CCL, PUTs, Spread anomaly | Versiones iniciales |

## Requisitos

- Windows 10/11 y .NET 8 SDK.
- Visual Studio 2022 o Rider.
- Cuenta en PPI y acceso a Primary: sandbox de Remarkets o una ALyC con API.

## Configuración

`appsettings.json` viene **sin credenciales**. Para trabajar con tus datos creá `appsettings.Development.json` (está en `.gitignore`) con el mismo formato:

```json
{
  "Mode": "sandbox",
  "Primary": { "BaseUrl": "https://api.remarkets.primary.com.ar", "Username": "TU_USUARIO", "Password": "TU_CLAVE" },
  "PPI": { "Username": "TU_API_KEY", "Password": "TU_API_SECRET", "AccountNumber": "TU_CUENTA", "UseSandbox": true },
  "PaperTrading": { "Enabled": true }
}
```

Para operar con dinero real hay que cambiar `Mode` a `production` y desactivar `PaperTrading.Enabled`. No lo hagas sin entender cada estrategia y cada límite de riesgo.

## Compilar y probar

```bash
dotnet restore
dotnet build FinancialTerminal.sln
dotnet run --project FinancialTerminal.App.csproj
dotnet test FinancialTerminal.Tests.csproj
```

## Origen

Reescribe en C# la lógica del bot de arbitraje en Python (`arbitrage-bot`) y toma el diseño de la terminal web (`terminal-web`). También se apoya en ideas de proyectos de código abierto de terceros del mercado argentino (ChuchoBot y BOTArbitradorPUTs), que no forman parte de este repositorio.

Más detalle de arquitectura para trabajar con Claude Code en [`CLAUDE.md`](CLAUDE.md).

## Créditos y licencias de terceros

Este repositorio contiene solo código propio. Las ideas y fuentes que lo inspiraron son:

| Proyecto | Autoría | Licencia | Relación |
| --- | --- | --- | --- |
| [BOTArbitradorPUTs](https://github.com/martinmasella/BOTArbitradorPUTs) | martinmasella (Tinchex Capital) | AGPL-3.0 | La estrategia de **PUTs bajo la par** (`PutsArbitrageStrategy.cs`) usa las mismas fórmulas de costo y ratio. El código es una implementación propia y no se copió el del original. |
| [ChuchoBot](https://github.com/ChuchoCoder/chuchobot) | ChuchoCoder | sin licencia declarada | Inspiración de la lógica del **arbitraje MEP/CCL** y de la **caución automática**. No se copió código. |
| [Primary.Net](https://github.com/naicigam/Primary.Net) | Juan Manuel Álvarez | MIT | Referencia para consumir la API de Primary. `PrimaryClient` es una implementación propia. |
| [Primary API](http://api.primary.com.ar) y la API de PPI | Primary / Portfolio Personal | — | Servicios a los que se conecta la terminal. |

Si sos autor de alguno de estos proyectos y querés que el crédito se muestre de otra forma, abrí un issue.

Dependencias principales (NuGet): [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet), [Dapper](https://github.com/DapperLib/Dapper), [Serilog](https://serilog.net), [LiveCharts2](https://github.com/beto-rodriguez/LiveCharts2), [xUnit](https://xunit.net) y [Moq](https://github.com/devlooped/moq).

## Licencia

Código bajo [licencia MIT](LICENSE). © 2026 Nahuel Emiliano Diaz.
