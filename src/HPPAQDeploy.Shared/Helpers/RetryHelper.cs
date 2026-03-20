using System.Runtime.InteropServices;
using HPPAQDeploy.Shared.Configuration;
using Serilog;

namespace HPPAQDeploy.Shared.Helpers;

public static class RetryHelper
{
    private static readonly ILogger Logger = Log.ForContext(typeof(RetryHelper));

    private static readonly HashSet<Type> TransientExceptions = new()
    {
        typeof(TimeoutException),
        typeof(IOException),
        typeof(COMException),
        typeof(UnauthorizedAccessException)
    };

    /// <summary>
    /// Determines whether an exception is transient and eligible for retry.
    /// </summary>
    private static bool IsTransient(Exception ex)
    {
        // Check the exception itself and any inner exceptions
        var current = ex;
        while (current != null)
        {
            if (TransientExceptions.Contains(current.GetType()))
                return true;
            current = current.InnerException;
        }

        return false;
    }

    /// <summary>
    /// Retries an async operation up to <paramref name="maxRetries"/> times with exponential backoff.
    /// Only transient exceptions (TimeoutException, IOException, COMException, UnauthorizedAccessException)
    /// are retried; all other exceptions propagate immediately.
    /// </summary>
    public static async Task<T> RetryAsync<T>(
        Func<Task<T>> action,
        int? maxRetries = null,
        int? baseDelayMs = null,
        CancellationToken ct = default,
        [System.Runtime.CompilerServices.CallerMemberName] string? caller = null)
    {
        int retries = maxRetries ?? AppSettings.RetryMaxAttempts;
        int delayMs = baseDelayMs ?? AppSettings.RetryBaseDelayMs;
        Exception lastException = null!;

        for (int attempt = 0; attempt <= retries; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw; // Never retry cancellation
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                lastException = ex;

                if (attempt < retries)
                {
                    int delay = delayMs * (1 << attempt); // 2s, 4s, 8s
                    Logger.Warning(ex,
                        "Transient error in {Caller} (attempt {Attempt}/{MaxRetries}), retrying in {Delay}ms",
                        caller, attempt + 1, retries, delay);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                else
                {
                    Logger.Error(ex,
                        "Transient error in {Caller} (attempt {Attempt}/{MaxRetries}), no more retries",
                        caller, attempt + 1, retries);
                }
            }
            // Non-transient exceptions propagate immediately (no catch)
        }

        throw lastException;
    }

    /// <summary>
    /// Retries an async void-returning operation with the same semantics as the generic overload.
    /// </summary>
    public static async Task RetryAsync(
        Func<Task> action,
        int? maxRetries = null,
        int? baseDelayMs = null,
        CancellationToken ct = default,
        [System.Runtime.CompilerServices.CallerMemberName] string? caller = null)
    {
        int retries = maxRetries ?? AppSettings.RetryMaxAttempts;
        int delayMs = baseDelayMs ?? AppSettings.RetryBaseDelayMs;
        Exception lastException = null!;

        for (int attempt = 0; attempt <= retries; attempt++)
        {
            try
            {
                await action().ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                lastException = ex;

                if (attempt < retries)
                {
                    int delay = delayMs * (1 << attempt);
                    Logger.Warning(ex,
                        "Transient error in {Caller} (attempt {Attempt}/{MaxRetries}), retrying in {Delay}ms",
                        caller, attempt + 1, retries, delay);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                else
                {
                    Logger.Error(ex,
                        "Transient error in {Caller} (attempt {Attempt}/{MaxRetries}), no more retries",
                        caller, attempt + 1, retries);
                }
            }
        }

        throw lastException;
    }
}
