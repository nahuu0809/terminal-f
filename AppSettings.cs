using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FinancialTerminal.Core.Models;

namespace FinancialTerminal.Core.Configuration;

public sealed class AppSettings
{
    public string Mode { get; set; } = "sandbox";

    [JsonIgnore]
    public bool IsSandbox => Mode.Equals("sandbox", StringComparison.OrdinalIgnoreCase);

    public PrimarySettings Primary { get; set; } = new();
    public PPISettings PPI { get; set; } = new();
    public TradingSettings Trading { get; set; } = new();
    public SpreadAnomalyHunterSettings SpreadAnomalyHunter { get; set; } = new();
    public PaperTradingSettings PaperTrading { get; set; } = new();
    public RiskSettings Risk { get; set; } = new();
    public InstrumentsSettings Instruments { get; set; } = new();
    public ArbitrageSettings Arbitrage { get; set; } = new();
    public MarketMakingSettings MarketMaking { get; set; } = new();
    public CaucionSettings Caucion { get; set; } = new();
    public MepCclSettings MepCcl { get; set; } = new();
    public OptionsSettings Options { get; set; } = new();
    public ResilienceSettings Resilience { get; set; } = new();
    public MarketSchedule Market { get; set; } = new();
    public List<TickRange> TickTable { get; set; } =
    [
        new(0m, 10m, 0.01m),
        new(10m, 100m, 0.05m),
        new(100m, 1000m, 0.10m),
        new(1000m, null, 0.50m)
    ];

    public void NormalizeAndProtectSecrets()
    {
        Primary.EnsureSecretsProtected();
        PPI.EnsureSecretsProtected();
    }
}

public sealed class PrimarySettings
{
    public string BaseUrl { get; set; } = "https://api.remarkets.primary.com.ar"; // Sandbox default
    [JsonPropertyName("Username")]
    public string UsernameEncrypted { get; set; } = "";
    [JsonPropertyName("Password")]
    public string PasswordEncrypted { get; set; } = "";
    public string Account { get; set; } = "";
    public bool UseWebSocket { get; set; } = true;
    public int WebSocketReconnectMs { get; set; } = 5000;

    [JsonIgnore]
    public string Username
    {
        get => CredentialHelper.Unprotect(UsernameEncrypted);
        set => UsernameEncrypted = string.IsNullOrWhiteSpace(value) ? string.Empty : CredentialHelper.Protect(value);
    }

    [JsonIgnore]
    public string Password
    {
        get => CredentialHelper.Unprotect(PasswordEncrypted);
        set => PasswordEncrypted = string.IsNullOrWhiteSpace(value) ? string.Empty : CredentialHelper.Protect(value);
    }

    public void EnsureSecretsProtected()
    {
        if (!string.IsNullOrWhiteSpace(UsernameEncrypted))
            UsernameEncrypted = CredentialHelper.Protect(CredentialHelper.Unprotect(UsernameEncrypted));
        if (!string.IsNullOrWhiteSpace(PasswordEncrypted))
            PasswordEncrypted = CredentialHelper.Protect(CredentialHelper.Unprotect(PasswordEncrypted));
    }

    /// <summary>
    /// URLs conocidas de ALyCs para facilitar configuración.
    /// </summary>
    public static readonly Dictionary<string, string> KnownAlycs = new()
    {
        ["Remarkets (Sandbox)"] = "https://api.remarkets.primary.com.ar",
        ["Primary (Production)"] = "https://api.primary.com.ar",
        ["Cocos Capital"] = "https://api.cocos.xoms.com.ar",
        ["Eco Valores"] = "https://api.eco.xoms.com.ar",
        ["Veta Capital"] = "https://api.veta.xoms.com.ar",
        ["Bull Market Brokers"] = "https://api.bull.xoms.com.ar",
        ["Cohen"] = "https://api.cohen.xoms.com.ar",
        ["Adcap"] = "https://api.adcap.xoms.com.ar",
        ["BCCH"] = "https://api.bcch.xoms.com.ar"
    };
}

public sealed class PPISettings
{
    public string BaseUrl { get; set; } = "https://clientapi.portfoliopersonal.com/api/1.0";
    public string SandboxBaseUrl { get; set; } = "https://clientapisandbox.portfoliopersonal.com/api/1.0";
    [JsonPropertyName("Username")]
    public string UsernameEncrypted { get; set; } = "";
    [JsonPropertyName("Password")]
    public string PasswordEncrypted { get; set; } = "";
    public string AccountNumber { get; set; } = "";
    public string AuthorizedClient { get; set; } = "";
    public string ClientKey { get; set; } = "";
    public int RateLimitPerMinute { get; set; } = 60;
    public int TokenRenewalSecondsBeforeExpiry { get; set; } = 300;
    public bool UseSandbox { get; set; } = false;

