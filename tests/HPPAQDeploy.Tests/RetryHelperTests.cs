using HPPAQDeploy.Shared.Helpers;

namespace HPPAQDeploy.Tests;

public class RetryHelperTests
{
    [Fact]
    public async Task RetryAsync_SucceedsOnFirstAttempt()
    {
        var callCount = 0;
        var result = await RetryHelper.RetryAsync(async () =>
        {
            callCount++;
            await Task.CompletedTask;
            return 42;
        }, maxRetries: 3, baseDelayMs: 10);

        Assert.Equal(42, result);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task RetryAsync_RetriesOnTransientException()
    {
        var callCount = 0;
        var result = await RetryHelper.RetryAsync(async () =>
        {
            callCount++;
            await Task.CompletedTask;
            if (callCount < 3)
                throw new IOException("transient");
            return "ok";
        }, maxRetries: 3, baseDelayMs: 10);

        Assert.Equal("ok", result);
        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task RetryAsync_DoesNotRetry_NonTransientException()
    {
        var callCount = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await RetryHelper.RetryAsync<int>(async () =>
            {
                callCount++;
                await Task.CompletedTask;
                throw new InvalidOperationException("not transient");
            }, maxRetries: 3, baseDelayMs: 10);
        });

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task RetryAsync_ThrowsAfterMaxRetries()
    {
        var callCount = 0;
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await RetryHelper.RetryAsync<int>(async () =>
            {
                callCount++;
                await Task.CompletedTask;
                throw new IOException("always fails");
            }, maxRetries: 2, baseDelayMs: 10);
        });

        Assert.Equal(3, callCount); // initial + 2 retries
    }

    [Fact]
    public async Task RetryAsync_NeverRetries_OperationCanceled()
    {
        var callCount = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await RetryHelper.RetryAsync<int>(async () =>
            {
                callCount++;
                await Task.CompletedTask;
                throw new OperationCanceledException();
            }, maxRetries: 3, baseDelayMs: 10);
        });

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task RetryAsync_VoidOverload_Works()
    {
        var callCount = 0;
        await RetryHelper.RetryAsync(async () =>
        {
            callCount++;
            await Task.CompletedTask;
            if (callCount < 2)
                throw new TimeoutException("transient");
        }, maxRetries: 3, baseDelayMs: 10);

        Assert.Equal(2, callCount);
    }
}
