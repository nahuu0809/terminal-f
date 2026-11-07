using FinancialTerminal.Core.Configuration;
using FinancialTerminal.Core.Resilience;
using Xunit;

namespace FinancialTerminal.Tests;

public sealed class ResilienceTests
{
    [Fact]
    public void CircuitBreaker_OpensAfterThreshold()
    {
        var breaker = new SimpleCircuitBreaker(failureThreshold: 2, openDuration: TimeSpan.FromSeconds(2));

        breaker.RecordFailure();
        Assert.False(breaker.IsOpen);

        breaker.RecordFailure();
        Assert.True(breaker.IsOpen);

        Assert.Throws<CircuitOpenException>(() => breaker.ThrowIfOpen());
    }

    [Fact]
    public void RetryBackoff_IsBoundedByMaxDelay()
    {
        var settings = new ResilienceSettings
        {
            BaseDelayMs = 100,
            MaxDelayMs = 400,
            JitterMs = 0
        };

        var random = new Random(123);
        var d1 = RetryBackoff.ComputeDelay(1, settings, random).TotalMilliseconds;
        var d2 = RetryBackoff.ComputeDelay(2, settings, random).TotalMilliseconds;
        var d3 = RetryBackoff.ComputeDelay(10, settings, random).TotalMilliseconds;

        Assert.Equal(100, d1);
        Assert.Equal(200, d2);
        Assert.Equal(400, d3);
    }

    [Fact]
    public void FaultInjection_ThrowsWhenProbabilityIs100()
    {
        var settings = new ResilienceSettings
        {
            EnableFaultInjection = true,
            FaultInjectionProbabilityPercent = 100m
        };

        var random = new Random(1);
        Assert.Throws<HttpRequestException>(() => FaultInjection.MaybeThrow(settings, random, "test-op"));
    }
}
