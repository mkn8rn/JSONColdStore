using System.Text.Json;
using EFC.JSONColdStore;
using EFC.JSONColdStore.Storage;

namespace EFC.JSONColdStore.Tests;

public sealed class JsonColdStoreDatabaseSessionTests
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    [Fact]
    public async Task OpenAsyncCreatesMetadataAndExposesRecordStore()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseFsyncOnWrite(false)
            .Build();

        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(options);
        await session.Records.WriteRecordAsync("Entity", "1", "payload"u8.ToArray());

        Assert.NotEqual(Guid.Empty, session.Metadata.StoreId);
        Assert.Equal(JsonColdStoreCatalog.CurrentFormatVersion, session.Metadata.FormatVersion);
        Assert.True(session.Records.RecordExists("Entity", "1"));
        Assert.True(File.Exists(Path.Combine(root, JsonColdStoreCatalog.StoreFileName)));
    }

    [Fact]
    public async Task OpenAsyncRunsManifestRecoveryBeforeReturning()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseFsyncOnWrite(false)
            .Build();
        var targetSegments = JsonColdStoreRecordStore.GetRecordPathSegments("Entity", "1");
        await JsonColdStoreAtomicFileWriter.WriteAsync(root, targetSegments, "payload"u8.ToArray(), fsync: false);
        var manifest = JsonColdStoreWriteManifest.Create(targetSegments, payloadLength: 7);
        await WriteManifestAsync(root, manifest);

        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(options);

        Assert.Equal(1, session.RecoveryResult.CompletedManifests);
        Assert.Equal(0, session.RecoveryResult.FailedManifests);
        Assert.False(File.Exists(PendingManifestPath(root, manifest.ManifestId)));
    }

    [Fact]
    public async Task OpenAsyncRejectsConcurrentWriters()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();

        await using var firstSession = await JsonColdStoreDatabaseSession.OpenAsync(options);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => JsonColdStoreDatabaseSession.OpenAsync(options));
    }

    [Fact]
    public async Task OpenAsyncReleasesWriterLockWhenStartupValidationFails()
    {
        var root = NewTempDirectory();
        using var key = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);
        var encryptedMetadata = JsonColdStoreStoreMetadata.CreateNew(
            new JsonColdStoreOptionsBuilder(root)
                .UseEncryptionKey(key)
                .UseFsyncOnWrite(false)
                .Build(),
            providerVersion: "test");
        await WriteStoreMetadataAsync(root, encryptedMetadata);
        var plaintextOptions = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => JsonColdStoreDatabaseSession.OpenAsync(plaintextOptions));

        using var databaseLock = await JsonColdStoreDatabaseLock.AcquireAsync(plaintextOptions);
        Assert.True(File.Exists(Path.Combine(
            root,
            JsonColdStoreDatabaseLock.LockDirectoryName,
            JsonColdStoreDatabaseLock.WriterLockFileName)));
    }

    private static async Task WriteManifestAsync(string root, JsonColdStoreWriteManifest manifest)
    {
        await JsonColdStoreAtomicFileWriter.WriteAsync(
            root,
            JsonColdStoreRecordStore.GetPendingManifestPathSegments(manifest.ManifestId),
            JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJsonOptions),
            fsync: false);
    }

    private static async Task WriteStoreMetadataAsync(string root, JsonColdStoreStoreMetadata metadata)
    {
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(
            Path.Combine(root, JsonColdStoreCatalog.StoreFileName),
            JsonSerializer.SerializeToUtf8Bytes(metadata, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }));
    }

    private static string PendingManifestPath(string root, Guid manifestId) =>
        Path.Combine(root, "_transactions", "pending", manifestId.ToString("N") + ".json");

    private static string NewTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "jsoncoldstore-session-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
