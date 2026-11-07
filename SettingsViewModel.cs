using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FinancialTerminal.Core.Configuration;
using FinancialTerminal.Core.Models;

namespace FinancialTerminal.App.ViewModels;

/// <summary>
/// ViewModel for the Settings window. Edits a deep copy; only persists on Save.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _liveSettings;
    private readonly string? _settingsPath;

    // ── General ──
    [ObservableProperty] private string _mode = "sandbox";

    // ── Primary ──
    [ObservableProperty] private string _primaryBaseUrl = "";
    [ObservableProperty] private string _primaryUsername = "";
    [ObservableProperty] private string _primaryPassword = "";
    [ObservableProperty] private string _primaryAccount = "";
    [ObservableProperty] private bool _primaryUseWebSocket = true;
    [ObservableProperty] private int _primaryWsReconnectMs = 5000;

    // ── PPI ──
    [ObservableProperty] private string _ppiUsername = "";
    [ObservableProperty] private string _ppiPassword = "";
    [ObservableProperty] private string _ppiAccountNumber = "";
    [ObservableProperty] private bool _ppiUseSandbox;
    [ObservableProperty] private int _ppiRateLimitPerMinute = 60;

    // ── Trading ──
    [ObservableProperty] private decimal _initialCapital = 100_000m;
    [ObservableProperty] private decimal _minNetProfit = 500m;
    [ObservableProperty] private decimal _maxCapitalPerOperationPercent = 25m;
    [ObservableProperty] private int _pollingIntervalMs = 5000;
    [ObservableProperty] private decimal _maxSlippagePercent = 0.3m;

    // ── Paper Trading ──
    [ObservableProperty] private bool _paperEnabled = true;
    [ObservableProperty] private decimal _paperInitialCashArs = 1_000_000m;
    [ObservableProperty] private decimal _paperInitialCashUsd;
    [ObservableProperty] private decimal _paperDefaultSlippageBps = 10m;

    // ── Risk ──
    [ObservableProperty] private decimal _maxDailyLossPercent = 10m;
    [ObservableProperty] private decimal _maxDailyLossArs = 50_000m;
    [ObservableProperty] private decimal _maxPositionPerTicker = 100m;
    [ObservableProperty] private int _maxConsecutiveFailures = 5;
    [ObservableProperty] private bool _enableLatencyKillSwitch = true;

    // ── CI vs 24hs ──
    [ObservableProperty] private bool _arbitrageEnabled = true;
    [ObservableProperty] private decimal _arbitrageTasaMinimaTNA = 0.25m;
    [ObservableProperty] private string _arbitrageTickers = "";

    // ── MEP/CCL ──
    [ObservableProperty] private bool _mepCclEnabled;
    [ObservableProperty] private decimal _mepCclMinSpreadPercent = 0.3m;
    [ObservableProperty] private string _mepCclBondBases = "";
    [ObservableProperty] private string _mepCclArsSettlement = "CI";
    [ObservableProperty] private string _mepCclUsdSettlement = "T24hs";

    // ── Market Making ──
    [ObservableProperty] private bool _mmEnabled;
    [ObservableProperty] private string _mmTicker = "GGAL";
    [ObservableProperty] private decimal _mmMinSpread = 0.5m;
    [ObservableProperty] private decimal _mmOrderSize = 10m;
    [ObservableProperty] private decimal _mmMaxPosition = 100m;

    // ── Caucion ──
    [ObservableProperty] private bool _caucionEnabled = true;
    [ObservableProperty] private decimal _caucionMinRatePercent = 40m;
    [ObservableProperty] private string _caucionCloseTime = "16:50";

    // ── Options (PUTs) ──
    [ObservableProperty] private bool _optionsEnabled;
    [ObservableProperty] private decimal _optionsComisionOpcionesPct = 0.37m;
    [ObservableProperty] private decimal _optionsComisionAccionesPct = 0.22m;
    [ObservableProperty] private decimal _optionsMinRatioPct = 0.5m;
    [ObservableProperty] private string _optionsUnderlyings = "";
    [ObservableProperty] private string _optionsPutTickers = "";

    // ── Market Schedule ──
    [ObservableProperty] private string _marketOpenTime = "10:30";
    [ObservableProperty] private string _marketCloseTime = "17:00";

    // ── Status ──
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isDirty;

    public IReadOnlyDictionary<string, string> KnownAlycs => PrimarySettings.KnownAlycs;

    public SettingsViewModel(AppSettings settings, string? settingsPath = null)
    {
        _liveSettings = settings;
        _settingsPath = settingsPath;
        LoadFromSettings(settings);
    }

    private void LoadFromSettings(AppSettings s)
    {
        Mode = s.Mode;

        PrimaryBaseUrl = s.Primary.BaseUrl;
        PrimaryUsername = s.Primary.Username;
        PrimaryPassword = s.Primary.Password;
        PrimaryAccount = s.Primary.Account;
        PrimaryUseWebSocket = s.Primary.UseWebSocket;
        PrimaryWsReconnectMs = s.Primary.WebSocketReconnectMs;

        PpiUsername = s.PPI.Username;
        PpiPassword = s.PPI.Password;
        PpiAccountNumber = s.PPI.AccountNumber;
        PpiUseSandbox = s.PPI.UseSandbox;
        PpiRateLimitPerMinute = s.PPI.RateLimitPerMinute;

        InitialCapital = s.Trading.InitialCapital;
        MinNetProfit = s.Trading.MinNetProfit;
        MaxCapitalPerOperationPercent = s.Trading.MaxCapitalPerOperationPercent;
        PollingIntervalMs = s.Trading.PollingIntervalMs;
        MaxSlippagePercent = s.Trading.MaxSlippagePercent;

        PaperEnabled = s.PaperTrading.Enabled;
        PaperInitialCashArs = s.PaperTrading.InitialCashArs;
        PaperInitialCashUsd = s.PaperTrading.InitialCashUsd;
        PaperDefaultSlippageBps = s.PaperTrading.DefaultSlippageBps;

        MaxDailyLossPercent = s.Risk.MaxDailyLossPercent;
        MaxDailyLossArs = s.Risk.MaxDailyLossArs;
        MaxPositionPerTicker = s.Risk.MaxPositionPerTicker;
        MaxConsecutiveFailures = s.Risk.MaxConsecutiveExecutionFailures;
        EnableLatencyKillSwitch = s.Risk.EnableLatencyKillSwitch;

        ArbitrageEnabled = s.Arbitrage.Enabled;
        ArbitrageTasaMinimaTNA = s.Arbitrage.TasaMinimaTNA;
        ArbitrageTickers = string.Join(", ", s.Arbitrage.Tickers);

        MepCclEnabled = s.MepCcl.Enabled;
        MepCclMinSpreadPercent = s.MepCcl.MinSpreadPercent;
        MepCclBondBases = string.Join(", ", s.MepCcl.BondBases);
        MepCclArsSettlement = s.MepCcl.ArsSettlement.ToString();
        MepCclUsdSettlement = s.MepCcl.UsdSettlement.ToString();

        MmEnabled = s.MarketMaking.Enabled;
        MmTicker = s.MarketMaking.Ticker;
        MmMinSpread = s.MarketMaking.MinSpread;
        MmOrderSize = s.MarketMaking.OrderSize;
        MmMaxPosition = s.MarketMaking.MaxPosition;

        CaucionEnabled = s.Caucion.EnableAutoAtClose;
        CaucionMinRatePercent = s.Caucion.MinRatePercent;
        CaucionCloseTime = s.Caucion.CloseExecutionTime.ToString("HH:mm");

        OptionsEnabled = s.Options.Enabled;
        OptionsComisionOpcionesPct = s.Options.ComisionOpcionesPct;
        OptionsComisionAccionesPct = s.Options.ComisionAccionesPct;
        OptionsMinRatioPct = s.Options.MinRatioPct;
        OptionsUnderlyings = string.Join(", ", s.Options.Underlyings);
        OptionsPutTickers = string.Join(", ", s.Options.PutTickers);

        MarketOpenTime = s.Market.OpenTime.ToString("HH:mm");
        MarketCloseTime = s.Market.CloseTime.ToString("HH:mm");

        IsDirty = false;
        StatusText = "";
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != nameof(IsDirty) && e.PropertyName != nameof(StatusText))
            IsDirty = true;
    }

    [RelayCommand]
    private void Save()
    {
        ApplyToSettings(_liveSettings);
        try
        {
            ConfigLoader.Save(_liveSettings, _settingsPath);
            StatusText = "Guardado correctamente";
            IsDirty = false;
        }
        catch (Exception ex)
        {
            StatusText = $"Error al guardar: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        LoadFromSettings(new AppSettings());
        StatusText = "Valores por defecto cargados (no guardados)";
    }

    private void ApplyToSettings(AppSettings s)
    {
        s.Mode = Mode;

        s.Primary.BaseUrl = PrimaryBaseUrl;
        s.Primary.Username = PrimaryUsername;
        s.Primary.Password = PrimaryPassword;
        s.Primary.Account = PrimaryAccount;
        s.Primary.UseWebSocket = PrimaryUseWebSocket;
        s.Primary.WebSocketReconnectMs = PrimaryWsReconnectMs;

        s.PPI.Username = PpiUsername;
        s.PPI.Password = PpiPassword;
        s.PPI.AccountNumber = PpiAccountNumber;
        s.PPI.UseSandbox = PpiUseSandbox;
        s.PPI.RateLimitPerMinute = PpiRateLimitPerMinute;

        s.Trading.InitialCapital = InitialCapital;
        s.Trading.MinNetProfit = MinNetProfit;
        s.Trading.MaxCapitalPerOperationPercent = MaxCapitalPerOperationPercent;
        s.Trading.PollingIntervalMs = PollingIntervalMs;
        s.Trading.MaxSlippagePercent = MaxSlippagePercent;

        s.PaperTrading.Enabled = PaperEnabled;
        s.PaperTrading.InitialCashArs = PaperInitialCashArs;
        s.PaperTrading.InitialCashUsd = PaperInitialCashUsd;
        s.PaperTrading.DefaultSlippageBps = PaperDefaultSlippageBps;

        s.Risk.MaxDailyLossPercent = MaxDailyLossPercent;
        s.Risk.MaxDailyLossArs = MaxDailyLossArs;
        s.Risk.MaxPositionPerTicker = MaxPositionPerTicker;
        s.Risk.MaxConsecutiveExecutionFailures = MaxConsecutiveFailures;
        s.Risk.EnableLatencyKillSwitch = EnableLatencyKillSwitch;

        s.Arbitrage.Enabled = ArbitrageEnabled;
        s.Arbitrage.TasaMinimaTNA = ArbitrageTasaMinimaTNA;
        s.Arbitrage.Tickers = ParseCsvList(ArbitrageTickers);

        s.MepCcl.Enabled = MepCclEnabled;
        s.MepCcl.MinSpreadPercent = MepCclMinSpreadPercent;
        s.MepCcl.BondBases = ParseCsvList(MepCclBondBases);
        if (Enum.TryParse<Settlement>(MepCclArsSettlement, true, out var arsS)) s.MepCcl.ArsSettlement = arsS;
        if (Enum.TryParse<Settlement>(MepCclUsdSettlement, true, out var usdS)) s.MepCcl.UsdSettlement = usdS;

        s.MarketMaking.Enabled = MmEnabled;
        s.MarketMaking.Ticker = MmTicker;
        s.MarketMaking.MinSpread = MmMinSpread;
        s.MarketMaking.OrderSize = MmOrderSize;
        s.MarketMaking.MaxPosition = MmMaxPosition;

        s.Caucion.EnableAutoAtClose = CaucionEnabled;
        s.Caucion.MinRatePercent = CaucionMinRatePercent;
        if (TimeOnly.TryParse(CaucionCloseTime, out var ct)) s.Caucion.CloseExecutionTime = ct;

        s.Options.Enabled = OptionsEnabled;
        s.Options.ComisionOpcionesPct = OptionsComisionOpcionesPct;
        s.Options.ComisionAccionesPct = OptionsComisionAccionesPct;
        s.Options.MinRatioPct = OptionsMinRatioPct;
        s.Options.Underlyings = ParseCsvList(OptionsUnderlyings);
        s.Options.PutTickers = ParseCsvList(OptionsPutTickers);

        if (TimeOnly.TryParse(MarketOpenTime, out var ot)) s.Market.OpenTime = ot;
        if (TimeOnly.TryParse(MarketCloseTime, out var clt)) s.Market.CloseTime = clt;
    }

    private static List<string> ParseCsvList(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Where(s => s.Length > 0)
           .ToList();
}
