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
        // Use a very short open duration
        var cb = new CircuitBreaker(failureThreshold: 1, openDuration: TimeSpan.FromMilliseconds(50));

        cb.RecordFailure("host1");
        Assert.True(cb.IsOpen("host1"));

        Thread.Sleep(100);
        // After duration, should transition to half-open and allow probe
        Assert.False(cb.IsOpen("host1"));
    }

    [Fact]
    public void CaseInsensitive_HostMatching()
    {
        var cb = new CircuitBreaker(failureThreshold: 2);

        cb.RecordFailure("HOST1");
        cb.RecordFailure("host1");
        Assert.True(cb.IsOpen("Host1"));
    }
}
