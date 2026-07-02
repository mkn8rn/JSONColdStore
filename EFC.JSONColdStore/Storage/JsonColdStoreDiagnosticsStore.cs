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
        var skippedUnsafePaths = CreatePathSet();
        var entityDiagnostics = _modelDescriptor.Entities
            .Select(descriptor => CreateEntityDiagnostics(descriptor, skippedUnsafePaths))
            .ToArray();
        var pendingManifests = CountFiles(skippedUnsafePaths, "_transactions", "pending", "*.json");
        var failedManifests = CountFiles(skippedUnsafePaths, "_transactions", "failed", "*.json");
        var stagedWrites = CountFiles(skippedUnsafePaths, "_transactions", "staged", "*.jcs");
        var quarantineFiles = CountFiles(skippedUnsafePaths, "_quarantine", "records", "*.jcs");
        var snapshots = CountDirectories(skippedUnsafePaths, "_snapshots");
        var eventLogs = CountFiles(skippedUnsafePaths, "_events", "*.jsonl");
        var temporaryFiles = CountTemporaryFiles(_options.DatabaseDirectory, skippedUnsafePaths);

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
            PendingManifestCount = pendingManifests.Count,
            FailedManifestCount = failedManifests.Count,
            StagedWriteCount = stagedWrites.Count,
            QuarantineFileCount = quarantineFiles.Count,
            SnapshotCount = snapshots,
            EventLogFileCount = eventLogs.Count,
            TemporaryFileCount = temporaryFiles.Count,
            SkippedUnsafePathCount = skippedUnsafePaths.Count,
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
        JsonColdStoreEntityDescriptor descriptor,
        ISet<string> skippedUnsafePaths)
    {
        var recordFiles = CountFiles(
            skippedUnsafePaths,
            "entities",
            JsonColdStoreNameEncoder.EncodePathSegment(descriptor.EntityName),
            "records",
            "*.jcs");
        var indexFiles = CountFiles(
            skippedUnsafePaths,
            "entities",
            JsonColdStoreNameEncoder.EncodePathSegment(descriptor.EntityName),
            "indexes",
            "*.json");
        var legacyRecordFiles = CountLegacyRecords(descriptor, skippedUnsafePaths);

        return new JsonColdStoreEntityDiagnostics
        {
            EntityName = descriptor.EntityName,
            ClrTypeName = descriptor.ClrType.FullName ?? descriptor.ClrType.Name,
            DeclaredIndexCount = descriptor.Indexes.Count,
            RecordFileCount = recordFiles.Count,
            IndexFileCount = indexFiles.Count,
            LegacyRecordFileCount = legacyRecordFiles.Count,
            SkippedUnsafePathCount =
                recordFiles.SkippedUnsafePathCount
                + indexFiles.SkippedUnsafePathCount
                + legacyRecordFiles.SkippedUnsafePathCount,
        };
    }

    private DiagnosticCounter CountLegacyRecords(
        JsonColdStoreEntityDescriptor descriptor,
        ISet<string> skippedUnsafePaths)
    {
        var legacyDirectory = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            descriptor.ClrType.Name);

        return CountFilesInDirectory(
            legacyDirectory,
            "*.json",
            skippedUnsafePaths,
            JsonColdStoreLegacyRecordNames.IsSafeRecordFile);
    }

    private DiagnosticCounter CountFiles(
        ISet<string> skippedUnsafePaths,
        params string[] pathSegmentsAndPattern)
    {
        if (pathSegmentsAndPattern.Length < 2)
            throw new ArgumentException("A directory path and search pattern are required.", nameof(pathSegmentsAndPattern));

        var pattern = pathSegmentsAndPattern[^1];
        var directorySegments = pathSegmentsAndPattern[..^1];
        var directory = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            directorySegments);

        return CountFilesInDirectory(directory, pattern, skippedUnsafePaths);
    }

    private int CountDirectories(
        ISet<string> skippedUnsafePaths,
        params string[] pathSegments)
    {
        var directory = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            pathSegments);

        if (!DirectoryExistsAndIsSafe(directory, skippedUnsafePaths))
            return 0;

        try
        {
            var count = 0;
            foreach (var childDirectory in Directory.EnumerateDirectories(directory))
            {
                if (JsonColdStoreDirectoryWalker.IsReparsePoint(childDirectory))
                {
                    RecordSkippedUnsafePath(skippedUnsafePaths, childDirectory);
                    continue;
                }

                count++;
            }

            return count;
        }
        catch (IOException)
        {
            RecordSkippedUnsafePath(skippedUnsafePaths, directory);
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            RecordSkippedUnsafePath(skippedUnsafePaths, directory);
            return 0;
        }
    }

    private static DiagnosticCounter CountFilesInDirectory(
        string directory,
        string pattern,
        ISet<string> skippedUnsafePaths,
        Func<string, bool>? shouldCountFile = null)
    {
        var skippedBeforeDirectoryCheck = skippedUnsafePaths.Count;
        if (!DirectoryExistsAndIsSafe(directory, skippedUnsafePaths))
        {
            return new DiagnosticCounter(
                0,
                skippedUnsafePaths.Count - skippedBeforeDirectoryCheck);
        }

        try
        {
            var count = 0;
            var skipped = 0;
            foreach (var file in Directory.EnumerateFiles(directory, pattern))
            {
                if (JsonColdStoreDirectoryWalker.IsReparsePoint(file))
                {
                    skipped += RecordSkippedUnsafePath(skippedUnsafePaths, file);
                    continue;
                }

                if (shouldCountFile?.Invoke(file) ?? true)
                    count++;
            }

            return new DiagnosticCounter(count, skipped);
        }
        catch (IOException)
        {
            return new DiagnosticCounter(
                0,
                RecordSkippedUnsafePath(skippedUnsafePaths, directory));
        }
        catch (UnauthorizedAccessException)
        {
            return new DiagnosticCounter(
                0,
                RecordSkippedUnsafePath(skippedUnsafePaths, directory));
        }
    }

    private static bool DirectoryExistsAndIsSafe(
        string directory,
        ISet<string> skippedUnsafePaths)
    {
        try
        {
            var attributes = File.GetAttributes(directory);
            var safe = (attributes & FileAttributes.Directory) != 0
                && (attributes & FileAttributes.ReparsePoint) == 0;
            if (!safe)
                RecordSkippedUnsafePath(skippedUnsafePaths, directory);

            return safe;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            RecordSkippedUnsafePath(skippedUnsafePaths, directory);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            RecordSkippedUnsafePath(skippedUnsafePaths, directory);
            return false;
        }
    }

    private static DiagnosticCounter CountTemporaryFiles(
        string directory,
        ISet<string> skippedUnsafePaths)
    {
        if (!Directory.Exists(directory))
            return DiagnosticCounter.Empty;

        var count = 0;
        var skipped = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (JsonColdStoreDirectoryWalker.IsReparsePoint(file))
                {
                    skipped += RecordSkippedUnsafePath(skippedUnsafePaths, file);
                    continue;
                }

                if (Path.GetFileName(file).Contains(".tmp-", StringComparison.Ordinal))
                    count++;
            }

            foreach (var childDirectory in Directory.EnumerateDirectories(directory))
            {
                if (IsSnapshotDirectory(childDirectory))
                    continue;

                if (JsonColdStoreDirectoryWalker.IsReparsePoint(childDirectory))
                {
                    skipped += RecordSkippedUnsafePath(skippedUnsafePaths, childDirectory);
                    continue;
                }

                var childCount = CountTemporaryFiles(childDirectory, skippedUnsafePaths);
                count += childCount.Count;
                skipped += childCount.SkippedUnsafePathCount;
            }

            return new DiagnosticCounter(count, skipped);
        }
        catch (IOException)
        {
            return new DiagnosticCounter(
                count,
                skipped + RecordSkippedUnsafePath(skippedUnsafePaths, directory));
        }
        catch (UnauthorizedAccessException)
        {
            return new DiagnosticCounter(
                count,
                skipped + RecordSkippedUnsafePath(skippedUnsafePaths, directory));
        }
    }

    private static bool IsSnapshotDirectory(string directory) =>
        string.Equals(Path.GetFileName(directory), "_snapshots", StringComparison.Ordinal);

    private static HashSet<string> CreatePathSet() =>
        new(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

    private static int RecordSkippedUnsafePath(ISet<string> skippedUnsafePaths, string path) =>
        skippedUnsafePaths.Add(Path.GetFullPath(path)) ? 1 : 0;

    private readonly record struct DiagnosticCounter(
        int Count,
        int SkippedUnsafePathCount)
    {
        internal static DiagnosticCounter Empty { get; } = new(0, 0);
    }

    private sealed record JsonColdStoreMetadataDiagnostics(
        bool Exists,
        bool Protected,
        JsonColdStoreStoreMetadata? Metadata);
}
