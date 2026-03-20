using System.Collections.Concurrent;

namespace HPPAQDeploy.Shared.Helpers;

/// <summary>
/// Per-host circuit breaker to avoid hammering unreachable endpoints.
/// After <see cref="FailureThreshold"/> consecutive failures within
/// <see cref="TrackingWindow"/>, the circuit opens and rejects requests
/// for <see cref="OpenDuration"/> before allowing a single probe request.
/// </summary>
public class CircuitBreaker
{
    public int FailureThreshold { get; }
    public TimeSpan OpenDuration { get; }
    public TimeSpan TrackingWindow { get; }

    private readonly ConcurrentDictionary<string, HostCircuit> _circuits = new(StringComparer.OrdinalIgnoreCase);

    public CircuitBreaker(int failureThreshold = 3, TimeSpan? openDuration = null, TimeSpan? trackingWindow = null)
    {
        FailureThreshold = failureThreshold;
        OpenDuration = openDuration ?? TimeSpan.FromMinutes(2);
        TrackingWindow = trackingWindow ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Returns true if the host circuit is open (should not attempt connection).
    /// </summary>
    public bool IsOpen(string hostname)
    {
        if (!_circuits.TryGetValue(hostname, out var circuit))
            return false;

        if (circuit.State == CircuitState.Open && DateTime.UtcNow >= circuit.OpenUntil)
        {
            // Transition to half-open: allow one probe
            circuit.State = CircuitState.HalfOpen;
            return false;
        }

        return circuit.State == CircuitState.Open;
    }

    /// <summary>
    /// Records a successful operation, resetting the circuit to closed.
    /// </summary>
    public void RecordSuccess(string hostname)
    {
        if (_circuits.TryGetValue(hostname, out var circuit))
        {
            circuit.ConsecutiveFailures = 0;
            circuit.State = CircuitState.Closed;
            circuit.Failures.Clear();
        }
    }

    /// <summary>
    /// Records a failure. If threshold is exceeded, opens the circuit.
    /// </summary>
    public void RecordFailure(string hostname)
    {
        var circuit = _circuits.GetOrAdd(hostname, _ => new HostCircuit());

        // Trim old failures outside the tracking window
        var cutoff = DateTime.UtcNow - TrackingWindow;
        while (circuit.Failures.TryPeek(out var oldest) && oldest < cutoff)
            circuit.Failures.TryDequeue(out _);

        circuit.Failures.Enqueue(DateTime.UtcNow);
        circuit.ConsecutiveFailures++;

        if (circuit.ConsecutiveFailures >= FailureThreshold)
        {
            circuit.State = CircuitState.Open;
            circuit.OpenUntil = DateTime.UtcNow + OpenDuration;
        }
    }

    /// <summary>
    /// Resets the circuit for a specific host.
    /// </summary>
    public void Reset(string hostname)
    {
        _circuits.TryRemove(hostname, out _);
    }

    /// <summary>
    /// Resets all circuits.
    /// </summary>
    public void ResetAll()
    {
        _circuits.Clear();
    }

    private class HostCircuit
    {
        public CircuitState State = CircuitState.Closed;
        public int ConsecutiveFailures;
        public DateTime OpenUntil;
        public readonly ConcurrentQueue<DateTime> Failures = new();
    }

    private enum CircuitState
    {
        Closed,
        Open,
        HalfOpen
    }
}
