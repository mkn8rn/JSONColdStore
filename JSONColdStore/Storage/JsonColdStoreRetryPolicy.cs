namespace JSONColdStore.Storage;

internal static class JsonColdStoreRetryPolicy
{
    internal static async Task ExecuteAsync(
        JsonColdStoreRetryOptions options,
        Func<CancellationToken, Task> operation,
        Func<Exception, bool> shouldRetry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(shouldRetry);

        await ExecuteAsync<object?>(
            options,
            async token =>
            {
                await operation(token).ConfigureAwait(false);
                return null;
            },
            shouldRetry,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<TResult> ExecuteAsync<TResult>(
        JsonColdStoreRetryOptions options,
        Func<CancellationToken, Task<TResult>> operation,
        Func<Exception, bool> shouldRetry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(shouldRetry);

        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (attempt < options.MaxRetries && shouldRetry(exception))
            {
                attempt++;
                if (options.BaseDelay > TimeSpan.Zero)
                    await Task.Delay(options.BaseDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
