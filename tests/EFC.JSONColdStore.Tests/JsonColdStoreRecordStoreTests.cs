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
        var manifest = JsonColdStoreWriteManifest.Create(targetSegments, payloadLength: 7);
        await WriteManifestAsync(root, manifest);

        var result = await store.RecoverPendingManifestsAsync();

        Assert.Equal(1, result.CompletedManifests);
        Assert.Equal(0, result.FailedManifests);
        Assert.False(File.Exists(ManifestPath(root, manifest.ManifestId)));
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
        var manifest = JsonColdStoreWriteManifest.Create(
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
    public void NameEncoderProducesSingleSafePathSegment()
    {
        var encoded = JsonColdStoreNameEncoder.EncodePathSegment("..\\unsafe/name");

        Assert.DoesNotContain("..", encoded);
        Assert.DoesNotContain("\\", encoded);
        Assert.DoesNotContain("/", encoded);
    }

    private static async Task WriteManifestAsync(string root, JsonColdStoreWriteManifest manifest)
    {
        await JsonColdStoreAtomicFileWriter.WriteAsync(
            root,
            JsonColdStoreRecordStore.GetPendingManifestPathSegments(manifest.ManifestId),
            JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJsonOptions),
            fsync: false);
    }

    private static string ManifestPath(string root, Guid manifestId) =>
        Path.Combine(root, "_transactions", "pending", manifestId.ToString("N") + ".json");

    private static string NewTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "jsoncoldstore-record-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
