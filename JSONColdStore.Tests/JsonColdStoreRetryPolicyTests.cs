using JSONColdStore;
using JSONColdStore.Storage;

namespace JSONColdStore.Tests;

public sealed class JsonColdStoreRetryPolicyTests
{
    [Fact]
    public async Task ExecuteAsyncRetriesTransientFailuresUntilSuccess()
    {
        var attempts = 0;
        var options = new JsonColdStoreRetryOptions
        {
            MaxRetries = 2,
            BaseDelay = TimeSpan.Zero,
        };

        var result = await JsonColdStoreRetryPolicy.ExecuteAsync(
            options,
            _ =>
            {
                attempts++;
                if (attempts < 3)
                    throw new IOException("transient");

                return Task.FromResult("ok");
            },
            exception => exception is IOException);

        Assert.Equal("ok", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExecuteAsyncStopsAfterConfiguredRetries()
    {
        var attempts = 0;
        var options = new JsonColdStoreRetryOptions
        {
            MaxRetries = 1,
            BaseDelay = TimeSpan.Zero,
        };

        await Assert.ThrowsAsync<IOException>(() => JsonColdStoreRetryPolicy.ExecuteAsync(
            options,
            _ =>
            {
                attempts++;
                throw new IOException("still failing");
            },
            exception => exception is IOException));

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ExecuteAsyncDoesNotRetryRejectedExceptionTypes()
    {
        var attempts = 0;
        var options = new JsonColdStoreRetryOptions
        {
            MaxRetries = 3,
            BaseDelay = TimeSpan.Zero,
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => JsonColdStoreRetryPolicy.ExecuteAsync(
            options,
            _ =>
            {
                attempts++;
                throw new InvalidDataException("not transient");
            },
            exception => exception is IOException));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsyncDoesNotRetryUnsafePathReplayFailures()
    {
        var attempts = 0;
        var options = new JsonColdStoreRetryOptions
        {
            MaxRetries = 3,
            BaseDelay = TimeSpan.Zero,
        };

        await Assert.ThrowsAsync<JsonColdStoreUnsafePathException>(() => JsonColdStoreRetryPolicy.ExecuteAsync(
            options,
            _ =>
            {
                attempts++;
                throw new JsonColdStoreUnsafePathException("fail closed");
            },
            JsonColdStoreRecordStore.IsTransientReplayException));

        Assert.Equal(1, attempts);
    }
}