    [JsonIgnore]
    public string ActiveBaseUrl => UseSandbox ? SandboxBaseUrl : BaseUrl;

    [JsonIgnore]
    public string Username
    {
        get => CredentialHelper.Unprotect(UsernameEncrypted);
        set => UsernameEncrypted = string.IsNullOrWhiteSpace(value) ? string.Empty : CredentialHelper.Protect(value);
    }

    [JsonIgnore]
    public string Password
    {
        get => CredentialHelper.Unprotect(PasswordEncrypted);
        set => PasswordEncrypted = string.IsNullOrWhiteSpace(value) ? string.Empty : CredentialHelper.Protect(value);
    }

    public void EnsureSecretsProtected()
    {
        if (!string.IsNullOrWhiteSpace(UsernameEncrypted))
            UsernameEncrypted = CredentialHelper.Protect(CredentialHelper.Unprotect(UsernameEncrypted));
        if (!string.IsNullOrWhiteSpace(PasswordEncrypted))
            PasswordEncrypted = CredentialHelper.Protect(CredentialHelper.Unprotect(PasswordEncrypted));
    }
}

public sealed class TradingSettings
{
    /// <summary>Capital inicial en ARS. Rango conservador: 50.000 - 100.000 ARS.</summary>
    public decimal InitialCapital { get; set; } = 75_000m;
    public decimal MinNetProfit { get; set; } = 100m;
    /// <summary>Máximo capital por operación (%). Conservador: no más del 20% por operación.</summary>
    public decimal MaxCapitalPerOperationPercent { get; set; } = 20m;
    public int PollingIntervalMs { get; set; } = 5000;
    public decimal MaxSlippagePercent { get; set; } = 0.3m;
    public decimal MinBookDepthArs { get; set; } = 10_000m;
    public int OrderBookLevelsToCheck { get; set; } = 3;
    public int ValidationTimeoutSeconds { get; set; } = 5;
}

public sealed class PaperTradingSettings
{
    public bool Enabled { get; set; } = true;
    public decimal InitialCashArs { get; set; } = 1_000_000m;
    public decimal InitialCashUsd { get; set; } = 0m;
    public decimal DefaultSlippageBps { get; set; } = 10m;
    public decimal CedearSlippageBps { get; set; } = 15m;
    public decimal BondSlippageBps { get; set; } = 20m;
    public decimal LargeOrderThresholdArs { get; set; } = 2_000_000m;
    public decimal LargeOrderExtraSlippageBps { get; set; } = 15m;
    public string DeterministicSeed { get; set; } = "terminal-f-paper";
}

public sealed class RiskSettings
{
    public decimal MaxDailyLossPercent { get; set; } = 10m;
    public decimal MaxDailyLossArs { get; set; } = 50_000m;
    public decimal MaxPositionPerTicker { get; set; } = 100m;
    /// <summary>Maximum notional loss allowed per individual trade (ARS). 0 = disabled.</summary>
    public decimal MaxLossPerTradeArs { get; set; } = 0m;
    public bool StopTradingFlag { get; set; } = false;
    public int MaxConsecutiveExecutionFailures { get; set; } = 5;
    public int MaxExecutionLatencyMs { get; set; } = 20_000;
    public bool EnableLatencyKillSwitch { get; set; } = true;
}

public sealed class InstrumentsSettings
{
    public List<string> BymaLocalTickers { get; set; } = ["GGAL", "YPFD", "PAMP", "BMA", "TXAR"];
    public List<string> CedearTickers { get; set; } = ["AAPL", "GOOGL", "MSFT", "TSLA", "AMZN", "MELI", "NVDA"];
    public List<string> BondTickers { get; set; } = ["AL30", "AL30D", "GD30", "GD30D", "AL35", "AL35D"];
    public List<string> ONTickers { get; set; } = [];
    public List<string> OptionUnderlyings { get; set; } = ["GGAL", "YPFD", "PAMP"];

    /// <summary>Ratio de conversión: cuántos CEDEARs equivalen a 1 acción subyacente USA.</summary>
    public Dictionary<string, int> CedearRatios { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AAPL"]  = 15,
        ["GOOGL"] = 1,
        ["MSFT"]  = 5,
        ["TSLA"]  = 1,
        ["AMZN"]  = 4,
        ["MELI"]  = 1,
        ["NVDA"]  = 10,
        ["META"]  = 1,
        ["BABA"]  = 1,
        ["NFLX"]  = 1,
        ["DIS"]   = 5,
        ["KO"]    = 10,
        ["BRKB"]  = 2,
        ["COIN"]  = 1,
        ["UBER"]  = 5,
    };

