using System.Text.Json;
using System.Text.Json.Serialization;
using EFC.JSONColdStore;
using EFC.JSONColdStore.Storage;

namespace EFC.JSONColdStore.Tests;

public sealed class JsonColdStoreCatalogTests
{
    private static readonly JsonSerializerOptions StoreJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task EnsureInitializedAsyncCreatesRootMetadata()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseCompression(JsonColdStoreCompression.Brotli)
            .UseStartupMode(JsonColdStoreStartupMode.MetadataOnly)
            .UseFsyncOnWrite(false)
            .Build();
        var catalog = new JsonColdStoreCatalog(options);

        var metadata = await catalog.EnsureInitializedAsync();

        Assert.NotEqual(Guid.Empty, metadata.StoreId);
        Assert.Equal(JsonColdStoreCatalog.CurrentFormatVersion, metadata.FormatVersion);
        Assert.Equal(JsonColdStoreCompression.Brotli, metadata.Policy.Compression);
        Assert.False(metadata.Policy.EncryptionEnabled);
        Assert.True(File.Exists(Path.Combine(root, JsonColdStoreCatalog.StoreFileName)));
    }

    [Fact]
    public async Task EnsureInitializedAsyncReopensExistingMetadataWithoutChangingStoreId()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var firstCatalog = new JsonColdStoreCatalog(options);
        var first = await firstCatalog.EnsureInitializedAsync();

        var secondCatalog = new JsonColdStoreCatalog(options);
        var second = await secondCatalog.EnsureInitializedAsync();

        Assert.Equal(first.StoreId, second.StoreId);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
    }

    [Fact]
    public async Task LoadAndValidateAsyncRequiresKeyForEncryptedStore()
    {
        var root = NewTempDirectory();
        using var key = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);
        var encryptedMetadata = JsonColdStoreStoreMetadata.CreateNew(
            new JsonColdStoreOptionsBuilder(root)
                .UseEncryptionKey(key)
                .Build(),
            providerVersion: "test");
        await WriteMetadataAsync(root, encryptedMetadata);
        var plaintextOptions = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var catalog = new JsonColdStoreCatalog(plaintextOptions);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => catalog.LoadAndValidateAsync());

        Assert.Contains("requires a configured encryption key", exception.Message);
    }

    [Fact]
    public async Task LoadAndValidateAsyncRejectsPlaintextStoreWhenEncryptionIsRequired()
    {
        var root = NewTempDirectory();
        var plaintextMetadata = JsonColdStoreStoreMetadata.CreateNew(
            new JsonColdStoreOptionsBuilder(root).Build(),
            providerVersion: "test");
        await WriteMetadataAsync(root, plaintextMetadata);
        using var key = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);
        var encryptedOptions = new JsonColdStoreOptionsBuilder(root)
            .UseEncryption(new JsonColdStoreEncryptionOptions
            {
                Key = key,
                RequireEncryptedStore = true,
            })
            .UseFsyncOnWrite(false)
            .Build();
        var catalog = new JsonColdStoreCatalog(encryptedOptions);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => catalog.LoadAndValidateAsync());

        Assert.Contains("requires an encrypted", exception.Message);
    }

    [Fact]
    public async Task LoadAndValidateAsyncRejectsUnsupportedFormatVersion()
    {
        var root = NewTempDirectory();
        var metadata = JsonColdStoreStoreMetadata.CreateNew(
            new JsonColdStoreOptionsBuilder(root).Build(),
            providerVersion: "test") with
        {
            FormatVersion = JsonColdStoreCatalog.CurrentFormatVersion + 100,
        };
        await WriteMetadataAsync(root, metadata);
        var catalog = new JsonColdStoreCatalog(
            new JsonColdStoreOptionsBuilder(root).UseFsyncOnWrite(false).Build());

        await Assert.ThrowsAsync<NotSupportedException>(() => catalog.LoadAndValidateAsync());
    }

    [Fact]
    public async Task MetadataDoesNotPersistEncryptionKeyDetails()
    {
        var root = NewTempDirectory();
        using var key = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseEncryption(new JsonColdStoreEncryptionOptions
            {
                Key = key,
                KeyId = "do-not-persist",
                RequireEncryptedStore = true,
            })
            .UseFsyncOnWrite(false)
            .Build();
        var catalog = new JsonColdStoreCatalog(options);

        await catalog.EnsureInitializedAsync();

        var metadataText = await File.ReadAllTextAsync(Path.Combine(root, JsonColdStoreCatalog.StoreFileName));
        Assert.DoesNotContain("do-not-persist", metadataText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key", metadataText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("encryptionEnabled", metadataText, StringComparison.Ordinal);
    }

    private static async Task WriteMetadataAsync(string root, JsonColdStoreStoreMetadata metadata)
    {
        Directory.CreateDirectory(root);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata, StoreJsonOptions);
        await File.WriteAllBytesAsync(Path.Combine(root, JsonColdStoreCatalog.StoreFileName), bytes);
    }

    private static string NewTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "jsoncoldstore-catalog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
