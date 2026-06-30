using System.Text.Json;
using System.Text.Json.Serialization;

namespace EFC.JSONColdStore.Storage;

internal sealed class JsonColdStoreCatalog
{
    internal const int CurrentFormatVersion = 1;
    internal const string StoreFileName = "_store.json";

    private static readonly JsonSerializerOptions StoreJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly JsonColdStoreOptions _options;

    internal JsonColdStoreCatalog(JsonColdStoreOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    internal async Task<JsonColdStoreStoreMetadata> EnsureInitializedAsync(
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_options.DatabaseDirectory);

        var storeFile = GetStoreFilePath();
        if (File.Exists(storeFile))
            return await LoadAndValidateAsync(cancellationToken);

        var metadata = JsonColdStoreStoreMetadata.CreateNew(_options, JsonColdStoreProviderInfo.Version);
        await WriteMetadataAsync(metadata, cancellationToken);
        return metadata;
    }

    internal async Task<JsonColdStoreStoreMetadata> LoadIfExistsOrCreateTransientAsync(
        CancellationToken cancellationToken = default)
    {
        var storeFile = GetStoreFilePath();
        return File.Exists(storeFile)
            ? await LoadAndValidateAsync(cancellationToken)
            : JsonColdStoreStoreMetadata.CreateNew(_options, JsonColdStoreProviderInfo.Version);
    }

    internal async Task<JsonColdStoreStoreMetadata> LoadAndValidateAsync(
        CancellationToken cancellationToken = default)
    {
        var bytes = await JsonColdStoreFileReader.ReadAllBytesAsync(
            _options,
            GetStoreFilePath(),
            cancellationToken);
        var metadata = JsonSerializer.Deserialize<JsonColdStoreStoreMetadata>(
            bytes,
            StoreJsonOptions);

        if (metadata is null)
            throw new InvalidDataException("The JSONColdStore metadata file is empty.");

        Validate(metadata);
        return metadata;
    }

    private async Task WriteMetadataAsync(
        JsonColdStoreStoreMetadata metadata,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata, StoreJsonOptions);
        await JsonColdStoreAtomicFileWriter.WriteAsync(
            _options.DatabaseDirectory,
            [StoreFileName],
            bytes,
            _options.FsyncOnWrite,
            cancellationToken);
    }

    private void Validate(JsonColdStoreStoreMetadata metadata)
    {
        if (metadata.FormatVersion != CurrentFormatVersion)
        {
            throw new NotSupportedException(
                $"JSONColdStore format version {metadata.FormatVersion} is not supported.");
        }

        if (metadata.Policy.EncryptionEnabled && _options.Encryption is null)
        {
            throw new InvalidOperationException(
                "This JSONColdStore database is encrypted and requires a configured encryption key.");
        }

        if (_options.Encryption?.RequireEncryptedStore == true && !metadata.Policy.EncryptionEnabled)
        {
            throw new InvalidOperationException(
                "The configured encryption policy requires an encrypted JSONColdStore database.");
        }
    }

    private string GetStoreFilePath() =>
        JsonColdStorePathValidator.GetSafeChildPath(_options.DatabaseDirectory, StoreFileName);

}

internal sealed record JsonColdStoreStoreMetadata
{
    public required Guid StoreId { get; init; }

    public int FormatVersion { get; init; } = JsonColdStoreCatalog.CurrentFormatVersion;

    public required DateTimeOffset CreatedAt { get; init; }

    public required string ProviderVersion { get; init; }

    public required JsonColdStoreStorePolicySnapshot Policy { get; init; }

    internal static JsonColdStoreStoreMetadata CreateNew(
        JsonColdStoreOptions options,
        string providerVersion)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new JsonColdStoreStoreMetadata
        {
            StoreId = Guid.NewGuid(),
            FormatVersion = JsonColdStoreCatalog.CurrentFormatVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            ProviderVersion = providerVersion,
            Policy = JsonColdStoreStorePolicySnapshot.FromOptions(options),
        };
    }
}

internal sealed record JsonColdStoreStorePolicySnapshot
{
    public JsonColdStoreCompression Compression { get; init; }

    public bool EncryptionEnabled { get; init; }

    public JsonColdStoreStartupMode StartupMode { get; init; }

    public JsonColdStoreScanPolicy FullScanPolicy { get; init; }

    internal static JsonColdStoreStorePolicySnapshot FromOptions(JsonColdStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new JsonColdStoreStorePolicySnapshot
        {
            Compression = options.Compression,
            EncryptionEnabled = options.Encryption is not null,
            StartupMode = options.StartupMode,
            FullScanPolicy = options.FullScanPolicy,
        };
    }
}
