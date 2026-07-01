using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using EFC.JSONColdStore.Infrastructure;

namespace EFC.JSONColdStore.Storage;

internal sealed class JsonColdStoreLegacyRecordStore
{
    private const int LegacyEnvelopeVersion = 0x01;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int MinimumEncryptedEnvelopeLength = 1 + NonceLength + TagLength;

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private static readonly JsonSerializerOptions IndexJsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly JsonColdStoreOptions _options;

    internal JsonColdStoreLegacyRecordStore(JsonColdStoreOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    internal bool RecordExists(JsonColdStoreEntityDescriptor descriptor, string recordId)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!JsonColdStoreLegacyRecordNames.IsSafeRecordId(recordId))
            return false;

        return File.Exists(GetRecordPath(descriptor, recordId));
    }

    internal async Task<byte[]> ReadRecordAsync(
        JsonColdStoreEntityDescriptor descriptor,
        string recordId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateLegacyRecordId(recordId);

        var bytes = await JsonColdStoreFileReader.ReadAllBytesAsync(
            _options,
            GetRecordPath(descriptor, recordId),
            cancellationToken);
        return Decode(bytes);
    }

    internal async IAsyncEnumerable<JsonColdStoreLegacyRecord> ReadAllRecordsAsync(
        JsonColdStoreEntityDescriptor descriptor,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var entityDirectory = GetEntityDirectory(descriptor);
        if (!Directory.Exists(entityDirectory))
            yield break;

        foreach (var file in Directory.EnumerateFiles(entityDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!JsonColdStoreLegacyRecordNames.TryGetRecordIdFromFileName(
                    Path.GetFileName(file),
                    out var recordId))
            {
                continue;
            }

            var bytes = await JsonColdStoreFileReader.ReadAllBytesAsync(_options, file, cancellationToken);
            yield return new JsonColdStoreLegacyRecord(
                recordId,
                Decode(bytes));
        }
    }

    internal async Task<JsonColdStoreLegacyIndexLookup> LookupIndexAsync(
        JsonColdStoreEntityDescriptor descriptor,
        JsonColdStoreIndexDescriptor index,
        string indexKey,
        object indexValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(index);

        var entityDirectory = GetEntityDirectory(descriptor);
        if (!Directory.Exists(entityDirectory))
            return JsonColdStoreLegacyIndexLookup.FallbackToScan;

        var keyScoped = await TryReadKeyScopedIndexAsync(
            entityDirectory,
            index.StorageName,
            indexValue,
            cancellationToken);
        if (keyScoped is not null)
            return keyScoped;

        var shardPath = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            descriptor.ClrType.Name,
            $"_index_{index.StorageName}.json");
        if (!File.Exists(shardPath))
            return JsonColdStoreLegacyIndexLookup.FallbackToScan;

        Dictionary<string, List<string>>? shard;
        try
        {
            var bytes = await JsonColdStoreFileReader.ReadAllBytesAsync(_options, shardPath, cancellationToken);
            shard = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
                bytes,
                IndexJsonOptions);
        }
        catch (Exception ex) when (IsRecoverableLegacyIndexReadFailure(ex))
        {
            return JsonColdStoreLegacyIndexLookup.FallbackToScan;
        }

        if (shard is not null && shard.TryGetValue(indexKey, out var recordIds))
            return CreateLookupFromIndex(recordIds);

        return JsonColdStoreLegacyIndexLookup.FallbackToScan;
    }

    internal void DeleteRecordIfExists(JsonColdStoreEntityDescriptor descriptor, string recordId)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!JsonColdStoreLegacyRecordNames.IsSafeRecordId(recordId))
            return;

        var recordPath = GetRecordPath(descriptor, recordId);
        if (File.Exists(recordPath))
            File.Delete(recordPath);
    }

    private async Task<JsonColdStoreLegacyIndexLookup?> TryReadKeyScopedIndexAsync(
        string entityDirectory,
        string indexName,
        object indexValue,
        CancellationToken cancellationToken)
    {
        var keyScopedDirectory = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            Path.GetFileName(entityDirectory),
            $"_index_{indexName}");
        if (!Directory.Exists(keyScopedDirectory))
            return null;

        var key = Convert.ToString(indexValue, System.Globalization.CultureInfo.InvariantCulture);
        if (!Guid.TryParse(key, out var guidKey))
            return JsonColdStoreLegacyIndexLookup.FromIndex([]);

        var shardPath = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            Path.GetFileName(entityDirectory),
            $"_index_{indexName}",
            $"{guidKey:D}.json");
        if (!File.Exists(shardPath))
            return JsonColdStoreLegacyIndexLookup.FromIndex([]);

        List<string>? recordIds;
        try
        {
            var bytes = await JsonColdStoreFileReader.ReadAllBytesAsync(_options, shardPath, cancellationToken);
            recordIds = JsonSerializer.Deserialize<List<string>>(
                bytes,
                IndexJsonOptions);
        }
        catch (Exception ex) when (IsRecoverableLegacyIndexReadFailure(ex))
        {
            return JsonColdStoreLegacyIndexLookup.FallbackToScan;
        }

        return CreateLookupFromIndex(recordIds ?? []);
    }

    private static bool IsRecoverableLegacyIndexReadFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException;

    private static JsonColdStoreLegacyIndexLookup CreateLookupFromIndex(IEnumerable<string> recordIds)
    {
        var safeRecordIds = new List<string>();
        foreach (var recordId in recordIds)
        {
            if (!JsonColdStoreLegacyRecordNames.IsSafeRecordId(recordId))
                return JsonColdStoreLegacyIndexLookup.FallbackToScan;

            safeRecordIds.Add(recordId);
        }

        return JsonColdStoreLegacyIndexLookup.FromIndex(safeRecordIds);
    }

    private static void ValidateLegacyRecordId(string recordId)
    {
        if (!JsonColdStoreLegacyRecordNames.IsSafeRecordId(recordId))
            throw new InvalidDataException("The legacy JSONColdStore record id is not a safe file name.");
    }

    private byte[] Decode(ReadOnlySpan<byte> bytes)
    {
        byte[] plaintext;
        if (bytes.Length >= MinimumEncryptedEnvelopeLength && bytes[0] == LegacyEnvelopeVersion)
        {
            if (_options.Encryption is null)
                throw new InvalidOperationException("An encryption key is required to read this legacy JSON payload.");

            plaintext = Decrypt(bytes);
        }
        else
        {
            if (_options.Encryption?.RequireEncryptedStore == true)
            {
                throw new InvalidOperationException(
                    "The store requires encrypted legacy JSON payloads.");
            }

            plaintext = bytes.ToArray();
        }

        return StripUtf8Bom(plaintext);
    }

    private byte[] Decrypt(ReadOnlySpan<byte> envelope)
    {
        var keyBytes = _options.Encryption!.Key.CopyBytes();
        try
        {
            var nonce = envelope.Slice(1, NonceLength);
            var ciphertext = envelope.Slice(1 + NonceLength, envelope.Length - 1 - NonceLength - TagLength);
            var tag = envelope[^TagLength..];
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(keyBytes, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    private static byte[] StripUtf8Bom(byte[] bytes)
    {
        return bytes.AsSpan().StartsWith(Utf8Bom)
            ? bytes[Utf8Bom.Length..]
            : bytes;
    }

    private string GetRecordPath(JsonColdStoreEntityDescriptor descriptor, string recordId)
    {
        if (string.IsNullOrWhiteSpace(recordId))
            throw new ArgumentException("A record id is required.", nameof(recordId));

        return JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            descriptor.ClrType.Name,
            recordId + ".json");
    }

    private string GetEntityDirectory(JsonColdStoreEntityDescriptor descriptor) =>
        JsonColdStorePathValidator.GetSafeChildPath(_options.DatabaseDirectory, descriptor.ClrType.Name);
}

internal sealed record JsonColdStoreLegacyRecord(string RecordId, byte[] Payload);

internal sealed record JsonColdStoreLegacyIndexLookup(bool UseIndex, IReadOnlyList<string> RecordIds)
{
    internal static JsonColdStoreLegacyIndexLookup FallbackToScan { get; } = new(false, []);

    internal static JsonColdStoreLegacyIndexLookup FromIndex(IEnumerable<string> recordIds) =>
        new(
            true,
            recordIds
                .Where(recordId => !string.IsNullOrWhiteSpace(recordId))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray());
}
