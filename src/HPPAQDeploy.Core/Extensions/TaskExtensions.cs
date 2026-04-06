namespace HPPAQDeploy.Core.Extensions;

public static class TaskExtensions
{
    public static async Task ParallelForEachAsync<T>(
        IEnumerable<T> source,
        int maxConcurrency,
        Func<T, CancellationToken, Task> action,
        IProgress<(int completed, int total)>? progress,
        CancellationToken ct)
    {
        var items = source as IReadOnlyList<T> ?? source.ToList();
        int total = items.Count;
        int completed = 0;

        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var tasks = items.Select(async item =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await action(item, ct).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
                int current = Interlocked.Increment(ref completed);
                progress?.Report((current, total));
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
