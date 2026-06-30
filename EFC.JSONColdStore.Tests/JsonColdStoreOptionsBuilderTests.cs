using EFC.JSONColdStore;

namespace EFC.JSONColdStore.Tests;

public sealed class JsonColdStoreOptionsBuilderTests
{
    [Fact]
    public void BuildAppliesSafeDefaults()
    {
        var options = new JsonColdStoreOptionsBuilder(TestDirectory("store")).Build();

        Assert.Equal(JsonColdStoreCompression.Auto, options.Compression);
        Assert.Equal(JsonColdStoreStartupMode.MetadataOnly, options.StartupMode);
        Assert.Equal(JsonColdStoreDurability.ManifestedAtomicWrites, options.Durability);
        Assert.True(options.FsyncOnWrite);
        Assert.True(options.AsyncFlush.Enabled);
        Assert.Equal(256, options.AsyncFlush.QueueCapacity);
        Assert.True(options.Integrity.EnableChecksums);
        Assert.True(options.Integrity.VerifyOnStartup);
        Assert.False(options.Integrity.VerifyOnRead);
        Assert.Equal(3, options.ReadRetry.MaxRetries);
        Assert.Equal(TimeSpan.FromMilliseconds(25), options.ReadRetry.BaseDelay);
    }

    [Fact]
    public void BuildAppliesAdvancedPolicies()
    {
        using var key = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);

        var options = new JsonColdStoreOptionsBuilder(TestDirectory("store"))
            .UseCompression(JsonColdStoreCompression.Brotli)
            .UseEncryption(new JsonColdStoreEncryptionOptions
            {
                Key = key,
                KeyId = "test-key",
                RequireEncryptedStore = true,
            })
            .UseStartupMode(JsonColdStoreStartupMode.FullHydration)
            .UseFsyncOnWrite(false)
            .UseAsyncFlush(queueCapacity: 32)
            .UseFlushRetry(maxRetries: 5, baseDelay: TimeSpan.FromMilliseconds(25))
            .UseTransactionReplay(maxRetries: 7)
            .UseReadRetry(maxRetries: 4, baseDelay: TimeSpan.FromMilliseconds(10))
            .UseChecksums(verifyOnStartup: false, verifyOnRead: true)
            .UseQuarantine(TimeSpan.FromDays(9))
            .UseIndexMaintenance(TimeSpan.Zero, rebuildOnCatalogMismatch: false)
            .UseEventLog(enabled: true, retention: TimeSpan.FromDays(3))
            .UseSnapshots(enabled: true, interval: TimeSpan.FromHours(6), retentionCount: 2)
            .UseFullScanPolicy(JsonColdStoreScanPolicy.AllowExplicitScans)
            .Build();

        Assert.Equal(JsonColdStoreCompression.Brotli, options.Compression);
        Assert.Equal("test-key", options.Encryption?.KeyId);
        Assert.True(options.Encryption?.RequireEncryptedStore);
        Assert.Equal(JsonColdStoreStartupMode.FullHydration, options.StartupMode);
        Assert.False(options.FsyncOnWrite);
        Assert.Equal(32, options.AsyncFlush.QueueCapacity);
        Assert.Equal(5, options.FlushRetry.MaxRetries);
        Assert.Equal(TimeSpan.FromMilliseconds(25), options.FlushRetry.BaseDelay);
        Assert.Equal(7, options.TransactionReplay.MaxRetries);
        Assert.Equal(4, options.ReadRetry.MaxRetries);
        Assert.Equal(TimeSpan.FromMilliseconds(10), options.ReadRetry.BaseDelay);
        Assert.False(options.Integrity.VerifyOnStartup);
        Assert.True(options.Integrity.VerifyOnRead);
        Assert.Equal(TimeSpan.FromDays(9), options.Quarantine.Retention);
        Assert.Equal(TimeSpan.Zero, options.IndexMaintenance.RescanInterval);
        Assert.True(options.EventLog.Enabled);
        Assert.True(options.Snapshots.Enabled);
        Assert.Equal(JsonColdStoreScanPolicy.AllowExplicitScans, options.FullScanPolicy);
    }

    [Fact]
    public void UseAsyncFlushRejectsInvalidCapacity()
    {
        var builder = new JsonColdStoreOptionsBuilder(TestDirectory("store"));

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.UseAsyncFlush(0));
    }

    [Fact]
    public void RetryPoliciesRejectNegativeValues()
    {
        var builder = new JsonColdStoreOptionsBuilder(TestDirectory("store"));

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.UseFlushRetry(-1, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.UseFlushRetry(1, TimeSpan.FromMilliseconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.UseTransactionReplay(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.UseReadRetry(-1, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.UseReadRetry(1, TimeSpan.FromMilliseconds(-1)));
    }

    private static string TestDirectory(string name) =>
        Path.Combine(Path.GetTempPath(), "jsoncoldstore-tests", name);
}
