using System.Security.Cryptography;
using System.Text.Json;
using JSONColdStore.Infrastructure;

namespace JSONColdStore.Storage;

internal sealed class JsonColdStoreDiagnosticsStore
{
    private const string LegacyIndexPrefix = "_index_";
    private const string LegacyChecksumDocumentName = "_checksums.json";
    private const string LegacyChecksumSignatureName = "_checksums.sig";
    private const string LegacySharedRowsFileName = "_rows.json";

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
        var skippedUnsafePaths = CreatePathSet();
        if (!DatabaseRootExistsAndIsSafe(_options.DatabaseDirectory, skippedUnsafePaths))
        {
            return CreateResult(
                new JsonColdStoreMetadataDiagnostics(false, false, null),
                CreateEmptyEntityDiagnostics(),
                DiagnosticCounter.Empty,
                DiagnosticCounter.Empty,
                DiagnosticCounter.Empty,
                DiagnosticCounter.Empty,
                snapshots: 0,
                DiagnosticCounter.Empty,
                DiagnosticCounter.Empty,
                DiagnosticCounter.Empty,
                skippedUnsafePaths);
        }

        var metadataDiagnostics = await TryReadMetadataAsync(
            skippedUnsafePaths,
            cancellationToken).ConfigureAwait(false);
        var metadata = metadataDiagnostics.Metadata;
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
        var plaintextProtectedDocuments = ShouldCountPlaintextProtectedDocuments(metadataDiagnostics, metadata)
            ? CountPlaintextProtectedDocuments(skippedUnsafePaths)
            : DiagnosticCounter.Empty;

