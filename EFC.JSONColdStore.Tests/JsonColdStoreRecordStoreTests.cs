using System.Globalization;
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
        Assert.False(Directory.Exists(Path.Combine(root, "_transactions", "staged"))
            && Directory.GetFiles(Path.Combine(root, "_transactions", "staged"), "*.jcs").Length > 0);
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
    public async Task ReadRecordAsyncRejectsReparsePointRecordFile()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        var recordPath = JsonColdStorePathValidator.GetSafeChildPath(
            root,
            [.. JsonColdStoreRecordStore.GetRecordPathSegments("Entity", "linked-read")]);
        Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
        var outsideFile = Path.Combine(outside, "outside.jcs");
        await File.WriteAllTextAsync(outsideFile, "outside-record");
        JsonColdStoreReparsePointTestHelper.CreateRequiredFileLink(
            recordPath,
            outsideFile,
            nameof(ReadRecordAsyncRejectsReparsePointRecordFile));
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);

        await Assert.ThrowsAsync<JsonColdStoreUnsafePathException>(
            () => store.ReadRecordAsync("Entity", "linked-read"));

        Assert.Equal("outside-record", await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public async Task DeleteRecordAsyncRejectsReparsePointRecordFile()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);
        await store.WriteRecordAsync("Entity", "linked-delete", "payload"u8.ToArray());
        var recordPath = JsonColdStorePathValidator.GetSafeChildPath(
            root,
            [.. JsonColdStoreRecordStore.GetRecordPathSegments("Entity", "linked-delete")]);
        File.Delete(recordPath);
        var outsideFile = Path.Combine(outside, "outside.jcs");
        await File.WriteAllTextAsync(outsideFile, "outside-delete");
        JsonColdStoreReparsePointTestHelper.CreateRequiredFileLink(
            recordPath,
            outsideFile,
            nameof(DeleteRecordAsyncRejectsReparsePointRecordFile));

        await Assert.ThrowsAsync<JsonColdStoreUnsafePathException>(
            () => store.DeleteRecordAsync("Entity", "linked-delete"));

        Assert.Equal("outside-delete", await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public async Task RecordExistsRejectsReparsePointRecordFile()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        var recordPath = JsonColdStorePathValidator.GetSafeChildPath(
            root,
            [.. JsonColdStoreRecordStore.GetRecordPathSegments("Entity", "linked-exists")]);
        Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
        var outsideFile = Path.Combine(outside, "outside.jcs");
        await File.WriteAllTextAsync(outsideFile, "outside-record-exists");
        JsonColdStoreReparsePointTestHelper.CreateRequiredFileLink(
            recordPath,
            outsideFile,
            nameof(RecordExistsRejectsReparsePointRecordFile));
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);

        var exception = Assert.Throws<JsonColdStoreUnsafePathException>(
            () => store.RecordExists("Entity", "linked-exists"));

        Assert.Contains("current record", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(recordPath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outside, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("outside-record-exists", await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public async Task ReadRecordAsyncRetriesTransientReadLock()
    {
        var root = NewTempDirectory();
        var writeOptions = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseFsyncOnWrite(false)
            .Build();
        var writer = new JsonColdStoreRecordStore(writeOptions);
        await writer.WriteRecordAsync("Entity", "locked", "payload"u8.ToArray());
        var recordPath = JsonColdStorePathValidator.GetSafeChildPath(
            root,
            [.. JsonColdStoreRecordStore.GetRecordPathSegments("Entity", "locked")]);

        var noRetryOptions = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseFsyncOnWrite(false)
            .UseReadRetry(maxRetries: 0, baseDelay: TimeSpan.Zero)
            .Build();
        var retryOptions = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseFsyncOnWrite(false)
            .UseReadRetry(maxRetries: 20, baseDelay: TimeSpan.FromMilliseconds(10))
            .Build();
        await using var locked = new FileStream(
            recordPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous);

        var immediateFailure = await Record.ExceptionAsync(
            () => new JsonColdStoreRecordStore(noRetryOptions).ReadRecordAsync("Entity", "locked"));

        Assert.True(
            immediateFailure is IOException or UnauthorizedAccessException,
            "Exclusive file locks should fail immediately when read retries are disabled.");

        var retryingRead = new JsonColdStoreRecordStore(retryOptions).ReadRecordAsync("Entity", "locked");
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.False(retryingRead.IsCompleted);

        await locked.DisposeAsync();

        var read = await retryingRead.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("payload"u8.ToArray(), read);
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
    public async Task WriteRecordAsyncSkipsReparsePointEventLogDirectory()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
                Path.Combine(root, "_events"),
                outside,
                nameof(WriteRecordAsyncSkipsReparsePointEventLogDirectory));

        var options = new JsonColdStoreOptionsBuilder(root)
            .UseEventLog(enabled: true, TimeSpan.FromDays(7))
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);

        await store.WriteRecordAsync("Entity", "event-reparse", "payload"u8.ToArray());

        Assert.True(store.RecordExists("Entity", "event-reparse"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task WriteRecordAsyncSkipsReparsePointEventLogFile()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        var eventsDirectory = Path.Combine(root, "_events");
        Directory.CreateDirectory(eventsDirectory);
        var outsideFile = Path.Combine(outside, "outside.jsonl");
        await File.WriteAllTextAsync(outsideFile, "outside-event");
        JsonColdStoreReparsePointTestHelper.CreateRequiredFileLink(
            Path.Combine(
                eventsDirectory,
                DateTimeOffset.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl"),
            outsideFile,
            nameof(WriteRecordAsyncSkipsReparsePointEventLogFile));
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseEventLog(enabled: true, TimeSpan.FromDays(7))
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);

        await store.WriteRecordAsync("Entity", "event-file-reparse", "payload"u8.ToArray());

        Assert.True(store.RecordExists("Entity", "event-file-reparse"));
        Assert.Equal("outside-event", await File.ReadAllTextAsync(outsideFile));
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
    public async Task ReadAllRecordsAsyncRejectsReparsePointRecordsDirectory()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        var recordsDirectory = CurrentRecordsDirectory(root, "Entity");
        Directory.CreateDirectory(Path.GetDirectoryName(recordsDirectory)!);
        var outsideFile = Path.Combine(outside, "outside.jcs");
        await File.WriteAllTextAsync(outsideFile, "outside-scan-payload");
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
            recordsDirectory,
            outside,
            nameof(ReadAllRecordsAsyncRejectsReparsePointRecordsDirectory));
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);

        var exception = await Assert.ThrowsAsync<JsonColdStoreUnsafePathException>(async () =>
        {
            await foreach (var _ in store.ReadAllRecordsAsync("Entity"))
            {
            }
        });

        Assert.Contains("records directory", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(recordsDirectory, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outside, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("outside-scan-payload", await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public async Task EntityHasRecordsRejectsReparsePointRecordsDirectory()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        var recordsDirectory = CurrentRecordsDirectory(root, "Entity");
        Directory.CreateDirectory(Path.GetDirectoryName(recordsDirectory)!);
        var outsideFile = Path.Combine(outside, "outside.jcs");
        await File.WriteAllTextAsync(outsideFile, "outside-entity-has-records");
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
            recordsDirectory,
            outside,
            nameof(EntityHasRecordsRejectsReparsePointRecordsDirectory));
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);

        var exception = Assert.Throws<JsonColdStoreUnsafePathException>(
            () => store.EntityHasRecords("Entity"));

        Assert.Contains("records directory", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(recordsDirectory, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outside, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("outside-entity-has-records", await File.ReadAllTextAsync(outsideFile));
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
    public async Task ReadRecordAsyncPrunesExpiredQuarantineFiles()
    {
        var root = NewTempDirectory();
        var quarantineDirectory = Path.Combine(root, "_quarantine", "records");
        Directory.CreateDirectory(quarantineDirectory);
        var expiredFile = Path.Combine(quarantineDirectory, "expired.jcs");
        await File.WriteAllTextAsync(expiredFile, "old quarantine");
        File.SetLastWriteTimeUtc(expiredFile, DateTime.UtcNow.Subtract(TimeSpan.FromDays(2)));
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseChecksums(verifyOnStartup: true, verifyOnRead: true)
            .UseQuarantine(TimeSpan.FromDays(1))
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

        Assert.False(File.Exists(expiredFile));
        Assert.Single(Directory.GetFiles(quarantineDirectory, "*.jcs"));
    }

    [Fact]
    public async Task ReadRecordAsyncRejectsReparsePointQuarantineDirectory()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseChecksums(verifyOnStartup: true, verifyOnRead: true)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);
        await store.WriteRecordAsync("Entity", "quarantine-reparse", "payload"u8.ToArray());
        var recordPath = JsonColdStorePathValidator.GetSafeChildPath(
            root,
            [.. JsonColdStoreRecordStore.GetRecordPathSegments("Entity", "quarantine-reparse")]);
        var bytes = await File.ReadAllBytesAsync(recordPath);
        bytes[^1] ^= 0x7F;
        await File.WriteAllBytesAsync(recordPath, bytes);
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
                Path.Combine(root, "_quarantine"),
                outside,
                nameof(ReadRecordAsyncRejectsReparsePointQuarantineDirectory));

        await Assert.ThrowsAsync<JsonColdStoreUnsafePathException>(
            () => store.ReadRecordAsync("Entity", "quarantine-reparse"));

        Assert.True(store.RecordExists("Entity", "quarantine-reparse"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task ReadRecordAsyncKeepsFreshQuarantineWhenCorruptSourceTimestampIsExpired()
    {
        var root = NewTempDirectory();
        var quarantineDirectory = Path.Combine(root, "_quarantine", "records");
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseChecksums(verifyOnStartup: true, verifyOnRead: true)
            .UseQuarantine(TimeSpan.FromDays(1))
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);
        await store.WriteRecordAsync("Entity", "old-corrupt", """{"id":1}"""u8.ToArray());
        var recordPath = JsonColdStorePathValidator.GetSafeChildPath(
            root,
            [.. JsonColdStoreRecordStore.GetRecordPathSegments("Entity", "old-corrupt")]);
        var bytes = await File.ReadAllBytesAsync(recordPath);
        bytes[^1] ^= 0x7F;
        await File.WriteAllBytesAsync(recordPath, bytes);
        File.SetLastWriteTimeUtc(recordPath, DateTime.UtcNow.Subtract(TimeSpan.FromDays(10)));

        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadRecordAsync("Entity", "old-corrupt"));

        var quarantineFile = Assert.Single(Directory.GetFiles(quarantineDirectory, "*.jcs"));
        Assert.True(File.GetLastWriteTimeUtc(quarantineFile) > DateTime.UtcNow.Subtract(TimeSpan.FromDays(1)));
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
    public async Task VerifyAllRecordsAsyncQuarantinesMalformedCurrentRecordName()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);
        var malformedPath = await WriteMalformedCurrentRecordAsync(
            root,
            options,
            "Entity",
            "plain-record-id.jcs");

        await Assert.ThrowsAsync<InvalidDataException>(() => store.VerifyAllRecordsAsync());

        Assert.False(File.Exists(malformedPath));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "_quarantine", "records"), "*.jcs"));
    }

    [Fact]
    public async Task RepairAllRecordsAsyncQuarantinesMalformedCurrentRecordName()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);
        var malformedPath = await WriteMalformedCurrentRecordAsync(
            root,
            options,
            "Entity",
            "plain-record-id.jcs");

        var result = await store.RepairAllRecordsAsync();

        Assert.Equal(0, result.VerifiedRecords);
        Assert.Equal(1, result.QuarantinedRecords);
        Assert.False(File.Exists(malformedPath));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "_quarantine", "records"), "*.jcs"));
    }

    [Fact]
    public async Task RepairAllRecordsAsyncQuarantinesMalformedCurrentEntityDirectory()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);
        var malformedPath = await WriteMalformedCurrentRecordAsync(
            root,
            options,
            "plain-entity",
            JsonColdStoreNameEncoder.EncodePathSegment("1") + ".jcs",
            encodeEntityName: false);

        var result = await store.RepairAllRecordsAsync();

        Assert.Equal(0, result.VerifiedRecords);
        Assert.Equal(1, result.QuarantinedRecords);
        Assert.False(File.Exists(malformedPath));
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
    public async Task RecoverPendingManifestsPublishesStagedWriteWhenTargetIsMissing()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);
        var targetSegments = JsonColdStoreRecordStore.GetRecordPathSegments("Entity", "staged");
        var encodedPayload = JsonColdStorePayloadCodec.Encode("payload"u8, options);
        var manifest = JsonColdStoreWriteManifest.CreateStagedWrite(
            targetSegments,
            encodedPayload.Length);
        await JsonColdStoreAtomicFileWriter.WriteAsync(
            root,
            manifest.StagedPathSegments!,
            encodedPayload,
            fsync: false);
        await WriteManifestAsync(root, manifest);

        var result = await store.RecoverPendingManifestsAsync();
        var read = await store.ReadRecordAsync("Entity", "staged");

        Assert.Equal(1, result.CompletedManifests);
        Assert.Equal(0, result.FailedManifests);
        Assert.Equal("payload"u8.ToArray(), read);
        Assert.False(File.Exists(ManifestPath(root, manifest.ManifestId)));
        Assert.False(File.Exists(StagedPath(root, manifest.ManifestId)));
    }

    [Fact]
    public async Task RecoverPendingManifestsRejectsReparsePointTargetDirectoryWithoutRetrying()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseFsyncOnWrite(false)
            .UseTransactionReplay(maxRetries: 5)
            .Build();
        var store = new JsonColdStoreRecordStore(options);
        var targetSegments = JsonColdStoreRecordStore.GetRecordPathSegments("Entity", "staged");
        var encodedPayload = JsonColdStorePayloadCodec.Encode("payload"u8, options);
        var manifest = JsonColdStoreWriteManifest.CreateStagedWrite(
            targetSegments,
            encodedPayload.Length);
        await JsonColdStoreAtomicFileWriter.WriteAsync(
            root,
            manifest.StagedPathSegments!,
            encodedPayload,
            fsync: false);
        await WriteManifestAsync(root, manifest);
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
                Path.Combine(root, "entities"),
                outside,
                nameof(RecoverPendingManifestsRejectsReparsePointTargetDirectoryWithoutRetrying));

        await Assert.ThrowsAsync<JsonColdStoreUnsafePathException>(
            () => store.RecoverPendingManifestsAsync());

        Assert.True(File.Exists(ManifestPath(root, manifest.ManifestId)));
        Assert.True(File.Exists(StagedPath(root, manifest.ManifestId)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task RecoverPendingManifestsDeletesOrphanedStagedWriteWithoutManifest()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreRecordStore(options);
        var orphanedManifestId = Guid.NewGuid();
        await JsonColdStoreAtomicFileWriter.WriteAsync(
            root,
            JsonColdStoreRecordStore.GetStagedWritePathSegments(orphanedManifestId),
            JsonColdStorePayloadCodec.Encode("orphaned"u8, options),
            fsync: false);

        var result = await store.RecoverPendingManifestsAsync();

        Assert.Equal(0, result.CompletedManifests);
        Assert.Equal(0, result.FailedManifests);
        Assert.Equal(1, result.DeletedOrphanedStagedWrites);
        Assert.False(File.Exists(StagedPath(root, orphanedManifestId)));
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
    public async Task RecoverPendingManifestsRejectsReparsePointFailedDirectory()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseFsyncOnWrite(false)
            .UseTransactionReplay(maxRetries: 5)
            .Build();
        var store = new JsonColdStoreRecordStore(options);
        var manifest = JsonColdStoreWriteManifest.CreateWrite(
            JsonColdStoreRecordStore.GetRecordPathSegments("Missing", "1"),
            payloadLength: 7);
        await WriteManifestAsync(root, manifest);
        Directory.CreateDirectory(Path.Combine(root, "_transactions"));
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
                Path.Combine(root, "_transactions", "failed"),
                outside,
                nameof(RecoverPendingManifestsRejectsReparsePointFailedDirectory));

        await Assert.ThrowsAsync<JsonColdStoreUnsafePathException>(
            () => store.RecoverPendingManifestsAsync());

        Assert.True(File.Exists(ManifestPath(root, manifest.ManifestId)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task RecoverPendingManifestsRejectsReparsePointFailedManifestFile()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.None)
            .UseFsyncOnWrite(false)
            .UseTransactionReplay(maxRetries: 5)
            .Build();
        var store = new JsonColdStoreRecordStore(options);
        var manifest = JsonColdStoreWriteManifest.CreateWrite(
            JsonColdStoreRecordStore.GetRecordPathSegments("Missing", "linked-failed"),
            payloadLength: 7);
        await WriteManifestAsync(root, manifest);
        var failedDirectory = Path.Combine(root, "_transactions", "failed");
        Directory.CreateDirectory(failedDirectory);
        var outsideFile = Path.Combine(outside, manifest.ManifestId.ToString("N") + ".json");
        await File.WriteAllTextAsync(outsideFile, "outside-failed");
        JsonColdStoreReparsePointTestHelper.CreateRequiredFileLink(
            Path.Combine(failedDirectory, manifest.ManifestId.ToString("N") + ".json"),
            outsideFile,
            nameof(RecoverPendingManifestsRejectsReparsePointFailedManifestFile));

        await Assert.ThrowsAsync<JsonColdStoreUnsafePathException>(
            () => store.RecoverPendingManifestsAsync());

        Assert.True(File.Exists(ManifestPath(root, manifest.ManifestId)));
        Assert.Equal("outside-failed", await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public void IsTransientReplayExceptionDoesNotRetryUnsafePathFailures()
    {
        Assert.True(JsonColdStoreRecordStore.IsTransientReplayException(
            new IOException("retry")));
        Assert.True(JsonColdStoreRecordStore.IsTransientReplayException(
            new UnauthorizedAccessException("retry")));
        Assert.False(JsonColdStoreRecordStore.IsTransientReplayException(
            new JsonColdStoreUnsafePathException("fail closed")));
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

    [Fact]
    public void NameEncoderRoundTripsEncodedPathSegment()
    {
        var value = "spaces punctuation ..\\unsafe/name 123";
        var encoded = JsonColdStoreNameEncoder.EncodePathSegment(value);

        var decoded = JsonColdStoreNameEncoder.DecodePathSegment(encoded);

        Assert.Equal(value, decoded);
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

    private static async Task<string> WriteMalformedCurrentRecordAsync(
        string root,
        JsonColdStoreOptions options,
        string entityName,
        string fileName,
        bool encodeEntityName = true)
    {
        var recordsDirectory = Path.Combine(
            root,
            "entities",
            encodeEntityName ? JsonColdStoreNameEncoder.EncodePathSegment(entityName) : entityName,
            "records");
        Directory.CreateDirectory(recordsDirectory);

        var path = Path.Combine(recordsDirectory, fileName);
        var payload = JsonColdStorePayloadCodec.Encode("payload"u8, options);
        await File.WriteAllBytesAsync(path, payload);
        return path;
    }

    private static string ManifestPath(string root, Guid manifestId) =>
        Path.Combine(root, "_transactions", "pending", manifestId.ToString("N") + ".json");

    private static string StagedPath(string root, Guid manifestId) =>
        Path.Combine(root, "_transactions", "staged", manifestId.ToString("N") + ".jcs");

    private static string CurrentRecordsDirectory(string root, string entityName) =>
        JsonColdStorePathValidator.GetSafeChildPath(
            root,
            "entities",
            JsonColdStoreNameEncoder.EncodePathSegment(entityName),
            "records");

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
