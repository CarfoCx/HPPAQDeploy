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
    private readonly TimeProvider _timeProvider;

    public CircuitBreaker(
        int failureThreshold = 3,
        TimeSpan? openDuration = null,
        TimeSpan? trackingWindow = null,
        TimeProvider? timeProvider = null)
    {
        if (failureThreshold <= 0)
            throw new ArgumentOutOfRangeException(nameof(failureThreshold));

        FailureThreshold = failureThreshold;
        OpenDuration = openDuration ?? TimeSpan.FromMinutes(2);
        TrackingWindow = trackingWindow ?? TimeSpan.FromMinutes(5);
        if (OpenDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(openDuration));
        if (TrackingWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(trackingWindow));

        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns true if the host circuit is open (should not attempt connection).
    /// </summary>
    public bool IsOpen(string hostname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        if (!_circuits.TryGetValue(hostname, out var circuit))
            return false;

        lock (circuit.Gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (circuit.State == CircuitState.Open && now >= circuit.OpenUntil)
            {
                // Transition to half-open: allow one probe
                circuit.State = CircuitState.HalfOpen;
                circuit.ProbeLeaseUntil = now + OpenDuration;
                return false;
            }

            if (circuit.State == CircuitState.HalfOpen)
            {
                // If a caller disappeared without recording an outcome, eventually
                // release the probe lease rather than blocking this host forever.
                if (now >= circuit.ProbeLeaseUntil)
                {
                    circuit.ProbeLeaseUntil = now + OpenDuration;
                    return false;
                }

                return true;
            }

            return circuit.State == CircuitState.Open;
        }
    }

    /// <summary>
    /// Records a successful operation, resetting the circuit to closed.
    /// </summary>
    public void RecordSuccess(string hostname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        if (_circuits.TryGetValue(hostname, out var circuit))
        {
            lock (circuit.Gate)
            {
                circuit.State = CircuitState.Closed;
                circuit.Failures.Clear();
            }
        }
    }

    /// <summary>
    /// Records a failure. If threshold is exceeded, opens the circuit.
    /// </summary>
    public void RecordFailure(string hostname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        var circuit = _circuits.GetOrAdd(hostname, _ => new HostCircuit());

        lock (circuit.Gate)
        {
            var now = _timeProvider.GetUtcNow();

            if (circuit.State == CircuitState.HalfOpen)
            {
                Open(circuit, now);
                return;
            }

            // Trim old failures outside the tracking window
            var cutoff = now - TrackingWindow;
            while (circuit.Failures.Count > 0 && circuit.Failures.Peek() < cutoff)
                circuit.Failures.Dequeue();

            circuit.Failures.Enqueue(now);

            if (circuit.Failures.Count >= FailureThreshold)
                Open(circuit, now);
        }
    }

    /// <summary>
    /// Resets the circuit for a specific host.
    /// </summary>
    public void Reset(string hostname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
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
        public readonly object Gate = new();
        public CircuitState State = CircuitState.Closed;
        public DateTimeOffset OpenUntil;
        public DateTimeOffset ProbeLeaseUntil;
        public readonly Queue<DateTimeOffset> Failures = new();
    }

    private void Open(HostCircuit circuit, DateTimeOffset now)
    {
        circuit.State = CircuitState.Open;
        circuit.OpenUntil = now + OpenDuration;
    }

    private enum CircuitState
    {
        Closed,
        Open,
        HalfOpen
    }
}