        return CreateResult(
            metadataDiagnostics,
            entityDiagnostics,
            pendingManifests,
            failedManifests,
            stagedWrites,
            quarantineFiles,
            snapshots,
            eventLogs,
            temporaryFiles,
            plaintextProtectedDocuments,
            skippedUnsafePaths);
    }

    private JsonColdStoreDiagnosticsResult CreateResult(
        JsonColdStoreMetadataDiagnostics metadataDiagnostics,
        IReadOnlyList<JsonColdStoreEntityDiagnostics> entityDiagnostics,
        DiagnosticCounter pendingManifests,
        DiagnosticCounter failedManifests,
        DiagnosticCounter stagedWrites,
        DiagnosticCounter quarantineFiles,
        int snapshots,
        DiagnosticCounter eventLogs,
        DiagnosticCounter temporaryFiles,
        DiagnosticCounter plaintextProtectedDocuments,
        ISet<string> skippedUnsafePaths)
    {
        var metadata = metadataDiagnostics.Metadata;
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
            MappedEntityCount = entityDiagnostics.Count,
            RecordFileCount = entityDiagnostics.Sum(entity => entity.RecordFileCount),
            IndexFileCount = entityDiagnostics.Sum(entity => entity.IndexFileCount),
            LegacyRecordFileCount = entityDiagnostics.Sum(entity => entity.LegacyRecordFileCount),
            LegacyIndexShardFileCount = entityDiagnostics.Sum(entity => entity.LegacyIndexShardFileCount),
            LegacyChecksumSidecarFileCount =
                entityDiagnostics.Sum(entity => entity.LegacyChecksumSidecarFileCount),
            LegacySharedRowsFileCount = entityDiagnostics.Sum(entity => entity.LegacySharedRowsFileCount),
            PlaintextProtectedDocumentCount = plaintextProtectedDocuments.Count,
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

    private JsonColdStoreEntityDiagnostics[] CreateEmptyEntityDiagnostics() =>
        _modelDescriptor.Entities
            .Select(descriptor => new JsonColdStoreEntityDiagnostics
            {
                EntityName = descriptor.EntityName,
                ClrTypeName = descriptor.ClrType.FullName ?? descriptor.ClrType.Name,
                DeclaredIndexCount = descriptor.Indexes.Count,
            })
            .ToArray();

    private async Task<JsonColdStoreMetadataDiagnostics> TryReadMetadataAsync(
        ISet<string> skippedUnsafePaths,
        CancellationToken cancellationToken)
    {
        var storePath = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            JsonColdStoreCatalog.StoreFileName);
        var skippedBeforeMetadataProbe = skippedUnsafePaths.Count;
        if (!FileExistsAndIsSafe(storePath, skippedUnsafePaths))
        {
            var unsafeStorePathExists = skippedUnsafePaths.Count > skippedBeforeMetadataProbe;
            return new JsonColdStoreMetadataDiagnostics(unsafeStorePathExists, false, null);
        }

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
        catch (Exception ex) when (IsUnsafeMetadataDiagnosticReadFailure(ex))
        {
            RecordSkippedUnsafePath(skippedUnsafePaths, storePath);
            return new JsonColdStoreMetadataDiagnostics(true, protectedMetadata, null);
        }
        catch (Exception ex) when (IsMetadataDiagnosticReadFailure(ex))
        {
            return new JsonColdStoreMetadataDiagnostics(true, protectedMetadata, null);
        }
    }

    private static bool IsUnsafeMetadataDiagnosticReadFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException;

    private static bool IsMetadataDiagnosticReadFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException
            or NotSupportedException
            or InvalidOperationException
            or CryptographicException;

    private static bool DatabaseRootExistsAndIsSafe(
        string databaseDirectory,
        ISet<string> skippedUnsafePaths)
    {
        var root = JsonColdStorePathValidator.NormalizeDatabaseDirectory(databaseDirectory);
        try
        {
            var attributes = File.GetAttributes(root);
            var safe = (attributes & FileAttributes.Directory) != 0
                && (attributes & FileAttributes.ReparsePoint) == 0;
            if (!safe)
                RecordSkippedUnsafePath(skippedUnsafePaths, root);

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
            RecordSkippedUnsafePath(skippedUnsafePaths, root);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            RecordSkippedUnsafePath(skippedUnsafePaths, root);
            return false;
        }
    }

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
        var legacyIndexShardFiles = CountLegacyIndexShards(descriptor, skippedUnsafePaths);
        var legacyChecksumSidecarFiles = CountLegacyChecksumSidecars(descriptor, skippedUnsafePaths);
        var legacySharedRowsFiles = CountLegacySharedRows(descriptor, skippedUnsafePaths);

        return new JsonColdStoreEntityDiagnostics
        {
            EntityName = descriptor.EntityName,
            ClrTypeName = descriptor.ClrType.FullName ?? descriptor.ClrType.Name,
            DeclaredIndexCount = descriptor.Indexes.Count,
            RecordFileCount = recordFiles.Count,
            IndexFileCount = indexFiles.Count,
            LegacyRecordFileCount = legacyRecordFiles.Count,
            LegacyIndexShardFileCount = legacyIndexShardFiles.Count,
            LegacyChecksumSidecarFileCount = legacyChecksumSidecarFiles.Count,
            LegacySharedRowsFileCount = legacySharedRowsFiles.Count,
            SkippedUnsafePathCount =
                recordFiles.SkippedUnsafePathCount
                + indexFiles.SkippedUnsafePathCount
                + legacyRecordFiles.SkippedUnsafePathCount
                + legacyIndexShardFiles.SkippedUnsafePathCount
                + legacyChecksumSidecarFiles.SkippedUnsafePathCount
                + legacySharedRowsFiles.SkippedUnsafePathCount,
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

    private DiagnosticCounter CountLegacyIndexShards(
        JsonColdStoreEntityDescriptor descriptor,
        ISet<string> skippedUnsafePaths)
    {
        var legacyDirectory = GetLegacyArtifactDirectory(descriptor);
        var flatShards = CountFilesInDirectory(
            legacyDirectory,
            LegacyIndexPrefix + "*.json",
            skippedUnsafePaths);
        var keyScopedShards = CountFilesInChildDirectories(
            legacyDirectory,
            LegacyIndexPrefix + "*",
            "*.json",
            skippedUnsafePaths);

        return new DiagnosticCounter(
            flatShards.Count + keyScopedShards.Count,
            flatShards.SkippedUnsafePathCount + keyScopedShards.SkippedUnsafePathCount);
    }

    private DiagnosticCounter CountLegacyChecksumSidecars(
        JsonColdStoreEntityDescriptor descriptor,
        ISet<string> skippedUnsafePaths)
    {
        var legacyDirectory = GetLegacyArtifactDirectory(descriptor);
        return CountFilesInDirectory(
            legacyDirectory,
            "_checksums.*",
            skippedUnsafePaths,
            file =>
            {
                var fileName = Path.GetFileName(file);
                return string.Equals(fileName, LegacyChecksumDocumentName, StringComparison.Ordinal)
                    || string.Equals(fileName, LegacyChecksumSignatureName, StringComparison.Ordinal);
            });
    }

    private DiagnosticCounter CountLegacySharedRows(
        JsonColdStoreEntityDescriptor descriptor,
        ISet<string> skippedUnsafePaths)
    {
        if (!descriptor.IsSharedType)
            return DiagnosticCounter.Empty;

        return CountFilesInDirectory(
            GetLegacyArtifactDirectory(descriptor),
            LegacySharedRowsFileName,
            skippedUnsafePaths);
    }

    private DiagnosticCounter CountPlaintextProtectedDocuments(
        ISet<string> skippedUnsafePaths)
    {
        var storeDocument = CountPlaintextProtectedDocument(
            JsonColdStorePathValidator.GetSafeChildPath(
                _options.DatabaseDirectory,
                JsonColdStoreCatalog.StoreFileName),
            skippedUnsafePaths);
        var modelDocument = CountPlaintextProtectedDocument(
            JsonColdStorePathValidator.GetSafeChildPath(
                _options.DatabaseDirectory,
                JsonColdStoreModelCatalog.ModelFileName),
            skippedUnsafePaths);
        var count = storeDocument.Count + modelDocument.Count;
        var skipped = storeDocument.SkippedUnsafePathCount + modelDocument.SkippedUnsafePathCount;

        foreach (var descriptor in _modelDescriptor.Entities)
        {
            var indexDirectory = JsonColdStorePathValidator.GetSafeChildPath(
                _options.DatabaseDirectory,
                "entities",
                JsonColdStoreNameEncoder.EncodePathSegment(descriptor.EntityName),
                "indexes");
            var indexDocuments = CountFilesInDirectory(
                indexDirectory,
                "*.json",
                skippedUnsafePaths,
                file => IsPlaintextProtectedDocument(file, skippedUnsafePaths));
            count += indexDocuments.Count;
            skipped += indexDocuments.SkippedUnsafePathCount;
        }

        return new DiagnosticCounter(count, skipped);
    }

    private static DiagnosticCounter CountPlaintextProtectedDocument(
        string path,
        ISet<string> skippedUnsafePaths)
    {
        var skippedBeforeFileCheck = skippedUnsafePaths.Count;
        if (!FileExistsAndIsSafe(path, skippedUnsafePaths))
        {
            return new DiagnosticCounter(
                0,
                skippedUnsafePaths.Count - skippedBeforeFileCheck);
        }

        return new DiagnosticCounter(
            IsPlaintextProtectedDocument(path, skippedUnsafePaths) ? 1 : 0,
            skippedUnsafePaths.Count - skippedBeforeFileCheck);
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

    private static DiagnosticCounter CountFilesInChildDirectories(
        string directory,
        string childDirectoryPattern,
        string filePattern,
        ISet<string> skippedUnsafePaths)
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
            foreach (var childDirectory in Directory.EnumerateDirectories(directory, childDirectoryPattern))
            {
                if (JsonColdStoreDirectoryWalker.IsReparsePoint(childDirectory))
                {
                    skipped += RecordSkippedUnsafePath(skippedUnsafePaths, childDirectory);
                    continue;
                }

                var childFiles = CountFilesInDirectory(childDirectory, filePattern, skippedUnsafePaths);
                count += childFiles.Count;
                skipped += childFiles.SkippedUnsafePathCount;
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

    private static bool FileExistsAndIsSafe(
        string path,
        ISet<string> skippedUnsafePaths)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            var safe = (attributes & FileAttributes.Directory) == 0
                && (attributes & FileAttributes.ReparsePoint) == 0;
            if (!safe)
                RecordSkippedUnsafePath(skippedUnsafePaths, path);

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
            RecordSkippedUnsafePath(skippedUnsafePaths, path);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            RecordSkippedUnsafePath(skippedUnsafePaths, path);
            return false;
        }
    }

    private static bool IsPlaintextProtectedDocument(
        string path,
        ISet<string> skippedUnsafePaths)
    {
        try
        {
            var header = new byte[4];
            using var stream = File.OpenRead(path);
            var read = stream.Read(header);
            return read < header.Length || !JsonColdStorePayloadCodec.IsEnvelope(header);
        }
        catch (IOException)
        {
            RecordSkippedUnsafePath(skippedUnsafePaths, path);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            RecordSkippedUnsafePath(skippedUnsafePaths, path);
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

    private static bool ShouldCountPlaintextProtectedDocuments(
        JsonColdStoreMetadataDiagnostics metadataDiagnostics,
        JsonColdStoreStoreMetadata? metadata) =>
        metadata?.Policy.EncryptionEnabled
            ?? metadataDiagnostics.Protected;

    private string GetLegacyArtifactDirectory(JsonColdStoreEntityDescriptor descriptor) =>
        JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            descriptor.IsSharedType ? descriptor.EntityName : descriptor.ClrType.Name);

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
