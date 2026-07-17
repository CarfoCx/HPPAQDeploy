namespace HPPAQDeploy.Core.Extensions;

public static class TaskExtensions
{
    public static async Task ParallelForEachAsync<T>(
        this IEnumerable<T> source,
        int maxConcurrency,
        Func<T, CancellationToken, Task> action,
        IProgress<(int completed, int total)>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(action);
        if (maxConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency));

        var items = source as IReadOnlyList<T> ?? source.ToList();
        int total = items.Count;
        int completed = 0;

        await Parallel.ForEachAsync(
            items,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxConcurrency,
                CancellationToken = ct
            },
            async (item, token) =>
            {
                try
                {
                    await action(item, token).ConfigureAwait(false);
                }
                finally
                {
                    int current = Interlocked.Increment(ref completed);
                    progress?.Report((current, total));
                }
            }).ConfigureAwait(false);
    }
}
