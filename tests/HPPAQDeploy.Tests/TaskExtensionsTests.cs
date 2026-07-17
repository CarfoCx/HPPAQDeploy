using HPPAQDeploy.Core.Extensions;
using System.Collections.Concurrent;

namespace HPPAQDeploy.Tests;

public class TaskExtensionsTests
{
    [Fact]
    public async Task ParallelForEachAsync_BoundsConcurrencyAndReportsCompletion()
    {
        var active = 0;
        var maximumActive = 0;
        var progressValues = new ConcurrentBag<(int completed, int total)>();
        var progress = new InlineProgress<(int completed, int total)>(value => progressValues.Add(value));

        await Enumerable.Range(1, 20).ParallelForEachAsync(
            maxConcurrency: 3,
            async (_, ct) =>
            {
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, current);
                await Task.Delay(15, ct);
                Interlocked.Decrement(ref active);
            },
            progress,
            CancellationToken.None);

        Assert.InRange(maximumActive, 2, 3);
        Assert.Equal(20, progressValues.Count);
        Assert.All(progressValues, value => Assert.Equal(20, value.total));
        Assert.Equal(20, progressValues.Max(value => value.completed));
    }

    [Fact]
    public async Task ParallelForEachAsync_RejectsInvalidConcurrency()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Array.Empty<int>().ParallelForEachAsync(
                0,
                (_, _) => Task.CompletedTask,
                progress: null,
                CancellationToken.None));
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        int observed;
        do
        {
            observed = maximum;
            if (candidate <= observed) return;
        }
        while (Interlocked.CompareExchange(ref maximum, candidate, observed) != observed);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
