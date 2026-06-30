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

    internal JsonColdStoreRecordStore(JsonColdStoreOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    internal async Task WriteRecordAsync(
        string entityName,
        string recordId,
        ReadOnlyMemory<byte> utf8Json,
        CancellationToken cancellationToken = default)
    {
        var recordPath = GetRecordPathSegments(entityName, recordId);
        var payload = JsonColdStorePayloadCodec.Encode(utf8Json.Span, _options);
        var manifest = JsonColdStoreWriteManifest.Create(recordPath, payload.Length);
        var manifestPath = GetPendingManifestPathSegments(manifest.ManifestId);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJsonOptions);

        await JsonColdStoreAtomicFileWriter.WriteAsync(
            _options.DatabaseDirectory,
            manifestPath,
            manifestBytes,
            _options.FsyncOnWrite,
            cancellationToken);

        try
        {
            await JsonColdStoreAtomicFileWriter.WriteAsync(
                _options.DatabaseDirectory,
                recordPath,
                payload,
                _options.FsyncOnWrite,
                cancellationToken);

            DeleteIfExists(manifestPath);
        }
        catch
        {
            throw;
        }
    }

    internal async Task<byte[]> ReadRecordAsync(
        string entityName,
        string recordId,
        CancellationToken cancellationToken = default)
    {
        var payload = await JsonColdStoreAtomicFileWriter.ReadAsync(
            _options.DatabaseDirectory,
            GetRecordPathSegments(entityName, recordId),
            cancellationToken);

        return JsonColdStorePayloadCodec.Decode(payload, _options);
    }

    internal bool RecordExists(string entityName, string recordId)
    {
        var path = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            [.. GetRecordPathSegments(entityName, recordId)]);

        return File.Exists(path);
    }

    internal async Task<JsonColdStoreRecoveryResult> RecoverPendingManifestsAsync(
        CancellationToken cancellationToken = default)
    {
        var pendingDirectory = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            "_transactions",
            "pending");

        if (!Directory.Exists(pendingDirectory))
            return new JsonColdStoreRecoveryResult(0, 0);

        var completed = 0;
        var failed = 0;
        foreach (var manifestPath in Directory.GetFiles(pendingDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            JsonColdStoreWriteManifest? manifest;
            try
            {
                await using var stream = File.OpenRead(manifestPath);
                manifest = await JsonSerializer.DeserializeAsync<JsonColdStoreWriteManifest>(
                    stream,
                    ManifestJsonOptions,
                    cancellationToken);
            }
            catch
            {
                MoveManifestToFailed(manifestPath);
                failed++;
                continue;
            }

            if (manifest is null)
            {
                MoveManifestToFailed(manifestPath);
                failed++;
                continue;
            }

            var targetPath = JsonColdStorePathValidator.GetSafeChildPath(
                _options.DatabaseDirectory,
                [.. manifest.TargetPathSegments]);

            if (File.Exists(targetPath))
            {
                File.Delete(manifestPath);
                completed++;
            }
            else
            {
                MoveManifestToFailed(manifestPath);
                failed++;
            }
        }

        return new JsonColdStoreRecoveryResult(completed, failed);
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

    private void DeleteIfExists(IEnumerable<string> pathSegments)
    {
        var path = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            [.. pathSegments]);

        if (File.Exists(path))
            File.Delete(path);
    }

    private void MoveManifestToFailed(string manifestPath)
    {
        var failedDirectory = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            "_transactions",
            "failed");
        Directory.CreateDirectory(failedDirectory);

        var failedPath = Path.Combine(failedDirectory, Path.GetFileName(manifestPath));
        File.Move(manifestPath, failedPath, overwrite: true);
    }
}

internal sealed record JsonColdStoreWriteManifest(
    Guid ManifestId,
    DateTimeOffset CreatedAt,
    string[] TargetPathSegments,
    int PayloadLength)
{
    internal static JsonColdStoreWriteManifest Create(string[] targetPathSegments, int payloadLength)
    {
        if (payloadLength < 0)
            throw new ArgumentOutOfRangeException(nameof(payloadLength), "Payload length cannot be negative.");

        return new JsonColdStoreWriteManifest(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            targetPathSegments,
            payloadLength);
    }
}

internal sealed record JsonColdStoreRecoveryResult(int CompletedManifests, int FailedManifests);
