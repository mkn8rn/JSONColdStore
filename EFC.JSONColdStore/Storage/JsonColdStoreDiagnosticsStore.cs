using System.Security.Cryptography;
using System.Text.Json;
using EFC.JSONColdStore.Infrastructure;

namespace EFC.JSONColdStore.Storage;

internal sealed class JsonColdStoreDiagnosticsStore
{
    private readonly JsonColdStoreOptions _options;
    private readonly JsonColdStoreModelDescriptor _modelDescriptor;

    internal JsonColdStoreDiagnosticsStore(
        JsonColdStoreOptions options,
        JsonColdStoreModelDescriptor modelDescriptor)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _modelDescriptor = modelDescriptor ?? throw new ArgumentNullException(nameof(modelDescriptor));
    }

    internal async Task<JsonColdStoreDiagnosticsResult> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var metadataDiagnostics = await TryReadMetadataAsync(cancellationToken).ConfigureAwait(false);
        var metadata = metadataDiagnostics.Metadata;
        var entityDiagnostics = _modelDescriptor.Entities
            .Select(CreateEntityDiagnostics)
            .ToArray();

        return new JsonColdStoreDiagnosticsResult
        {
            HasStoreMetadata = metadataDiagnostics.Exists,
            StoreMetadataReadable = metadata is not null,
            StoreMetadataProtected = metadataDiagnostics.Protected,
            StoreId = metadata?.StoreId,
            FormatVersion = metadata?.FormatVersion,
            ProviderVersion = metadata?.ProviderVersion,
            Compression = metadata?.Policy.Compression ?? _options.Compression,
            EncryptionEnabled = metadata?.Policy.EncryptionEnabled
                ?? (metadataDiagnostics.Protected || _options.Encryption is not null),
            IntegrityChecksumsEnabled = _options.Integrity.EnableChecksums,
            KeyedIntegrityEnabled = _options.Integrity.Key is not null,
            StartupMode = metadata?.Policy.StartupMode ?? _options.StartupMode,
            FullScanPolicy = metadata?.Policy.FullScanPolicy ?? _options.FullScanPolicy,
            MappedEntityCount = entityDiagnostics.Length,
            RecordFileCount = entityDiagnostics.Sum(entity => entity.RecordFileCount),
            IndexFileCount = entityDiagnostics.Sum(entity => entity.IndexFileCount),
            LegacyRecordFileCount = entityDiagnostics.Sum(entity => entity.LegacyRecordFileCount),
            PendingManifestCount = CountFiles("_transactions", "pending", "*.json"),
            FailedManifestCount = CountFiles("_transactions", "failed", "*.json"),
            StagedWriteCount = CountFiles("_transactions", "staged", "*.jcs"),
            QuarantineFileCount = CountFiles("_quarantine", "records", "*.jcs"),
            SnapshotCount = CountDirectories("_snapshots"),
            EventLogFileCount = CountFiles("_events", "*.jsonl"),
            TemporaryFileCount = CountTemporaryFiles(_options.DatabaseDirectory),
            Entities = entityDiagnostics,
        };
    }

    private async Task<JsonColdStoreMetadataDiagnostics> TryReadMetadataAsync(
        CancellationToken cancellationToken)
    {
        var storePath = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            JsonColdStoreCatalog.StoreFileName);
        if (!File.Exists(storePath))
            return new JsonColdStoreMetadataDiagnostics(false, false, null);

        var protectedMetadata = false;
        try
        {
            var bytes = await JsonColdStoreFileReader.ReadAllBytesAsync(
                _options,
                storePath,
                cancellationToken).ConfigureAwait(false);
            protectedMetadata = JsonColdStorePayloadCodec.IsEnvelope(bytes);

            var catalog = new JsonColdStoreCatalog(_options);
            var metadata = await catalog.LoadAndValidateAsync(cancellationToken).ConfigureAwait(false);
            return new JsonColdStoreMetadataDiagnostics(true, protectedMetadata, metadata);
        }
        catch (Exception ex) when (IsMetadataDiagnosticReadFailure(ex))
        {
            return new JsonColdStoreMetadataDiagnostics(true, protectedMetadata, null);
        }
    }

    private static bool IsMetadataDiagnosticReadFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException
            or NotSupportedException
            or InvalidOperationException
            or CryptographicException;

    private JsonColdStoreEntityDiagnostics CreateEntityDiagnostics(
        JsonColdStoreEntityDescriptor descriptor)
    {
        return new JsonColdStoreEntityDiagnostics
        {
            EntityName = descriptor.EntityName,
            ClrTypeName = descriptor.ClrType.FullName ?? descriptor.ClrType.Name,
            DeclaredIndexCount = descriptor.Indexes.Count,
            RecordFileCount = CountFiles(
                "entities",
                JsonColdStoreNameEncoder.EncodePathSegment(descriptor.EntityName),
                "records",
                "*.jcs"),
            IndexFileCount = CountFiles(
                "entities",
                JsonColdStoreNameEncoder.EncodePathSegment(descriptor.EntityName),
                "indexes",
                "*.json"),
            LegacyRecordFileCount = CountLegacyRecords(descriptor),
        };
    }

    private int CountLegacyRecords(JsonColdStoreEntityDescriptor descriptor)
    {
        var legacyDirectory = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            descriptor.ClrType.Name);
        if (!Directory.Exists(legacyDirectory))
            return 0;

        return Directory.EnumerateFiles(legacyDirectory, "*.json")
            .Count(JsonColdStoreLegacyRecordNames.IsSafeRecordFile);
    }

    private int CountFiles(params string[] pathSegmentsAndPattern)
    {
        if (pathSegmentsAndPattern.Length < 2)
            throw new ArgumentException("A directory path and search pattern are required.", nameof(pathSegmentsAndPattern));

        var pattern = pathSegmentsAndPattern[^1];
        var directorySegments = pathSegmentsAndPattern[..^1];
        var directory = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            directorySegments);

        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, pattern).Count()
            : 0;
    }

    private int CountDirectories(params string[] pathSegments)
    {
        var directory = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            pathSegments);

        return Directory.Exists(directory)
            ? Directory.EnumerateDirectories(directory).Count()
            : 0;
    }

    private static int CountTemporaryFiles(string directory)
    {
        if (!Directory.Exists(directory))
            return 0;

        var count = Directory.EnumerateFiles(directory)
            .Count(path => Path.GetFileName(path).Contains(".tmp-", StringComparison.Ordinal));

        foreach (var childDirectory in Directory.EnumerateDirectories(directory))
        {
            if (string.Equals(Path.GetFileName(childDirectory), "_snapshots", StringComparison.Ordinal))
                continue;

            count += CountTemporaryFiles(childDirectory);
        }

        return count;
    }

    private sealed record JsonColdStoreMetadataDiagnostics(
        bool Exists,
        bool Protected,
        JsonColdStoreStoreMetadata? Metadata);
}
