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
    private readonly bool _protectManifests;

    internal JsonColdStoreRecordStore(JsonColdStoreOptions options, bool protectManifests = false)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _protectManifests = protectManifests;
    }

    internal async Task WriteRecordAsync(
        string entityName,
        string recordId,
        ReadOnlyMemory<byte> utf8Json,
        CancellationToken cancellationToken = default)
    {
        var recordPath = GetRecordPathSegments(entityName, recordId);
        var payload = JsonColdStorePayloadCodec.Encode(utf8Json.Span, _options);
        var manifest = JsonColdStoreWriteManifest.CreateWrite(recordPath, payload.Length);
        var manifestPath = GetPendingManifestPathSegments(manifest.ManifestId);
        var manifestBytes = EncodeManifest(manifest);

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

    internal async Task DeleteRecordAsync(
        string entityName,
        string recordId,
        CancellationToken cancellationToken = default)
    {
        var recordPath = GetRecordPathSegments(entityName, recordId);
        var manifest = JsonColdStoreWriteManifest.CreateDelete(recordPath);
        var manifestPath = GetPendingManifestPathSegments(manifest.ManifestId);
        var manifestBytes = EncodeManifest(manifest);

        await JsonColdStoreAtomicFileWriter.WriteAsync(
            _options.DatabaseDirectory,
            manifestPath,
            manifestBytes,
            _options.FsyncOnWrite,
            cancellationToken);

        var targetPath = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            [.. recordPath]);

        if (File.Exists(targetPath))
            File.Delete(targetPath);

        DeleteIfExists(manifestPath);
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
            _options.DatabaseDirectory,
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

        return File.Exists(path);
    }

    internal async IAsyncEnumerable<byte[]> ReadAllRecordsAsync(
        string entityName,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var recordsDirectory = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            "entities",
            JsonColdStoreNameEncoder.EncodePathSegment(entityName),
            "records");

        if (!Directory.Exists(recordsDirectory))
            yield break;

        foreach (var recordPath in Directory.EnumerateFiles(recordsDirectory, "*.jcs").Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = await File.ReadAllBytesAsync(recordPath, cancellationToken);
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

            yield return decoded;
        }
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
                var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
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

            switch (manifest.Operation)
            {
                case JsonColdStoreManifestOperation.Write when File.Exists(targetPath):
                    File.Delete(manifestPath);
                    completed++;
                    break;

                case JsonColdStoreManifestOperation.Write:
                    MoveManifestToFailed(manifestPath);
                    failed++;
                    break;

                case JsonColdStoreManifestOperation.Delete:
                    if (File.Exists(targetPath))
                        File.Delete(targetPath);

                    File.Delete(manifestPath);
                    completed++;
                    break;

                default:
                    MoveManifestToFailed(manifestPath);
                    failed++;
                    break;
            }
        }

        return new JsonColdStoreRecoveryResult(completed, failed);
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

    private void QuarantineRecordPath(string recordPath)
    {
        if (!File.Exists(recordPath))
            return;

        var quarantineDirectory = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            "_quarantine",
            "records");
        Directory.CreateDirectory(quarantineDirectory);

        var quarantineFileName =
            DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfffffff", CultureInfo.InvariantCulture)
            + "-"
            + Guid.NewGuid().ToString("N")
            + "-"
            + Path.GetFileName(recordPath);
        File.Move(recordPath, Path.Combine(quarantineDirectory, quarantineFileName), overwrite: false);
    }
}

internal sealed record JsonColdStoreWriteManifest(
    Guid ManifestId,
    DateTimeOffset CreatedAt,
    string[] TargetPathSegments,
    int PayloadLength,
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
            JsonColdStoreManifestOperation.Write);
    }

    internal static JsonColdStoreWriteManifest CreateDelete(string[] targetPathSegments) =>
        new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            targetPathSegments,
            0,
            JsonColdStoreManifestOperation.Delete);
}

internal sealed record JsonColdStoreRecoveryResult(int CompletedManifests, int FailedManifests);

internal enum JsonColdStoreManifestOperation
{
    Write,
    Delete,
}
