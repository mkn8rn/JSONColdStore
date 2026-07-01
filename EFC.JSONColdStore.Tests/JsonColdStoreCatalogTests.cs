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
    public async Task LoadAndValidateAsyncReadsPlaintextEncryptedMetadataWithKey()
    {
        var root = NewTempDirectory();
        using var key = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);
        var encryptedOptions = new JsonColdStoreOptionsBuilder(root)
            .UseEncryptionKey(key)
            .UseFsyncOnWrite(false)
            .Build();
        var encryptedMetadata = JsonColdStoreStoreMetadata.CreateNew(
            encryptedOptions,
            providerVersion: "test");
        await WriteMetadataAsync(root, encryptedMetadata);
        var catalog = new JsonColdStoreCatalog(encryptedOptions);

        var metadata = await catalog.LoadAndValidateAsync();

        Assert.Equal(encryptedMetadata.StoreId, metadata.StoreId);
        Assert.True(metadata.Policy.EncryptionEnabled);
    }

    [Fact]
    public async Task LoadAndValidateAsyncRequiresKeyForProtectedMetadata()
    {
        var root = NewTempDirectory();
        using var key = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);
        var encryptedOptions = new JsonColdStoreOptionsBuilder(root)
            .UseEncryptionKey(key)
            .UseFsyncOnWrite(false)
            .Build();
        await new JsonColdStoreCatalog(encryptedOptions).EnsureInitializedAsync();
        var plaintextOptions = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var catalog = new JsonColdStoreCatalog(plaintextOptions);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => catalog.LoadAndValidateAsync());

        Assert.Contains("encryption key", exception.Message, StringComparison.OrdinalIgnoreCase);
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
    public async Task LoadAndValidateAsyncRejectsEmptyStoreId()
    {
        var root = NewTempDirectory();
        var metadata = JsonColdStoreStoreMetadata.CreateNew(
            new JsonColdStoreOptionsBuilder(root).Build(),
            providerVersion: "test") with
        {
            StoreId = Guid.Empty,
        };
        await WriteMetadataAsync(root, metadata);
        var catalog = new JsonColdStoreCatalog(
            new JsonColdStoreOptionsBuilder(root).UseFsyncOnWrite(false).Build());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => catalog.LoadAndValidateAsync());

        Assert.Contains("store id", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAndValidateAsyncRejectsMissingProviderVersion()
    {
        var root = NewTempDirectory();
        var metadata = JsonColdStoreStoreMetadata.CreateNew(
            new JsonColdStoreOptionsBuilder(root).Build(),
            providerVersion: "test") with
        {
            ProviderVersion = " ",
        };
        await WriteMetadataAsync(root, metadata);
        var catalog = new JsonColdStoreCatalog(
            new JsonColdStoreOptionsBuilder(root).UseFsyncOnWrite(false).Build());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => catalog.LoadAndValidateAsync());

        Assert.Contains("provider version", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAndValidateAsyncRejectsMissingCreationTimestamp()
    {
        var root = NewTempDirectory();
        var metadata = JsonColdStoreStoreMetadata.CreateNew(
            new JsonColdStoreOptionsBuilder(root).Build(),
            providerVersion: "test") with
        {
            CreatedAt = default,
        };
        await WriteMetadataAsync(root, metadata);
        var catalog = new JsonColdStoreCatalog(
            new JsonColdStoreOptionsBuilder(root).UseFsyncOnWrite(false).Build());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => catalog.LoadAndValidateAsync());

        Assert.Contains("creation timestamp", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAndValidateAsyncRejectsUndefinedPolicyValues()
    {
        var root = NewTempDirectory();
        var metadata = JsonColdStoreStoreMetadata.CreateNew(
            new JsonColdStoreOptionsBuilder(root).Build(),
            providerVersion: "test") with
        {
            Policy = new JsonColdStoreStorePolicySnapshot
            {
                Compression = (JsonColdStoreCompression)999,
                StartupMode = JsonColdStoreStartupMode.MetadataOnly,
                FullScanPolicy = JsonColdStoreScanPolicy.FailUnlessExplicit,
            },
        };
        await WriteMetadataAsync(root, metadata);
        var catalog = new JsonColdStoreCatalog(
            new JsonColdStoreOptionsBuilder(root).UseFsyncOnWrite(false).Build());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => catalog.LoadAndValidateAsync());

        Assert.Contains("compression", exception.Message, StringComparison.OrdinalIgnoreCase);
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

        var metadataBytes = await File.ReadAllBytesAsync(Path.Combine(root, JsonColdStoreCatalog.StoreFileName));
        var metadataText = PrintableText(metadataBytes);
        Assert.DoesNotContain("do-not-persist", metadataText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("encryptionEnabled", metadataText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureInitializedAsyncProtectsEncryptedRootMetadata()
    {
        var root = NewTempDirectory();
        using var key = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseEncryptionKey(key)
            .UseFsyncOnWrite(false)
            .Build();
        var catalog = new JsonColdStoreCatalog(options);

        var metadata = await catalog.EnsureInitializedAsync();

        var metadataBytes = await File.ReadAllBytesAsync(Path.Combine(root, JsonColdStoreCatalog.StoreFileName));
        var metadataText = PrintableText(metadataBytes);
        var reopened = await new JsonColdStoreCatalog(options).LoadAndValidateAsync();
        Assert.True(metadata.Policy.EncryptionEnabled);
        Assert.True(JsonColdStorePayloadCodec.IsEnvelope(metadataBytes));
        Assert.DoesNotContain("encryptionEnabled", metadataText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("formatVersion", metadataText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(metadata.StoreId, reopened.StoreId);
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

    private static string PrintableText(byte[] bytes) =>
        string.Concat(bytes.Select(value => value is >= 32 and <= 126 ? (char)value : '.'));
}
