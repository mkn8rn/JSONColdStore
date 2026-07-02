using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EFC.JSONColdStore.Infrastructure;

namespace EFC.JSONColdStore.Storage;

internal sealed class JsonColdStoreLegacyRecordStore
{
    private const int LegacyEnvelopeVersion = 0x01;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int MinimumEncryptedEnvelopeLength = 1 + NonceLength + TagLength;
    private const string SharedRowsFileName = "_rows.json";

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private static readonly JsonSerializerOptions IndexJsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions SharedRowsJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
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

        if (descriptor.IsSharedType)
        {
            await foreach (var row in ReadAllSharedRowsAsync(
                descriptor,
                cancellationToken))
            {
                yield return new JsonColdStoreLegacyRecord(row.RecordId, row.Payload);
            }

            yield break;
        }

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

    internal bool SharedRowsDocumentExists(JsonColdStoreEntityDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor.IsSharedType && File.Exists(GetSharedRowsPath(descriptor));
    }

    internal async IAsyncEnumerable<JsonColdStoreLegacySharedRow> ReadAllSharedRowsAsync(
        JsonColdStoreEntityDescriptor descriptor,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!descriptor.IsSharedType)
            yield break;

        var rowsPath = GetSharedRowsPath(descriptor);
        if (!File.Exists(rowsPath))
            yield break;

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = await JsonColdStoreFileReader.ReadAllBytesAsync(_options, rowsPath, cancellationToken);
        var payload = Decode(bytes);

        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"The legacy JSONColdStore shared rows document for '{descriptor.EntityName}' must be a JSON array.");
        }

        var rowIndex = 0;
        foreach (var row in document.RootElement.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowIndex++;
            var entity = CreateSharedRowEntity(descriptor, row, rowIndex);
            var recordId = descriptor.CreateRecordIdFromEntity(entity);
            var normalizedPayload = JsonSerializer.SerializeToUtf8Bytes(entity, SharedRowsJsonOptions);
            yield return new JsonColdStoreLegacySharedRow(recordId, entity, normalizedPayload);
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
        JsonColdStoreFileGuard.ThrowIfReparsePoint(
            recordPath,
            "The legacy record delete target cannot be a reparse point.");
        if (File.Exists(recordPath))
            File.Delete(recordPath);
    }

    internal void DeleteSharedRowsIfExists(JsonColdStoreEntityDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!descriptor.IsSharedType)
            return;

        var rowsPath = GetSharedRowsPath(descriptor);
        JsonColdStoreFileGuard.ThrowIfReparsePoint(
            rowsPath,
            "The legacy shared rows delete target cannot be a reparse point.");
        if (File.Exists(rowsPath))
            File.Delete(rowsPath);
    }

    private static Dictionary<string, object?> CreateSharedRowEntity(
        JsonColdStoreEntityDescriptor descriptor,
        JsonElement row,
        int rowIndex)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Legacy JSONColdStore shared row {rowIndex} for '{descriptor.EntityName}' must be a JSON object.");
        }

        var entity = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in descriptor.Properties)
        {
            if (!TryGetSharedRowProperty(row, property.Name, out var value))
            {
                throw new InvalidDataException(
                    $"Legacy JSONColdStore shared row {rowIndex} for '{descriptor.EntityName}' is missing '{property.Name}'.");
            }

            entity[property.Name] = ConvertSharedRowValue(
                value,
                property.ClrType,
                descriptor.EntityName,
                property.Name,
                rowIndex);
        }

        return entity;
    }

    private static bool TryGetSharedRowProperty(JsonElement row, string propertyName, out JsonElement value)
    {
        if (row.TryGetProperty(propertyName, out value))
            return true;

        foreach (var property in row.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static object? ConvertSharedRowValue(
        JsonElement value,
        Type propertyType,
        string entityName,
        string propertyName,
        int rowIndex)
    {
        var targetType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (Nullable.GetUnderlyingType(propertyType) is not null || !propertyType.IsValueType)
                return null;

            throw new InvalidDataException(
                $"Legacy JSONColdStore shared row {rowIndex} for '{entityName}' has null for non-null '{propertyName}'.");
        }

        try
        {
            if (targetType == typeof(string))
                return value.GetString();

            if (targetType == typeof(Guid))
            {
                return value.ValueKind == JsonValueKind.String
                    ? value.GetGuid()
                    : JsonSerializer.Deserialize<Guid>(value.GetRawText(), SharedRowsJsonOptions);
            }

            if (targetType == typeof(DateTime))
            {
                return value.ValueKind == JsonValueKind.String
                    ? value.GetDateTime()
                    : JsonSerializer.Deserialize<DateTime>(value.GetRawText(), SharedRowsJsonOptions);
            }

            if (targetType == typeof(DateTimeOffset))
            {
                return value.ValueKind == JsonValueKind.String
                    ? value.GetDateTimeOffset()
                    : JsonSerializer.Deserialize<DateTimeOffset>(value.GetRawText(), SharedRowsJsonOptions);
            }

            if (targetType.IsEnum)
            {
                return value.ValueKind == JsonValueKind.String
                    ? Enum.Parse(targetType, value.GetString()!, ignoreCase: true)
                    : Enum.ToObject(targetType, value.GetInt32());
            }

            return JsonSerializer.Deserialize(value.GetRawText(), targetType, SharedRowsJsonOptions);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or JsonException)
        {
            throw new InvalidDataException(
                $"Legacy JSONColdStore shared row {rowIndex} for '{entityName}' has invalid '{propertyName}'.",
                ex);
        }
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
            GetLegacyEntityDirectoryName(descriptor),
            recordId + ".json");
    }

    private string GetEntityDirectory(JsonColdStoreEntityDescriptor descriptor) =>
        JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            GetLegacyEntityDirectoryName(descriptor));

    private string GetSharedRowsPath(JsonColdStoreEntityDescriptor descriptor) =>
        JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            descriptor.EntityName,
            SharedRowsFileName);

    private static string GetLegacyEntityDirectoryName(JsonColdStoreEntityDescriptor descriptor) =>
        descriptor.IsSharedType
            ? descriptor.EntityName
            : descriptor.ClrType.Name;
}

internal sealed record JsonColdStoreLegacyRecord(string RecordId, byte[] Payload);

internal sealed record JsonColdStoreLegacySharedRow(
    string RecordId,
    IReadOnlyDictionary<string, object?> Entity,
    byte[] Payload);

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
