using FinancialTerminal.Core.Configuration;

namespace FinancialTerminal.Core.Resilience;

public sealed class CircuitOpenException : Exception
{
    public DateTime OpenUntilUtc { get; }

    public CircuitOpenException(string message, DateTime openUntilUtc) : base(message)
    {
        OpenUntilUtc = openUntilUtc;
    }
}

public sealed class SimpleCircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;
    private readonly object _gate = new();
    private int _failures;
    private DateTime _openUntilUtc = DateTime.MinValue;

    public SimpleCircuitBreaker(int failureThreshold, TimeSpan openDuration)
    {
        _failureThreshold = Math.Max(1, failureThreshold);
        _openDuration = openDuration <= TimeSpan.Zero ? TimeSpan.FromSeconds(15) : openDuration;
    }

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return DateTime.UtcNow < _openUntilUtc;
            }
        }
    }

    public void ThrowIfOpen()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow < _openUntilUtc)
            {
                throw new CircuitOpenException("Circuit breaker is open", _openUntilUtc);
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _failures = 0;
            _openUntilUtc = DateTime.MinValue;
        }
    }

    public void RecordFailure()
    {
        lock (_gate)
        {
            _failures++;
            if (_failures >= _failureThreshold)
            {
                _openUntilUtc = DateTime.UtcNow.Add(_openDuration);
                _failures = 0;
            }
        }
    }
}

public static class RetryBackoff
{
    public static TimeSpan ComputeDelay(int attempt, ResilienceSettings settings, Random random)
    {
        var boundedAttempt = Math.Max(1, attempt);
        var exponential = settings.BaseDelayMs * Math.Pow(2, boundedAttempt - 1);
        var capped = Math.Min(settings.MaxDelayMs, exponential);
        var jitter = settings.JitterMs <= 0 ? 0 : random.Next(0, settings.JitterMs + 1);
        return TimeSpan.FromMilliseconds(capped + jitter);
    }
}

public static class FaultInjection
{
    public static void MaybeThrow(ResilienceSettings settings, Random random, string operation)
    {
        if (!settings.EnableFaultInjection)
            return;

        var probability = Math.Clamp(settings.FaultInjectionProbabilityPercent, 0m, 100m);
        if (probability <= 0m)
            return;

        var roll = (decimal)(random.NextDouble() * 100.0);
        if (roll < probability)
        {
            throw new HttpRequestException($"Injected network failure on operation '{operation}'");
        }
    }
}
