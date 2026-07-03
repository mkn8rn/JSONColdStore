using System.Text;
using System.Text.Json;
using JSONColdStore;
using JSONColdStore.Storage;

namespace JSONColdStore.Tests;

public sealed class JsonColdStoreIndexStoreTests
{
    [Fact]
    public async Task ReadRecordIdsAsyncReturnsEmptyWhenIndexDocumentIsMissing()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreIndexStore(options, protectDocuments: false);

        var recordIds = await store.ReadRecordIdsAsync("Entity", "Value", "missing");
        var allRecordIds = await store.ReadAllRecordIdsAsync("Entity", "Value");
        var buckets = await store.ReadBucketsAsync("Entity", "Value");

        Assert.Empty(recordIds);
        Assert.Empty(allRecordIds);
        Assert.Empty(buckets);
        Assert.False(File.Exists(IndexPath(root, "Entity", "Value")));
    }

    [Fact]
    public async Task ReadRecordIdsAsyncRejectsReparsePointIndexDocumentDirectory()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        var outsideFile = Path.Combine(outside, "outside-index.txt");
        await File.WriteAllTextAsync(outsideFile, "outside index directory payload");
        var indexPath = IndexPath(root, "Entity", "Value");
        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
            indexPath,
            outside,
            nameof(ReadRecordIdsAsyncRejectsReparsePointIndexDocumentDirectory));
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreIndexStore(options, protectDocuments: false);

        var exception = await Assert.ThrowsAsync<JsonColdStoreUnsafePathException>(
            () => store.ReadRecordIdsAsync("Entity", "Value", "consumer"));

        Assert.Contains("index document", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(root, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(indexPath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outside, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outside index directory payload", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("outside index directory payload", await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public async Task ReadRecordIdsAsyncRejectsCorruptIndexDocument()
    {
        var root = NewTempDirectory();
        var indexPath = IndexPath(root, "Entity", "Value");
        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
        await File.WriteAllTextAsync(indexPath, "not valid json");
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var store = new JsonColdStoreIndexStore(options, protectDocuments: false);

        await Assert.ThrowsAsync<JsonException>(
            () => store.ReadRecordIdsAsync("Entity", "Value", "consumer"));
    }

    [Fact]
    public async Task ProtectedReadAcceptsPlaintextDocumentAndNextWriteProtectsIt()
    {
        var root = NewTempDirectory();
        using var key = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseEncryptionKey(key)
            .UseFsyncOnWrite(false)
            .Build();
        var plaintextStore = new JsonColdStoreIndexStore(options, protectDocuments: false);
        await plaintextStore.ReplaceAsync(
            "Entity",
            "Value",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["consumer-1"] = ["1"],
            });

        var indexPath = IndexPath(root, "Entity", "Value");
        Assert.Contains("consumer-1", await File.ReadAllTextAsync(indexPath));

        var protectedStore = new JsonColdStoreIndexStore(options, protectDocuments: true);
        var recordIds = await protectedStore.ReadRecordIdsAsync("Entity", "Value", "consumer-1");
        await protectedStore.UpsertAsync("Entity", "Value", "consumer-2", "2");
        var protectedBytes = await File.ReadAllBytesAsync(indexPath);
        var rewrittenRecordIds = await protectedStore.ReadRecordIdsAsync("Entity", "Value", "consumer-2");

        Assert.Equal(["1"], recordIds);
        Assert.True(JsonColdStorePayloadCodec.IsEnvelope(protectedBytes));
        Assert.DoesNotContain("consumer-1", Encoding.UTF8.GetString(protectedBytes));
        Assert.Equal(["2"], rewrittenRecordIds);
    }

    private static string IndexPath(string root, string entityName, string indexName) =>
        JsonColdStorePathValidator.GetSafeChildPath(
            root,
            "entities",
            JsonColdStoreNameEncoder.EncodePathSegment(entityName),
            "indexes",
            JsonColdStoreNameEncoder.EncodePathSegment(indexName) + ".json");

    private static string NewTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "jsoncoldstore-index-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
