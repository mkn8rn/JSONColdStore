using System.Text;
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
        var manifest = JsonColdStoreWriteManifest.CreateWrite(targetSegments, payloadLength: 7);
        await WriteManifestAsync(root, manifest);

        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(options);

        Assert.Equal(1, session.RecoveryResult.CompletedManifests);
        Assert.Equal(0, session.RecoveryResult.FailedManifests);
        Assert.False(File.Exists(PendingManifestPath(root, manifest.ManifestId)));
    }

    [Fact]
    public async Task OpenAsyncWithoutWriterLockDoesNotRecoverPendingManifests()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseFsyncOnWrite(false)
            .Build();
        var targetSegments = JsonColdStoreRecordStore.GetRecordPathSegments("Entity", "1");
        await JsonColdStoreAtomicFileWriter.WriteAsync(root, targetSegments, "payload"u8.ToArray(), fsync: false);
        var manifest = JsonColdStoreWriteManifest.CreateDelete(targetSegments);
        await WriteManifestAsync(root, manifest);

        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(
            options,
            acquireWriterLock: false);

        Assert.Equal(0, session.RecoveryResult.CompletedManifests);
        Assert.Equal(0, session.RecoveryResult.FailedManifests);
        Assert.True(File.Exists(PendingManifestPath(root, manifest.ManifestId)));
        Assert.True(session.Records.RecordExists("Entity", "1"));
    }

    [Fact]
    public async Task OpenAsyncDeletesOrphanedAtomicTempFilesAfterWriterLock()
    {
        var root = NewTempDirectory();
        var outsideTemp = Path.Combine(Path.GetDirectoryName(root)!, "outside.jcs.tmp-orphan");
        var rootTemp = Path.Combine(root, "_store.json.tmp-orphan");
        var entityDirectory = Path.Combine(root, "entities", "Entity", "records");
        var nestedTemp = Path.Combine(entityDirectory, "1.jcs.tmp-orphan");
        var snapshotDirectory = Path.Combine(root, "_snapshots", "snapshot-1");
        var snapshotTemp = Path.Combine(snapshotDirectory, "kept.jcs.tmp-orphan");
        Directory.CreateDirectory(entityDirectory);
        Directory.CreateDirectory(snapshotDirectory);
        await File.WriteAllTextAsync(rootTemp, "root temp");
        await File.WriteAllTextAsync(nestedTemp, "nested temp");
        await File.WriteAllTextAsync(snapshotTemp, "snapshot temp");
        await File.WriteAllTextAsync(outsideTemp, "outside temp");
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();

        try
        {
            await using var session = await JsonColdStoreDatabaseSession.OpenAsync(options);

            Assert.Equal(2, session.RecoveryResult.DeletedTemporaryFiles);
            Assert.False(File.Exists(rootTemp));
            Assert.False(File.Exists(nestedTemp));
            Assert.True(File.Exists(snapshotTemp));
            Assert.True(File.Exists(outsideTemp));
        }
        finally
        {
            if (File.Exists(outsideTemp))
                File.Delete(outsideTemp);
        }
    }

    [Fact]
    public async Task OpenAsyncWithoutWriterLockDoesNotDeleteOrphanedAtomicTempFiles()
    {
        var root = NewTempDirectory();
        var rootTemp = Path.Combine(root, "_store.json.tmp-orphan");
        await File.WriteAllTextAsync(rootTemp, "root temp");
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();

        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(
            options,
            acquireWriterLock: false);

        Assert.Equal(0, session.RecoveryResult.DeletedTemporaryFiles);
        Assert.True(File.Exists(rootTemp));
    }

    [Fact]
    public async Task OpenAsyncWithoutWriterLockRejectsRecordWrites()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();

        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(
            options,
            acquireWriterLock: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.Records.WriteRecordAsync("Entity", "1", "payload"u8.ToArray()));

        Assert.Contains("writer lock", exception.Message);
    }

    [Fact]
    public async Task OpenAsyncWithoutWriterLockDoesNotQuarantineCorruptRead()
    {
        var root = NewTempDirectory();
        var writeOptions = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseChecksums(verifyOnStartup: true, verifyOnRead: true)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(writeOptions);
        await store.WriteRecordAsync("Entity", "1", "payload"u8.ToArray());
        await CorruptRecordAsync(root, "Entity", "1");

        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(
            writeOptions,
            acquireWriterLock: false);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => session.Records.ReadRecordAsync("Entity", "1"));

        Assert.True(session.Records.RecordExists("Entity", "1"));
        Assert.False(Directory.Exists(Path.Combine(root, "_quarantine", "records")));
    }

    [Fact]
    public async Task OpenAsyncMetadataOnlyDoesNotHydrateRecords()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseChecksums(verifyOnStartup: true, verifyOnRead: true)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);
        await store.WriteRecordAsync("Entity", "1", "payload"u8.ToArray());
        await CorruptRecordAsync(root, "Entity", "1");

        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(options);

        Assert.Equal(0, session.StartupValidationResult.VerifiedRecords);
        Assert.True(session.Records.RecordExists("Entity", "1"));
    }

    [Fact]
    public async Task OpenAsyncFullHydrationVerifiesStoredRecords()
    {
        var root = NewTempDirectory();
        var writeOptions = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(writeOptions);
        await store.WriteRecordAsync("Entity", "1", "first"u8.ToArray());
        await store.WriteRecordAsync("Entity", "2", "second"u8.ToArray());
        var readOptions = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseStartupMode(JsonColdStoreStartupMode.FullHydration)
            .UseFsyncOnWrite(false)
            .Build();

        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(readOptions);

        Assert.Equal(2, session.StartupValidationResult.VerifiedRecords);
    }

    [Fact]
    public async Task OpenAsyncFullHydrationQuarantinesChecksumCorruptRecord()
    {
        var root = NewTempDirectory();
        var writeOptions = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(writeOptions);
        await store.WriteRecordAsync("Entity", "1", "payload"u8.ToArray());
        await CorruptRecordAsync(root, "Entity", "1");
        var readOptions = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseStartupMode(JsonColdStoreStartupMode.FullHydration)
            .UseChecksums(verifyOnStartup: true, verifyOnRead: false)
            .UseFsyncOnWrite(false)
            .Build();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => JsonColdStoreDatabaseSession.OpenAsync(readOptions));

        Assert.False(store.RecordExists("Entity", "1"));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "_quarantine", "records"), "*.jcs"));
    }

    [Fact]
    public async Task OpenAsyncFullHydrationWithoutWriterLockDoesNotQuarantineChecksumCorruptRecord()
    {
        var root = NewTempDirectory();
        var writeOptions = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(writeOptions);
        await store.WriteRecordAsync("Entity", "1", "payload"u8.ToArray());
        await CorruptRecordAsync(root, "Entity", "1");
        var readOptions = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseStartupMode(JsonColdStoreStartupMode.FullHydration)
            .UseChecksums(verifyOnStartup: true, verifyOnRead: false)
            .UseFsyncOnWrite(false)
            .Build();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => JsonColdStoreDatabaseSession.OpenAsync(
                readOptions,
                acquireWriterLock: false));

        Assert.True(store.RecordExists("Entity", "1"));
        Assert.False(Directory.Exists(Path.Combine(root, "_quarantine", "records")));
    }

    [Fact]
    public async Task OpenAsyncUsesPlaintextRecordWritesWhenExistingStorePolicyIsPlaintext()
    {
        var root = NewTempDirectory();
        var plainOptions = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseFsyncOnWrite(false)
            .Build();
        await new JsonColdStoreCatalog(plainOptions).EnsureInitializedAsync();
        using var key = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);
        var encryptedOptions = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseEncryptionKey(key)
            .UseFsyncOnWrite(false)
            .Build();

        await using (var session = await JsonColdStoreDatabaseSession.OpenAsync(encryptedOptions))
        {
            Assert.False(session.Metadata.Policy.EncryptionEnabled);
            await session.Records.WriteRecordAsync(
                "Entity",
                "1",
                """{"value":"policy-plain"}"""u8.ToArray());
        }

        var recordPath = JsonColdStorePathValidator.GetSafeChildPath(
            root,
            [.. JsonColdStoreRecordStore.GetRecordPathSegments("Entity", "1")]);
        var recordText = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(recordPath));
        await using var readSession = await JsonColdStoreDatabaseSession.OpenAsync(
            plainOptions,
            acquireWriterLock: false);
        var read = await readSession.Records.ReadRecordAsync("Entity", "1");

        Assert.Contains("policy-plain", recordText);
        Assert.Equal("""{"value":"policy-plain"}"""u8.ToArray(), read);
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

    private static async Task CorruptRecordAsync(string root, string entityName, string recordId)
    {
        var recordPath = JsonColdStorePathValidator.GetSafeChildPath(
            root,
            [.. JsonColdStoreRecordStore.GetRecordPathSegments(entityName, recordId)]);
        var bytes = await File.ReadAllBytesAsync(recordPath);
        bytes[^1] ^= 0x7F;
        await File.WriteAllBytesAsync(recordPath, bytes);
    }

    private static string NewTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "jsoncoldstore-session-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
