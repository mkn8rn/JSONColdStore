using System.Runtime.CompilerServices;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace EFC.JSONColdStore.Storage;

internal sealed class JsonColdStoreRecordStore
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly JsonColdStoreOptions _options;
    private readonly JsonColdStoreOptions _writeOptions;
    private readonly bool _protectManifests;
    private readonly bool _allowStorageMutations;
    private readonly JsonColdStoreEventLog _eventLog;

    internal JsonColdStoreRecordStore(
        JsonColdStoreOptions options,
        bool protectManifests = false,
        bool allowStorageMutations = true,
        bool protectRecordPayloads = true)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _writeOptions = protectRecordPayloads
            ? _options
            : _options with { Encryption = null };
        _protectManifests = protectManifests;
        _allowStorageMutations = allowStorageMutations;
        _eventLog = new JsonColdStoreEventLog(options, protectManifests);
    }

    internal async Task WriteRecordAsync(
        string entityName,
        string recordId,
        ReadOnlyMemory<byte> utf8Json,
        CancellationToken cancellationToken = default)
    {
        RequireStorageMutations();

        var recordPath = GetRecordPathSegments(entityName, recordId);
        var payload = JsonColdStorePayloadCodec.Encode(utf8Json.Span, _writeOptions);
        var manifest = JsonColdStoreWriteManifest.CreateStagedWrite(recordPath, payload.Length);
        var manifestPath = GetPendingManifestPathSegments(manifest.ManifestId);
        var manifestBytes = EncodeManifest(manifest);

        await JsonColdStoreAtomicFileWriter.WriteAsync(
            _options,
            manifest.StagedPathSegments!,
            payload,
            cancellationToken);

        await JsonColdStoreAtomicFileWriter.WriteAsync(
            _options,
            manifestPath,
            manifestBytes,
            cancellationToken);

        try
        {
            PublishStagedWrite(manifest);
            DeleteIfExists(manifestPath);
            await _eventLog.AppendAsync(
                "record.write",
                entityName,
                recordId,
                manifest.ManifestId,
                cancellationToken: cancellationToken);
        }
        catch
        {
            throw;
        }
    }

    internal async Task DeleteRecordAsync(
        string entityName,
        string recordId,
        CancellationToken cancellationToken = default)
    {
        RequireStorageMutations();

        var recordPath = GetRecordPathSegments(entityName, recordId);
        var manifest = JsonColdStoreWriteManifest.CreateDelete(recordPath);
        var manifestPath = GetPendingManifestPathSegments(manifest.ManifestId);
        var manifestBytes = EncodeManifest(manifest);

        await JsonColdStoreAtomicFileWriter.WriteAsync(
            _options,
            manifestPath,
            manifestBytes,
            cancellationToken);

        var targetPath = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            [.. recordPath]);

        DeleteFileIfExists(targetPath);

        DeleteIfExists(manifestPath);
        await _eventLog.AppendAsync(
            "record.delete",
            entityName,
            recordId,
            manifest.ManifestId,
            cancellationToken: cancellationToken);
    }

    internal async Task<byte[]> ReadRecordAsync(
        string entityName,
        string recordId,
        CancellationToken cancellationToken = default)
    {
        var recordPath = GetRecordPathSegments(entityName, recordId);
        var activeRecordPath = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            [.. recordPath]);
        var payload = await JsonColdStoreAtomicFileWriter.ReadAsync(
            _options,
            recordPath,
            cancellationToken);

        try
        {
            return JsonColdStorePayloadCodec.Decode(payload, _options);
        }
        catch (InvalidDataException)
        {
            QuarantineRecordPath(activeRecordPath);
            throw;
        }
    }

    internal bool RecordExists(string entityName, string recordId)
    {
        var path = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            [.. GetRecordPathSegments(entityName, recordId)]);

        JsonColdStoreFileGuard.ThrowIfReparsePoint(
            path,
            "The JSONColdStore current record cannot be a reparse point.");

        return File.Exists(path);
    }

    internal bool EntityHasRecords(string entityName)
    {
        var recordsDirectory = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            "entities",
            JsonColdStoreNameEncoder.EncodePathSegment(entityName),
            "records");

        return CurrentRecordsDirectoryExistsAndIsSafe(recordsDirectory)
            && Directory.EnumerateFiles(recordsDirectory, "*.jcs").Any();
    }

    internal async IAsyncEnumerable<byte[]> ReadAllRecordsAsync(
        string entityName,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var record in ReadAllNamedRecordsAsync(entityName, cancellationToken))
            yield return record.Payload;
    }

    internal async IAsyncEnumerable<JsonColdStoreRecord> ReadAllNamedRecordsAsync(
        string entityName,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var recordsDirectory = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            "entities",
            JsonColdStoreNameEncoder.EncodePathSegment(entityName),
            "records");

        if (!CurrentRecordsDirectoryExistsAndIsSafe(recordsDirectory))
            yield break;

        foreach (var recordPath in Directory.EnumerateFiles(recordsDirectory, "*.jcs").Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recordId = DecodeCurrentRecordId(recordPath);
            var payload = await JsonColdStoreFileReader.ReadAllBytesAsync(_options, recordPath, cancellationToken);
            byte[] decoded;
            try
            {
                decoded = JsonColdStorePayloadCodec.Decode(payload, _options);
            }
            catch (InvalidDataException)
            {
                QuarantineRecordPath(recordPath);
                throw;
            }

            yield return new JsonColdStoreRecord(recordId, decoded);
        }
    }

    private static bool CurrentRecordsDirectoryExistsAndIsSafe(string recordsDirectory)
    {
        if (!Directory.Exists(recordsDirectory))
            return false;

        if (JsonColdStoreDirectoryWalker.IsReparsePoint(recordsDirectory))
        {
            throw new JsonColdStoreUnsafePathException(
                "The JSONColdStore current records directory cannot be a reparse point.");
        }

        return true;
    }

    internal async Task<JsonColdStoreStartupValidationResult> VerifyAllRecordsAsync(
        CancellationToken cancellationToken = default)
    {
        var entitiesDirectory = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            "entities");

        if (!Directory.Exists(entitiesDirectory))
            return new JsonColdStoreStartupValidationResult(0);

        var verificationOptions = _options.Integrity.VerifyOnStartup
            ? _options with
            {
                Integrity = _options.Integrity with
                {
                    VerifyOnRead = true,
                },
            }
            : _options;
        var verifiedRecords = 0;

        foreach (var recordPath in EnumerateCurrentRecordPathsForMaintenance(entitiesDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = DecodeCurrentRecordId(recordPath);
            var payload = await JsonColdStoreFileReader.ReadAllBytesAsync(_options, recordPath, cancellationToken);
            try
            {
                _ = JsonColdStorePayloadCodec.Decode(payload, verificationOptions);
            }
            catch (InvalidDataException)
            {
                QuarantineRecordPath(recordPath);
                throw;
            }

            verifiedRecords++;
        }

        return new JsonColdStoreStartupValidationResult(verifiedRecords);
    }

    internal async Task<JsonColdStoreRecordRepairResult> RepairAllRecordsAsync(
        CancellationToken cancellationToken = default)
    {
        RequireStorageMutations();

        var entitiesDirectory = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            "entities");

        if (!Directory.Exists(entitiesDirectory))
            return new JsonColdStoreRecordRepairResult(0, 0);

        var repairOptions = _options with
        {
            Integrity = _options.Integrity with
            {
                VerifyOnRead = _options.Integrity.EnableChecksums,
            },
        };
        var verifiedRecords = 0;
        var quarantinedRecords = 0;
        var recordPaths = EnumerateCurrentRecordPathsForMaintenance(entitiesDirectory)
            .ToArray();

        foreach (var recordPath in recordPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(recordPath))
                continue;

            if (!TryDecodeCurrentRecordIdForRepair(recordPath))
            {
                quarantinedRecords++;
                continue;
            }

            var payload = await JsonColdStoreFileReader.ReadAllBytesAsync(_options, recordPath, cancellationToken);
            try
            {
                _ = JsonColdStorePayloadCodec.Decode(payload, repairOptions);
                verifiedRecords++;
            }
            catch (InvalidDataException)
            {
                QuarantineRecordPath(recordPath);
                quarantinedRecords++;
            }
        }

        return new JsonColdStoreRecordRepairResult(verifiedRecords, quarantinedRecords);
    }

    private static IEnumerable<string> EnumerateCurrentRecordPathsForMaintenance(string entitiesDirectory)
    {
        if (!Directory.Exists(entitiesDirectory))
            yield break;

        foreach (var recordPath in EnumerateCurrentRecordPathsForMaintenanceDirectory(entitiesDirectory))
            yield return recordPath;
    }

    private static IEnumerable<string> EnumerateCurrentRecordPathsForMaintenanceDirectory(string directory)
    {
        if (JsonColdStoreDirectoryWalker.IsReparsePoint(directory))
        {
            throw new JsonColdStoreUnsafePathException(
                "The JSONColdStore current record directory cannot be a reparse point.");
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.jcs").Order(StringComparer.Ordinal))
        {
            if (JsonColdStoreDirectoryWalker.IsReparsePoint(file))
            {
                throw new JsonColdStoreUnsafePathException(
                    "The JSONColdStore current record file cannot be a reparse point.");
            }

            yield return file;
        }

        foreach (var childDirectory in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
        {
            foreach (var recordPath in EnumerateCurrentRecordPathsForMaintenanceDirectory(childDirectory))
                yield return recordPath;
        }
    }

    private string DecodeCurrentRecordId(string recordPath)
    {
        try
        {
            ValidateCurrentRecordPathShape(recordPath);
            return JsonColdStoreNameEncoder.DecodePathSegment(
                Path.GetFileNameWithoutExtension(recordPath));
        }
        catch (InvalidDataException)
        {
            QuarantineRecordPath(recordPath);
            throw;
        }
    }

    private bool TryDecodeCurrentRecordIdForRepair(string recordPath)
    {
        try
        {
            ValidateCurrentRecordPathShape(recordPath);
            _ = JsonColdStoreNameEncoder.DecodePathSegment(
                Path.GetFileNameWithoutExtension(recordPath));
            return true;
        }
        catch (InvalidDataException)
        {
            QuarantineRecordPath(recordPath);
            return false;
        }
    }

    private static void ValidateCurrentRecordPathShape(string recordPath)
    {
        var recordsDirectory = Path.GetDirectoryName(recordPath);
        if (!string.Equals(
                Path.GetFileName(recordsDirectory),
                "records",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The current record path is not under a records directory.");
        }

        var entityDirectory = Path.GetDirectoryName(recordsDirectory);
        if (string.IsNullOrWhiteSpace(entityDirectory))
            throw new InvalidDataException("The current record path does not include an entity directory.");

        _ = JsonColdStoreNameEncoder.DecodePathSegment(Path.GetFileName(entityDirectory));
    }

    internal async Task<JsonColdStoreRecoveryResult> RecoverPendingManifestsAsync(
        CancellationToken cancellationToken = default)
    {
        RequireStorageMutations();

        var pendingDirectory = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            "_transactions",
            "pending");

        if (!Directory.Exists(pendingDirectory))
        {
            var deletedOrphans = DeleteOrphanedStagedWrites(new HashSet<Guid>());
            return new JsonColdStoreRecoveryResult(0, 0, deletedOrphans);
        }

        var completed = 0;
        var failed = 0;
        foreach (var manifestPath in Directory.GetFiles(pendingDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            JsonColdStoreWriteManifest? manifest;
            try
            {
                var manifestBytes = await ExecuteReplayAsync(
                    token => JsonColdStoreFileReader.ReadAllBytesAsync(_options, manifestPath, token),
                    cancellationToken);
                manifest = DecodeManifest(manifestBytes);
            }
            catch (CryptographicException)
            {
                throw;
            }
            catch (InvalidOperationException) when (_protectManifests)
            {
                throw;
            }
            catch
            {
                await MoveManifestToFailedAsync(manifestPath, cancellationToken);
                failed++;
                continue;
            }

            if (manifest is null)
            {
                await MoveManifestToFailedAsync(manifestPath, cancellationToken);
                failed++;
                continue;
            }

            var targetPath = JsonColdStorePathValidator.GetSafeChildPath(
                _options.DatabaseDirectory,
                [.. manifest.TargetPathSegments]);

            switch (manifest.Operation)
            {
                case JsonColdStoreManifestOperation.Write when File.Exists(targetPath):
                    await ExecuteReplayAsync(
                        _ =>
                        {
                            DeleteStagedIfExists(manifest);
                            File.Delete(manifestPath);
                            return Task.CompletedTask;
                        },
                        cancellationToken);
                    await _eventLog.AppendAsync(
                        "manifest.recovered",
                        manifestId: manifest.ManifestId,
                        detail: "write",
                        cancellationToken: cancellationToken);
                    completed++;
                    break;

                case JsonColdStoreManifestOperation.Write
                    when manifest.StagedPathSegments is not null
                    && StagedPayloadExists(manifest):
                    await ExecuteReplayAsync(
                        _ =>
                        {
                            PublishStagedWrite(manifest);
                            File.Delete(manifestPath);
                            return Task.CompletedTask;
                        },
                        cancellationToken);
                    await _eventLog.AppendAsync(
                        "manifest.recovered",
                        manifestId: manifest.ManifestId,
                        detail: "write-staged",
                        cancellationToken: cancellationToken);
                    completed++;
                    break;

                case JsonColdStoreManifestOperation.Write:
                    await MoveManifestToFailedAsync(manifestPath, cancellationToken);
                    await _eventLog.AppendAsync(
                        "manifest.failed",
                        manifestId: manifest.ManifestId,
                        detail: "write-target-missing",
                        cancellationToken: cancellationToken);
                    failed++;
                    break;

                case JsonColdStoreManifestOperation.Delete:
                    await ExecuteReplayAsync(
                        _ =>
                        {
                            DeleteFileIfExists(targetPath);
                            DeleteFileIfExists(manifestPath);
                            return Task.CompletedTask;
                        },
                        cancellationToken);
                    await _eventLog.AppendAsync(
                        "manifest.recovered",
                        manifestId: manifest.ManifestId,
                        detail: "delete",
                        cancellationToken: cancellationToken);
                    completed++;
                    break;

                default:
                    await MoveManifestToFailedAsync(manifestPath, cancellationToken);
                    await _eventLog.AppendAsync(
                        "manifest.failed",
                        manifestId: manifest.ManifestId,
                        detail: "unsupported-operation",
                        cancellationToken: cancellationToken);
                    failed++;
                    break;
            }
        }

        var deletedOrphanedStagedWrites = DeleteOrphanedStagedWrites(ReadPendingManifestIds(pendingDirectory));
        return new JsonColdStoreRecoveryResult(completed, failed, deletedOrphanedStagedWrites);
    }

    private static HashSet<Guid> ReadPendingManifestIds(string pendingDirectory)
    {
        if (!Directory.Exists(pendingDirectory))
            return [];

        return Directory.EnumerateFiles(pendingDirectory, "*.json")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Where(name => Guid.TryParseExact(name, "N", out _))
            .Select(name => Guid.ParseExact(name, "N"))
            .ToHashSet();
    }

    private int DeleteOrphanedStagedWrites(IReadOnlySet<Guid> activeManifestIds)
    {
        var stagedDirectory = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            "_transactions",
            "staged");
        if (!Directory.Exists(stagedDirectory))
            return 0;

        var deleted = 0;
        foreach (var stagedPath in Directory.EnumerateFiles(stagedDirectory, "*.jcs"))
        {
            var stagedName = Path.GetFileNameWithoutExtension(stagedPath);
            if (Guid.TryParseExact(stagedName, "N", out var manifestId)
                && activeManifestIds.Contains(manifestId))
            {
                continue;
            }

            if (JsonColdStoreDirectoryWalker.IsReparsePoint(stagedPath))
                continue;

            File.Delete(stagedPath);
            deleted++;
        }

        return deleted;
    }

    private bool StagedPayloadExists(JsonColdStoreWriteManifest manifest)
    {
        var stagedPath = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            [.. manifest.StagedPathSegments!]);
        return File.Exists(stagedPath);
    }

    private void PublishStagedWrite(JsonColdStoreWriteManifest manifest)
    {
        if (manifest.StagedPathSegments is null)
            throw new InvalidDataException("The write manifest does not contain a staged payload path.");

        var stagedPath = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            [.. manifest.StagedPathSegments]);
        var targetPath = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            [.. manifest.TargetPathSegments]);

        JsonColdStoreFileGuard.ThrowIfReparsePoint(
            stagedPath,
            "The staged payload cannot be a reparse point.");
        JsonColdStoreFileGuard.ThrowIfReparsePoint(
            targetPath,
            "The target record cannot be a reparse point.");

        CreateSafeTargetDirectory(manifest.TargetPathSegments);
        File.Move(stagedPath, targetPath, overwrite: true);
    }

    private void DeleteStagedIfExists(JsonColdStoreWriteManifest manifest)
    {
        if (manifest.StagedPathSegments is null)
            return;

        DeleteIfExists(manifest.StagedPathSegments);
    }

    private byte[] EncodeManifest(JsonColdStoreWriteManifest manifest)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJsonOptions);
        return _protectManifests
            ? JsonColdStorePayloadCodec.Encode(json, _options)
            : json;
    }

    private JsonColdStoreWriteManifest? DecodeManifest(ReadOnlySpan<byte> bytes)
    {
        var json = _protectManifests
            ? JsonColdStorePayloadCodec.Decode(bytes, _options)
            : bytes.ToArray();

        return JsonSerializer.Deserialize<JsonColdStoreWriteManifest>(
            json,
            ManifestJsonOptions);
    }

    private Task ExecuteReplayAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken) =>
        JsonColdStoreRetryPolicy.ExecuteAsync(
            _options.TransactionReplay,
            operation,
            IsTransientReplayException,
            cancellationToken);

    private Task<TResult> ExecuteReplayAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken) =>
        JsonColdStoreRetryPolicy.ExecuteAsync(
            _options.TransactionReplay,
            operation,
            IsTransientReplayException,
            cancellationToken);

    internal static bool IsTransientReplayException(Exception exception) =>
        exception is IOException
        || (exception is UnauthorizedAccessException and not JsonColdStoreUnsafePathException);

    private void CreateSafeTargetDirectory(IReadOnlyList<string> targetPathSegments)
    {
        if (targetPathSegments.Count <= 1)
        {
            JsonColdStoreDirectoryGuard.CreateDirectory(_options.DatabaseDirectory);
            return;
        }

        JsonColdStoreDirectoryGuard.CreateDirectory(
            _options.DatabaseDirectory,
            [.. targetPathSegments.Take(targetPathSegments.Count - 1)]);
    }

    private void RequireStorageMutations()
    {
        if (!_allowStorageMutations)
        {
            throw new InvalidOperationException(
                "This JSONColdStore session does not own the writer lock and cannot mutate storage.");
        }
    }

    internal static string[] GetRecordPathSegments(string entityName, string recordId) =>
    [
        "entities",
        JsonColdStoreNameEncoder.EncodePathSegment(entityName),
        "records",
        JsonColdStoreNameEncoder.EncodePathSegment(recordId) + ".jcs",
    ];

    internal static string[] GetPendingManifestPathSegments(Guid manifestId) =>
    [
        "_transactions",
        "pending",
        manifestId.ToString("N") + ".json",
    ];

    internal static string[] GetStagedWritePathSegments(Guid manifestId) =>
    [
        "_transactions",
        "staged",
        manifestId.ToString("N") + ".jcs",
    ];

    private void DeleteIfExists(IEnumerable<string> pathSegments)
    {
        var path = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            [.. pathSegments]);

        DeleteFileIfExists(path);
    }

    private Task MoveManifestToFailedAsync(string manifestPath, CancellationToken cancellationToken)
    {
        return ExecuteReplayAsync(
            _ =>
            {
                MoveManifestToFailed(manifestPath);
                return Task.CompletedTask;
            },
            cancellationToken);
    }

    private void MoveManifestToFailed(string manifestPath)
    {
        var failedDirectory = JsonColdStoreDirectoryGuard.CreateDirectory(
            _options.DatabaseDirectory,
            "_transactions",
            "failed");

        var failedPath = Path.Combine(failedDirectory, Path.GetFileName(manifestPath));
        JsonColdStoreFileGuard.ThrowIfReparsePoint(
            failedPath,
            "The failed manifest target cannot be a reparse point.");
        File.Move(manifestPath, failedPath, overwrite: true);
    }

    private void QuarantineRecordPath(string recordPath)
    {
        if (!_allowStorageMutations)
            return;

        if (!File.Exists(recordPath))
            return;

        var quarantineDirectory = JsonColdStoreDirectoryGuard.CreateDirectory(
            _options.DatabaseDirectory,
            "_quarantine",
            "records");

        var quarantinedAt = DateTimeOffset.UtcNow;
        var quarantineFileName =
            quarantinedAt.ToString("yyyyMMddHHmmssfffffff", CultureInfo.InvariantCulture)
            + "-"
            + Guid.NewGuid().ToString("N")
            + "-"
            + Path.GetFileName(recordPath);
        var quarantinePath = Path.Combine(quarantineDirectory, quarantineFileName);
        JsonColdStoreFileGuard.ThrowIfReparsePoint(
            quarantinePath,
            "The quarantine target cannot be a reparse point.");
        File.Move(recordPath, quarantinePath, overwrite: false);
        File.SetLastWriteTimeUtc(quarantinePath, quarantinedAt.UtcDateTime);
        PruneExpiredQuarantineFiles(quarantineDirectory);
    }

    private void PruneExpiredQuarantineFiles(string quarantineDirectory)
    {
        if (_options.Quarantine.Retention < TimeSpan.Zero)
            return;

        var cutoff = DateTimeOffset.UtcNow.Subtract(_options.Quarantine.Retention).UtcDateTime;
        foreach (var file in Directory.EnumerateFiles(quarantineDirectory, "*.jcs"))
        {
            try
            {
                if (JsonColdStoreDirectoryWalker.IsReparsePoint(file))
                    continue;

                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        JsonColdStoreFileGuard.ThrowIfReparsePoint(
            path,
            "The JSONColdStore delete target cannot be a reparse point.");

        if (File.Exists(path))
            File.Delete(path);
    }
}

internal sealed record JsonColdStoreWriteManifest(
    Guid ManifestId,
    DateTimeOffset CreatedAt,
    string[] TargetPathSegments,
    int PayloadLength,
    string[]? StagedPathSegments = null,
    JsonColdStoreManifestOperation Operation = JsonColdStoreManifestOperation.Write)
{
    internal static JsonColdStoreWriteManifest CreateWrite(string[] targetPathSegments, int payloadLength)
    {
        if (payloadLength < 0)
            throw new ArgumentOutOfRangeException(nameof(payloadLength), "Payload length cannot be negative.");

        return new JsonColdStoreWriteManifest(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            targetPathSegments,
            payloadLength,
            null,
            JsonColdStoreManifestOperation.Write);
    }

    internal static JsonColdStoreWriteManifest CreateStagedWrite(string[] targetPathSegments, int payloadLength)
    {
        if (payloadLength < 0)
            throw new ArgumentOutOfRangeException(nameof(payloadLength), "Payload length cannot be negative.");

        var manifestId = Guid.NewGuid();
        return new JsonColdStoreWriteManifest(
            manifestId,
            DateTimeOffset.UtcNow,
            targetPathSegments,
            payloadLength,
            JsonColdStoreRecordStore.GetStagedWritePathSegments(manifestId),
            JsonColdStoreManifestOperation.Write);
    }

    internal static JsonColdStoreWriteManifest CreateDelete(string[] targetPathSegments) =>
        new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            targetPathSegments,
            0,
            Operation: JsonColdStoreManifestOperation.Delete);
}

internal sealed record JsonColdStoreRecoveryResult(
    int CompletedManifests,
    int FailedManifests,
    int DeletedOrphanedStagedWrites = 0,
    int DeletedTemporaryFiles = 0);

internal sealed record JsonColdStoreRecord(string RecordId, byte[] Payload);

internal sealed record JsonColdStoreStartupValidationResult(int VerifiedRecords);

internal sealed record JsonColdStoreRecordRepairResult(
    int VerifiedRecords,
    int QuarantinedRecords);

internal enum JsonColdStoreManifestOperation
{
    Write,
    Delete,
}