    /// <summary>Nombre legible del subyacente del CEDEAR.</summary>
    public Dictionary<string, string> CedearNames { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AAPL"]  = "Apple Inc.",
        ["GOOGL"] = "Alphabet (Google)",
        ["MSFT"]  = "Microsoft",
        ["TSLA"]  = "Tesla",
        ["AMZN"]  = "Amazon",
        ["MELI"]  = "MercadoLibre",
        ["NVDA"]  = "NVIDIA",
        ["META"]  = "Meta Platforms",
        ["BABA"]  = "Alibaba",
        ["NFLX"]  = "Netflix",
        ["DIS"]   = "Walt Disney",
        ["KO"]    = "Coca-Cola",
        ["BRKB"]  = "Berkshire Hathaway B",
        ["COIN"]  = "Coinbase",
        ["UBER"]  = "Uber Technologies",
    };

    /// <summary>Nombre legible de acciones locales BYMA.</summary>
    public Dictionary<string, string> LocalNames { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GGAL"]  = "Grupo Galicia",
        ["YPFD"]  = "YPF",
        ["PAMP"]  = "Pampa Energía",
        ["BMA"]   = "Banco Macro",
        ["TXAR"]  = "Ternium Argentina",
        ["LOMA"]  = "Loma Negra",
        ["SUPV"]  = "Supervielle",
        ["EDN"]   = "Edenor",
        ["TGSU2"] = "TGS",
        ["TGNO4"] = "TGN",
        ["CEPU"]  = "Central Puerto",
        ["ALUA"]  = "Aluar",
        ["VALO"]  = "Grupo Financiero Valores",
    };

    [JsonIgnore]
    public IEnumerable<string> AllTickers => BymaLocalTickers
        .Concat(CedearTickers)
        .Concat(BondTickers)
        .Concat(ONTickers)
        .Distinct(StringComparer.OrdinalIgnoreCase);
}

public sealed class ArbitrageSettings
{
    public bool Enabled { get; set; } = true;
    public decimal TasaMinimaTNA { get; set; } = 0.25m;
    public List<string> Tickers { get; set; } = ["GGAL", "YPFD", "PAMP", "BMA", "TXAR"];
}

public sealed class MarketMakingSettings
{
    public bool Enabled { get; set; } = false;
    public string Ticker { get; set; } = "GGAL";
    public Settlement Settlement { get; set; } = Settlement.CI;
    public decimal MinSpread { get; set; } = 0.5m;
    public decimal OrderSize { get; set; } = 10m;
    public decimal MaxPosition { get; set; } = 100m;
    public int CancelThresholdTicks { get; set; } = 2;
}

public sealed class CaucionSettings
{
    public bool EnableAutoAtClose { get; set; } = true;
    public decimal MinRatePercent { get; set; } = 40m;
    public decimal ComisionTomadoraTNA { get; set; } = 0m;
    public decimal ComisionColocadoraTNA { get; set; } = 0m;
    public TimeOnly CloseExecutionTime { get; set; } = new(16, 50);
}

public sealed class MepCclSettings
{
    public bool Enabled { get; set; } = false;
    /// <summary>Spread mínimo (%) entre pares de bonos para señalar oportunidad.</summary>
    public decimal MinSpreadPercent { get; set; } = 0.3m;
    /// <summary>Pares de bonos base (sin sufijo D/C). Se asume que existen TICKER y TICKERD.</summary>
    public List<string> BondBases { get; set; } = ["AL30", "GD30"];
    /// <summary>Plazo preferido para la pata ARS.</summary>
    public Settlement ArsSettlement { get; set; } = Settlement.CI;
    /// <summary>Plazo preferido para la pata USD.</summary>
    public Settlement UsdSettlement { get; set; } = Settlement.T24hs;
}

public sealed class OptionsSettings
{
    public bool Enabled { get; set; } = false;
    public decimal UmbralPut { get; set; } = 0.95m;  // Bajo la par
    /// <summary>Comisión opciones (%) para calcular costo de armar.</summary>
    public decimal ComisionOpcionesPct { get; set; } = 0.37m;
    /// <summary>Comisión acciones (%) para calcular costo de armar.</summary>
    public decimal ComisionAccionesPct { get; set; } = 0.22m;
    /// <summary>Ratio mínimo (%) para señalar oportunidad.</summary>
    public decimal MinRatioPct { get; set; } = 0.5m;
    public List<string> Underlyings { get; set; } = ["GGAL", "YPFD", "PAMP"];
    /// <summary>Tickers de PUTs a monitorear. Formato Primary: GFGV75054F.</summary>
    public List<string> PutTickers { get; set; } = [];
}

