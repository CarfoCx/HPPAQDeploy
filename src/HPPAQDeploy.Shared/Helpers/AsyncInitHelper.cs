using Serilog;

namespace HPPAQDeploy.Shared.Helpers;

/// <summary>
/// Helper for safe fire-and-forget async initialization in constructors.
/// Ensures exceptions are logged rather than silently swallowed.
/// </summary>
public static class AsyncInitHelper
{
    /// <summary>
    /// Runs an async initialization task safely, logging any exceptions.
    /// Use this instead of <c>_ = SomeAsync()</c> in constructors.
    /// </summary>
    public static async void SafeFireAndForget(Func<Task> action, string context)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Async initialization failed in {Context}", context);
        }
    }

    /// <summary>
    /// Runs an already-started async task safely, logging any exceptions.
    /// </summary>
    public static async void SafeFireAndForget(Task task, string context)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Async operation failed in {Context}", context);
        }
    }
}
