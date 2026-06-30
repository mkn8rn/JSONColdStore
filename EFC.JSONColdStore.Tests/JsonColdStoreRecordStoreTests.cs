using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EFC.JSONColdStore;
using EFC.JSONColdStore.Storage;

namespace EFC.JSONColdStore.Tests;

public sealed class JsonColdStoreRecordStoreTests
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    [Fact]
    public async Task WriteRecordAsyncPublishesEncryptedReadableRecordAndRemovesManifest()
    {
        var root = NewTempDirectory();
        using var key = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.Brotli)
            .UseEncryptionKey(key)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);
        var payload = Encoding.UTF8.GetBytes("""{"id":"1","value":"secret"}""");

        await store.WriteRecordAsync("Consumer.Event", "1", payload);
        var read = await store.ReadRecordAsync("Consumer.Event", "1");

        Assert.Equal(payload, read);
        Assert.True(store.RecordExists("Consumer.Event", "1"));
        Assert.False(Directory.Exists(Path.Combine(root, "_transactions", "pending"))
            && Directory.GetFiles(Path.Combine(root, "_transactions", "pending"), "*.json").Length > 0);
    }

    [Fact]
    public async Task WriteRecordAsyncDoesNotCreateEventLogWhenDisabled()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);

        await store.WriteRecordAsync("Entity", "event-disabled", "payload"u8.ToArray());

        Assert.False(Directory.Exists(Path.Combine(root, "_events")));
    }

    [Fact]
    public async Task WriteRecordAsyncAppendsPlainEventLogWithoutRawRecordId()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseEventLog(enabled: true, TimeSpan.FromDays(7))
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);

        await store.WriteRecordAsync("Entity", "raw-record-id", "payload"u8.ToArray());

        var logText = await ReadOnlyEventLogTextAsync(root);
        Assert.Contains("record.write", logText);
        Assert.Contains("Entity", logText);
        Assert.Contains("recordIdHash", logText);
        Assert.DoesNotContain("raw-record-id", logText);
    }

    [Fact]
    public async Task WriteRecordAsyncProtectsEncryptedEventLogDetails()
    {
        var root = NewTempDirectory();
        using var key = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseEncryptionKey(key)
            .UseEventLog(enabled: true, TimeSpan.FromDays(7))
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options, protectManifests: true);

        await store.WriteRecordAsync("SecretEntity", "secret-record-id", "payload"u8.ToArray());

        var logText = await ReadOnlyEventLogTextAsync(root);
        Assert.Contains("\"protected\":true", logText);
        Assert.DoesNotContain("record.write", logText);
        Assert.DoesNotContain("SecretEntity", logText);
        Assert.DoesNotContain("secret-record-id", logText);
    }

    [Fact]
    public async Task WriteRecordAsyncPrunesExpiredEventLogs()
    {
        var root = NewTempDirectory();
        var eventsDirectory = Path.Combine(root, "_events");
        Directory.CreateDirectory(eventsDirectory);
        var expiredLog = Path.Combine(eventsDirectory, "20000101.jsonl");
        await File.WriteAllTextAsync(expiredLog, "expired");
        File.SetLastWriteTimeUtc(expiredLog, DateTime.UtcNow.Subtract(TimeSpan.FromDays(2)));
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseEventLog(enabled: true, TimeSpan.FromDays(1))
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);

        await store.WriteRecordAsync("Entity", "event-retention", "payload"u8.ToArray());

        Assert.False(File.Exists(expiredLog));
        Assert.NotEmpty(Directory.GetFiles(eventsDirectory, "*.jsonl"));
    }

    [Fact]
    public async Task EncodedNamesKeepUnsafeEntityAndRecordNamesInsideDatabaseDirectory()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);

        await store.WriteRecordAsync("..\\Unsafe/Entity", "../record", "payload"u8.ToArray());

        Assert.True(store.RecordExists("..\\Unsafe/Entity", "../record"));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "record.jcs")));
    }

    [Fact]
    public async Task ReadAllRecordsAsyncDecodesRecordsInStableFileOrder()
    {
        var root = NewTempDirectory();
        using var key = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.Brotli)
            .UseEncryptionKey(key)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);
        await store.WriteRecordAsync("Entity", "2", """{"id":2}"""u8.ToArray());
        await store.WriteRecordAsync("Entity", "1", """{"id":1}"""u8.ToArray());
        var records = new List<string>();

        await foreach (var record in store.ReadAllRecordsAsync("Entity"))
            records.Add(Encoding.UTF8.GetString(record));

        Assert.Equal(["{\"id\":1}", "{\"id\":2}"], records);
    }

    [Fact]
    public async Task ReadAllRecordsAsyncReturnsEmptyForMissingEntityDirectory()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);
        var records = new List<byte[]>();

        await foreach (var record in store.ReadAllRecordsAsync("Missing"))
            records.Add(record);

        Assert.Empty(records);
    }

    [Fact]
    public async Task ReadRecordAsyncQuarantinesChecksumCorruptRecord()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseChecksums(verifyOnStartup: true, verifyOnRead: true)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);
        await store.WriteRecordAsync("Entity", "1", """{"id":1}"""u8.ToArray());
        var recordPath = JsonColdStorePathValidator.GetSafeChildPath(
            root,
            [.. JsonColdStoreRecordStore.GetRecordPathSegments("Entity", "1")]);
        var bytes = await File.ReadAllBytesAsync(recordPath);
        bytes[^1] ^= 0x7F;
        await File.WriteAllBytesAsync(recordPath, bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadRecordAsync("Entity", "1"));

        Assert.False(store.RecordExists("Entity", "1"));
        var quarantineDirectory = Path.Combine(root, "_quarantine", "records");
        Assert.True(Directory.Exists(quarantineDirectory));
        Assert.Single(Directory.GetFiles(quarantineDirectory, "*.jcs"));
    }

    [Fact]
    public async Task ReadAllRecordsAsyncQuarantinesChecksumCorruptRecord()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseChecksums(verifyOnStartup: true, verifyOnRead: true)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);
        await store.WriteRecordAsync("Entity", "1", """{"id":1}"""u8.ToArray());
        var recordPath = JsonColdStorePathValidator.GetSafeChildPath(
            root,
            [.. JsonColdStoreRecordStore.GetRecordPathSegments("Entity", "1")]);
        var bytes = await File.ReadAllBytesAsync(recordPath);
        bytes[^1] ^= 0x7F;
        await File.WriteAllBytesAsync(recordPath, bytes);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in store.ReadAllRecordsAsync("Entity"))
            {
            }
        });

        Assert.False(store.RecordExists("Entity", "1"));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "_quarantine", "records"), "*.jcs"));
    }

    [Fact]
    public async Task RecoverPendingManifestsDeletesManifestWhenTargetExists()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);
        var targetSegments = JsonColdStoreRecordStore.GetRecordPathSegments("Entity", "1");
        await JsonColdStoreAtomicFileWriter.WriteAsync(root, targetSegments, "payload"u8.ToArray(), fsync: false);
        var manifest = JsonColdStoreWriteManifest.CreateWrite(targetSegments, payloadLength: 7);
        await WriteManifestAsync(root, manifest);

        var result = await store.RecoverPendingManifestsAsync();

        Assert.Equal(1, result.CompletedManifests);
        Assert.Equal(0, result.FailedManifests);
        Assert.False(File.Exists(ManifestPath(root, manifest.ManifestId)));
    }

    [Fact]
    public async Task RecoverPendingManifestsReadsProtectedManifest()
    {
        var root = NewTempDirectory();
        using var key = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseEncryptionKey(key)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options, protectManifests: true);
        var targetSegments = JsonColdStoreRecordStore.GetRecordPathSegments("Entity", "protected-record");
        await JsonColdStoreAtomicFileWriter.WriteAsync(
            root,
            targetSegments,
            JsonColdStorePayloadCodec.Encode("payload"u8, options),
            fsync: false);
        var manifest = JsonColdStoreWriteManifest.CreateWrite(targetSegments, payloadLength: 7);
        await WriteManifestAsync(root, manifest, options, protect: true);

        var manifestBytes = await File.ReadAllBytesAsync(ManifestPath(root, manifest.ManifestId));
        Assert.False(ContainsBytes(manifestBytes, "Entity"));
        Assert.False(ContainsBytes(manifestBytes, "records"));
        var result = await store.RecoverPendingManifestsAsync();

        Assert.Equal(1, result.CompletedManifests);
        Assert.Equal(0, result.FailedManifests);
        Assert.False(File.Exists(ManifestPath(root, manifest.ManifestId)));
    }

    [Fact]
    public async Task RecoverPendingManifestsRejectsProtectedManifestWithWrongKey()
    {
        var root = NewTempDirectory();
        using var correctKey = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);
        using var wrongKey = JsonColdStoreEncryptionKey.FromBytes(Enumerable.Repeat((byte)7, 32).ToArray());
        var writeOptions = new JsonColdStoreOptionsBuilder(root)
            .UseEncryptionKey(correctKey)
            .UseFsyncOnWrite(false)
            .Build();
        var readOptions = new JsonColdStoreOptionsBuilder(root)
            .UseEncryptionKey(wrongKey)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(readOptions, protectManifests: true);
        var manifest = JsonColdStoreWriteManifest.CreateWrite(
            JsonColdStoreRecordStore.GetRecordPathSegments("Entity", "wrong-key"),
            payloadLength: 7);
        await WriteManifestAsync(root, manifest, writeOptions, protect: true);

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => store.RecoverPendingManifestsAsync());

        Assert.True(File.Exists(ManifestPath(root, manifest.ManifestId)));
        Assert.False(File.Exists(Path.Combine(
            root,
            "_transactions",
            "failed",
            manifest.ManifestId.ToString("N") + ".json")));
    }

    [Fact]
    public async Task RecoverPendingManifestsMovesIncompleteManifestToFailed()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);
        var manifest = JsonColdStoreWriteManifest.CreateWrite(
            JsonColdStoreRecordStore.GetRecordPathSegments("Missing", "1"),
            payloadLength: 7);
        await WriteManifestAsync(root, manifest);

        var result = await store.RecoverPendingManifestsAsync();

        Assert.Equal(0, result.CompletedManifests);
        Assert.Equal(1, result.FailedManifests);
        Assert.False(File.Exists(ManifestPath(root, manifest.ManifestId)));
        Assert.True(File.Exists(Path.Combine(root, "_transactions", "failed", manifest.ManifestId.ToString("N") + ".json")));
    }

    [Fact]
    public async Task DeleteRecordAsyncRemovesRecordAndPendingManifest()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);
        await store.WriteRecordAsync("Entity", "1", "payload"u8.ToArray());

        await store.DeleteRecordAsync("Entity", "1");

        Assert.False(store.RecordExists("Entity", "1"));
        Assert.False(Directory.Exists(Path.Combine(root, "_transactions", "pending"))
            && Directory.GetFiles(Path.Combine(root, "_transactions", "pending"), "*.json").Length > 0);
    }

    [Fact]
    public async Task RecoverPendingManifestsCompletesPendingDelete()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);
        var targetSegments = JsonColdStoreRecordStore.GetRecordPathSegments("Entity", "1");
        await JsonColdStoreAtomicFileWriter.WriteAsync(root, targetSegments, "payload"u8.ToArray(), fsync: false);
        var manifest = JsonColdStoreWriteManifest.CreateDelete(targetSegments);
        await WriteManifestAsync(root, manifest);

        var result = await store.RecoverPendingManifestsAsync();

        Assert.Equal(1, result.CompletedManifests);
        Assert.Equal(0, result.FailedManifests);
        Assert.False(File.Exists(ManifestPath(root, manifest.ManifestId)));
        Assert.False(store.RecordExists("Entity", "1"));
    }

    [Fact]
    public void NameEncoderProducesSingleSafePathSegment()
    {
        var encoded = JsonColdStoreNameEncoder.EncodePathSegment("..\\unsafe/name");

        Assert.DoesNotContain("..", encoded);
        Assert.DoesNotContain("\\", encoded);
        Assert.DoesNotContain("/", encoded);
    }

    private static async Task WriteManifestAsync(
        string root,
        JsonColdStoreWriteManifest manifest,
        JsonColdStoreOptions? options = null,
        bool protect = false)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJsonOptions);
        if (protect)
        {
            ArgumentNullException.ThrowIfNull(options);
            bytes = JsonColdStorePayloadCodec.Encode(bytes, options);
        }

        await JsonColdStoreAtomicFileWriter.WriteAsync(
            root,
            JsonColdStoreRecordStore.GetPendingManifestPathSegments(manifest.ManifestId),
            bytes,
            fsync: false);
    }

    private static string ManifestPath(string root, Guid manifestId) =>
        Path.Combine(root, "_transactions", "pending", manifestId.ToString("N") + ".json");

    private static async Task<string> ReadOnlyEventLogTextAsync(string root)
    {
        var eventFiles = Directory.GetFiles(Path.Combine(root, "_events"), "*.jsonl");
        var eventFile = Assert.Single(eventFiles);
        return await File.ReadAllTextAsync(eventFile);
    }

    private static string NewTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "jsoncoldstore-record-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static bool ContainsBytes(byte[] haystack, string needle) =>
        haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0;
}
