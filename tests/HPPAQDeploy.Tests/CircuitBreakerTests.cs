using HPPAQDeploy.Shared.Helpers;

namespace HPPAQDeploy.Tests;

public class CircuitBreakerTests
{
    [Fact]
    public void NewCircuit_IsClosed()
    {
        var cb = new CircuitBreaker(failureThreshold: 3);
        Assert.False(cb.IsOpen("host1"));
    }

    [Fact]
    public void OpensAfterThresholdFailures()
    {
        var cb = new CircuitBreaker(failureThreshold: 3);

        cb.RecordFailure("host1");
        cb.RecordFailure("host1");
        Assert.False(cb.IsOpen("host1"));

        cb.RecordFailure("host1");
        Assert.True(cb.IsOpen("host1"));
    }

    [Fact]
    public void SuccessResets_Circuit()
    {
        var cb = new CircuitBreaker(failureThreshold: 2);

        cb.RecordFailure("host1");
        cb.RecordFailure("host1");
        Assert.True(cb.IsOpen("host1"));

        cb.RecordSuccess("host1");
        Assert.False(cb.IsOpen("host1"));
    }

    [Fact]
    public void DifferentHosts_HaveIndependentCircuits()
    {
        var cb = new CircuitBreaker(failureThreshold: 2);

        cb.RecordFailure("host1");
        cb.RecordFailure("host1");
        Assert.True(cb.IsOpen("host1"));
        Assert.False(cb.IsOpen("host2"));
    }

    [Fact]
    public void Reset_ClearsCircuit()
    {
        var cb = new CircuitBreaker(failureThreshold: 2);

        cb.RecordFailure("host1");
        cb.RecordFailure("host1");
        Assert.True(cb.IsOpen("host1"));

        cb.Reset("host1");
        Assert.False(cb.IsOpen("host1"));
    }

    [Fact]
    public void HalfOpen_AllowsProbeAfterDuration()
    {
        var clock = new ManualTimeProvider();
        var cb = new CircuitBreaker(
            failureThreshold: 1,
            openDuration: TimeSpan.FromMinutes(1),
            timeProvider: clock);

        cb.RecordFailure("host1");
        Assert.True(cb.IsOpen("host1"));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(cb.IsOpen("host1"));
    }

    [Fact]
    public void HalfOpen_AllowsOnlyOneProbe()
    {
        var clock = new ManualTimeProvider();
        var cb = new CircuitBreaker(1, TimeSpan.FromMinutes(1), timeProvider: clock);
        cb.RecordFailure("host1");
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.False(cb.IsOpen("host1"));
        Assert.True(cb.IsOpen("host1"));

        cb.RecordFailure("host1");
        Assert.True(cb.IsOpen("host1"));
    }

    [Fact]
    public void FailuresOutsideTrackingWindow_DoNotAccumulate()
    {
        var clock = new ManualTimeProvider();
        var cb = new CircuitBreaker(
            failureThreshold: 2,
            trackingWindow: TimeSpan.FromMinutes(5),
            timeProvider: clock);

        cb.RecordFailure("host1");
        clock.Advance(TimeSpan.FromMinutes(6));
        cb.RecordFailure("host1");

        Assert.False(cb.IsOpen("host1"));
        cb.RecordFailure("host1");
        Assert.True(cb.IsOpen("host1"));
    }

    [Fact]
    public void CaseInsensitive_HostMatching()
    {
        var cb = new CircuitBreaker(failureThreshold: 2);

        cb.RecordFailure("HOST1");
        cb.RecordFailure("host1");
        Assert.True(cb.IsOpen("Host1"));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