/// <summary>
/// Bot 3: Spread Anomaly Hunter.
/// Detecta spreads anormalmente grandes en el libro de órdenes y aprovecha
/// órdenes mal colocadas (ask[0] significativamente menor que ask[1]).
/// </summary>
public sealed class SpreadAnomalyHunterSettings
{
    public bool Enabled { get; set; } = false;
    /// <summary>Tickers a monitorear.</summary>
    public List<string> Tickers { get; set; } = ["GGAL", "YPFD", "PAMP", "BMA"];
    public Settlement Settlement { get; set; } = Settlement.CI;
    /// <summary>Spread mínimo bid-ask (%) para considerar que hay una anomalía.</summary>
    public decimal MinSpreadPercent { get; set; } = 1.5m;
    /// <summary>Brecha mínima (%) entre ask[0] y ask[1] para señalar orden mal colocada.</summary>
    public decimal MinAnomalyGapPercent { get; set; } = 1.0m;
    /// <summary>Capital máximo ARS a desplegar por oportunidad.</summary>
    public decimal MaxCapitalPerOpArs { get; set; } = 15_000m;
    /// <summary>Profundidad mínima del lado bid (ARS) para asegurar salida viable.</summary>
    public decimal MinBidDepthArs { get; set; } = 20_000m;
    /// <summary>Posición máxima acumulable (unidades). 0 = sin límite propio (usa RiskManager).</summary>
    public decimal MaxInventoryUnits { get; set; } = 500m;
    /// <summary>Porcentaje del gap ask[0]-ask[1] a capturar como target de venta (0.8 = 80%).</summary>
    public decimal TargetGapCaptureRatio { get; set; } = 0.8m;
}

public sealed class ResilienceSettings
{
    public int MaxRetries { get; set; } = 3;
    public int BaseDelayMs { get; set; } = 200;
    public int MaxDelayMs { get; set; } = 5000;
    public int JitterMs { get; set; } = 250;
    public int CircuitBreakerFailures { get; set; } = 5;
    public int CircuitBreakerBreakSeconds { get; set; } = 30;
    public int FallbackPollingIntervalMs { get; set; } = 5000;
    public bool EnableFaultInjection { get; set; } = false;
    public decimal FaultInjectionProbabilityPercent { get; set; } = 0m;
}

public sealed class MarketSchedule
{
    public TimeOnly OpenTime { get; set; } = new(10, 30);
    public TimeOnly CloseTime { get; set; } = new(17, 0);
    public TimeOnly AuctionCloseTime { get; set; } = new(16, 57);
    public TimeOnly CIOpenTime { get; set; } = new(10, 30);
    public TimeOnly CICloseTime { get; set; } = new(17, 0);

    public bool IsMarketOpen()
    {
        var now = TimeOnly.FromDateTime(DateTime.Now);
        var dayOfWeek = DateTime.Now.DayOfWeek;
        return dayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday
               && now >= OpenTime && now <= CloseTime;
    }

    public bool IsMarketOpen(Interfaces.IClock clock)
    {
        var now = clock.LocalTimeNow;
        var dayOfWeek = clock.TodayLocal.DayOfWeek;
        return dayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday
               && now >= OpenTime && now <= CloseTime;
    }
}

// ─── Config Loader ───────────────────────────────────────────────────────────

public static class ConfigLoader
{
    private static readonly string DefaultPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

    public static AppSettings Load(string? path = null)
    {
        var filePath = path ?? DefaultPath;
        if (!File.Exists(filePath))
        {
            var defaults = new AppSettings();
            Save(defaults, filePath);
            return defaults;
        }

        var json = File.ReadAllText(filePath);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var settings = JsonSerializer.Deserialize<AppSettings>(json, options) ?? new AppSettings();
        settings.NormalizeAndProtectSecrets();
        Save(settings, filePath);
        return settings;
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        var filePath = path ?? DefaultPath;
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var json = JsonSerializer.Serialize(settings, options);
        File.WriteAllText(filePath, json);
    }
}

// ─── Credential Helper ───────────────────────────────────────────────────────

public static class CredentialHelper
{
    /// <summary>
    /// Encripta usando DPAPI (solo Windows, per-user).
    /// </summary>
    public static string Protect(string plainText)
    {
        var bytes = Encoding.UTF8.GetBytes(plainText);
        if (OperatingSystem.IsWindows())
        {
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }

        // Fallback no-Windows: evita romper ejecución fuera de Windows.
        return Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string protectedBase64)
    {
        try
        {
            var data = Convert.FromBase64String(protectedBase64);
            if (OperatingSystem.IsWindows())
            {
                var decrypted = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }

            return Encoding.UTF8.GetString(data);
        }
        catch
        {
            // Si falla, asumir que es plaintext (primera vez)
            return protectedBase64;
        }
    }
}
