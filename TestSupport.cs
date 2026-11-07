using FinancialTerminal.Core.Configuration;

namespace FinancialTerminal.Tests;

internal static class TestSupport
{
    public static AppSettings CreateSettings()
    {
        return new AppSettings
        {
            Trading = new TradingSettings
            {
                InitialCapital = 100_000m,
                MaxCapitalPerOperationPercent = 25m,
                MinNetProfit = 1m,
                PollingIntervalMs = 250
            },
            Risk = new RiskSettings
            {
                MaxDailyLossPercent = 10m,
                MaxDailyLossArs = 5_000m,
                MaxPositionPerTicker = 100m,
                StopTradingFlag = false,
                MaxConsecutiveExecutionFailures = 3,
                MaxExecutionLatencyMs = 20_000,
                EnableLatencyKillSwitch = true
            }
        };
    }

    public static string CreateTempDbPath()
    {
        var fileName = $"ft-tests-{Guid.NewGuid():N}.sqlite3";
        return Path.Combine(Path.GetTempPath(), fileName);
    }
}
