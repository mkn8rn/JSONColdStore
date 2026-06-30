using System.Text.Json;
using System.Text.Json.Serialization;
using EFC.JSONColdStore.Storage;

namespace EFC.JSONColdStore.Infrastructure;

internal sealed class JsonColdStoreEntityRecordStore
{
    private static readonly JsonSerializerOptions EntityWriteJsonOptions = new()
    {
        WriteIndented = false,
    };

    private static readonly JsonSerializerOptions EntityReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(), new NullableGuidConverter() },
    };

    private readonly JsonColdStoreDatabaseSession _session;
    private readonly JsonColdStoreModelDescriptor _modelDescriptor;
    private readonly JsonColdStoreModelCatalog _modelCatalog;
    private readonly JsonColdStoreIndexStore _indexStore;
    private bool _modelCatalogValidatedForReads;
    private bool _modelCatalogValidatedForWrites;

    internal JsonColdStoreEntityRecordStore(
        JsonColdStoreDatabaseSession session,
        JsonColdStoreModelDescriptor modelDescriptor)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _modelDescriptor = modelDescriptor ?? throw new ArgumentNullException(nameof(modelDescriptor));
        _modelCatalog = new JsonColdStoreModelCatalog(
            session.Options,
            session.Metadata.Policy.EncryptionEnabled);
        _indexStore = new JsonColdStoreIndexStore(
            session.Options,
            session.Metadata.Policy.EncryptionEnabled);
    }

    internal async Task WriteEntityAsync<TEntity>(
        TEntity entity,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        await WriteEntityAsync(entity, typeof(TEntity), cancellationToken);
    }

    internal async Task WriteEntityAsync(
        object entity,
        Type entityType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(entityType);

        var descriptor = _modelDescriptor.FindEntity(entityType);
        await EnsureModelCatalogAsync(createIfMissing: true, cancellationToken);
        var recordId = descriptor.CreateRecordIdFromEntity(entity);
        var payload = JsonSerializer.SerializeToUtf8Bytes(entity, entityType, EntityWriteJsonOptions);

        await _session.Records.WriteRecordAsync(
            descriptor.EntityName,
            recordId,
            payload,
            cancellationToken);

        await UpsertIndexesAsync(descriptor, entity, recordId, cancellationToken);
        _session.LegacyRecords.DeleteRecordIfExists(descriptor, recordId);
    }

    internal async Task<TEntity?> ReadEntityAsync<TEntity>(
        object keyValue,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        var descriptor = _modelDescriptor.FindEntity(typeof(TEntity));
        await EnsureModelCatalogAsync(createIfMissing: false, cancellationToken);
        var recordId = descriptor.CreateRecordId(keyValue);
        byte[] payload;
        if (_session.Records.RecordExists(descriptor.EntityName, recordId))
        {
            payload = await _session.Records.ReadRecordAsync(
                descriptor.EntityName,
                recordId,
                cancellationToken);
        }
        else if (_session.LegacyRecords.RecordExists(descriptor, recordId))
        {
            payload = await _session.LegacyRecords.ReadRecordAsync(
                descriptor,
                recordId,
                cancellationToken);
        }
        else
        {
            return null;
        }

        return JsonSerializer.Deserialize<TEntity>(payload, EntityReadJsonOptions);
    }

    internal async Task<IReadOnlyList<TEntity>> ReadEntitiesByIndexAsync<TEntity>(
        string propertyName,
        object indexValue,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        var descriptor = _modelDescriptor.FindEntity(typeof(TEntity));
        var index = descriptor.FindSinglePropertyIndex(propertyName);
        await EnsureModelCatalogAsync(createIfMissing: false, cancellationToken);
        var indexKey = index.CreateIndexKeyFromValues(indexValue);
        var recordIds = await _indexStore.ReadRecordIdsAsync(
            descriptor.EntityName,
            index.StorageName,
            indexKey,
            cancellationToken);
        var results = new List<TEntity>();
        var seenRecordIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var recordId in recordIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_session.Records.RecordExists(descriptor.EntityName, recordId))
                continue;

            var payload = await _session.Records.ReadRecordAsync(
                descriptor.EntityName,
                recordId,
                cancellationToken);
            var entity = JsonSerializer.Deserialize<TEntity>(payload, EntityReadJsonOptions);
            if (entity is not null)
            {
                seenRecordIds.Add(recordId);
                results.Add(entity);
            }
        }

        await AddLegacyIndexResultsAsync(
            descriptor,
            index,
            indexKey,
            indexValue,
            seenRecordIds,
            results,
            cancellationToken);

        return results;
    }

    internal async IAsyncEnumerable<TEntity> ScanEntitiesAsync<TEntity>(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        where TEntity : class
    {
        var descriptor = _modelDescriptor.FindEntity(typeof(TEntity));
        await EnsureModelCatalogAsync(createIfMissing: false, cancellationToken);
        var seenRecordIds = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var payload in _session.Records.ReadAllRecordsAsync(
            descriptor.EntityName,
            cancellationToken))
        {
            var entity = JsonSerializer.Deserialize<TEntity>(payload, EntityReadJsonOptions);
            if (entity is not null)
            {
                seenRecordIds.Add(descriptor.CreateRecordIdFromEntity(entity));
                yield return entity;
            }
        }

        await foreach (var legacyRecord in _session.LegacyRecords.ReadAllRecordsAsync(
            descriptor,
            cancellationToken))
        {
            if (seenRecordIds.Contains(legacyRecord.RecordId))
                continue;

            var entity = JsonSerializer.Deserialize<TEntity>(
                legacyRecord.Payload,
                EntityReadJsonOptions);
            if (entity is not null)
                yield return entity;
        }
    }

    internal async Task<int> RebuildIndexesAsync<TEntity>(
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        var descriptor = _modelDescriptor.FindEntity(typeof(TEntity));
        await EnsureModelCatalogAsync(createIfMissing: true, cancellationToken);
        var bucketsByIndex = descriptor.Indexes.ToDictionary(
            index => index,
            _ => new Dictionary<string, List<string>>(StringComparer.Ordinal));
        var records = 0;

        await foreach (var entity in ScanEntitiesAsync<TEntity>(cancellationToken))
        {
            records++;
            var recordId = descriptor.CreateRecordIdFromEntity(entity);
            foreach (var index in descriptor.Indexes)
            {
                var indexKey = index.CreateIndexKeyFromEntity(entity);
                var buckets = bucketsByIndex[index];
                if (!buckets.TryGetValue(indexKey, out var recordIds))
                {
                    recordIds = [];
                    buckets[indexKey] = recordIds;
                }

                if (!recordIds.Contains(recordId, StringComparer.Ordinal))
                    recordIds.Add(recordId);
            }
        }

        foreach (var (index, buckets) in bucketsByIndex)
        {
            await _indexStore.ReplaceAsync(
                descriptor.EntityName,
                index.StorageName,
                buckets.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<string>)pair.Value,
                    StringComparer.Ordinal),
                cancellationToken);
        }

        return records;
    }

    internal async Task<JsonColdStoreEntityVerificationResult> VerifyEntitiesAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureModelCatalogAsync(createIfMissing: false, cancellationToken);
        var verifiedRecords = 0;
        var verifiedLegacyRecords = 0;

        foreach (var descriptor in _modelDescriptor.Entities)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await foreach (var payload in _session.Records.ReadAllRecordsAsync(
                descriptor.EntityName,
                cancellationToken))
            {
                VerifyPayload(payload, descriptor.ClrType, descriptor.EntityName);
                verifiedRecords++;
            }

            await foreach (var legacyRecord in _session.LegacyRecords.ReadAllRecordsAsync(
                descriptor,
                cancellationToken))
            {
                VerifyPayload(legacyRecord.Payload, descriptor.ClrType, descriptor.EntityName);
                verifiedLegacyRecords++;
            }
        }

        return new JsonColdStoreEntityVerificationResult(
            verifiedRecords,
            verifiedLegacyRecords);
    }

    private static void VerifyPayload(
        byte[] payload,
        Type entityType,
        string entityName)
    {
        var entity = JsonSerializer.Deserialize(payload, entityType, EntityReadJsonOptions);
        if (entity is null)
            throw new InvalidDataException(
                $"The JSONColdStore record for '{entityName}' deserialized to null.");
    }

    internal async Task DeleteEntityAsync(
        object entity,
        Type entityType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(entityType);

        var descriptor = _modelDescriptor.FindEntity(entityType);
        await EnsureModelCatalogAsync(createIfMissing: true, cancellationToken);
        var recordId = descriptor.CreateRecordIdFromEntity(entity);

        await _session.Records.DeleteRecordAsync(
            descriptor.EntityName,
            recordId,
            cancellationToken);

        await RemoveIndexesAsync(descriptor, recordId, cancellationToken);
        _session.LegacyRecords.DeleteRecordIfExists(descriptor, recordId);
    }

    private async Task EnsureModelCatalogAsync(
        bool createIfMissing,
        CancellationToken cancellationToken)
    {
        if (createIfMissing)
        {
            if (_modelCatalogValidatedForWrites)
                return;
        }
        else if (_modelCatalogValidatedForReads || _modelCatalogValidatedForWrites)
        {
            return;
        }

        var catalogExists = await _modelCatalog.EnsureCompatibleAsync(
            _modelDescriptor,
            createIfMissing,
            cancellationToken);

        if (catalogExists)
            _modelCatalogValidatedForReads = true;
        if (createIfMissing)
            _modelCatalogValidatedForWrites = true;
    }

    private async Task UpsertIndexesAsync(
        JsonColdStoreEntityDescriptor descriptor,
        object entity,
        string recordId,
        CancellationToken cancellationToken)
    {
        foreach (var index in descriptor.Indexes)
        {
            await _indexStore.UpsertAsync(
                descriptor.EntityName,
                index.StorageName,
                index.CreateIndexKeyFromEntity(entity),
                recordId,
                cancellationToken);
        }
    }

    private async Task AddLegacyIndexResultsAsync<TEntity>(
        JsonColdStoreEntityDescriptor descriptor,
        JsonColdStoreIndexDescriptor index,
        string indexKey,
        object indexValue,
        HashSet<string> seenRecordIds,
        List<TEntity> results,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var lookup = await _session.LegacyRecords.LookupIndexAsync(
            descriptor,
            index,
            indexKey,
            indexValue,
            cancellationToken);

        if (lookup.UseIndex)
        {
            foreach (var recordId in lookup.RecordIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seenRecordIds.Add(recordId))
                    continue;
                if (!_session.LegacyRecords.RecordExists(descriptor, recordId))
                    continue;

                var payload = await _session.LegacyRecords.ReadRecordAsync(
                    descriptor,
                    recordId,
                    cancellationToken);
                var entity = JsonSerializer.Deserialize<TEntity>(payload, EntityReadJsonOptions);
                if (entity is not null)
                    results.Add(entity);
            }

            return;
        }

        await foreach (var legacyRecord in _session.LegacyRecords.ReadAllRecordsAsync(
            descriptor,
            cancellationToken))
        {
            if (!seenRecordIds.Add(legacyRecord.RecordId))
                continue;

            var entity = JsonSerializer.Deserialize<TEntity>(
                legacyRecord.Payload,
                EntityReadJsonOptions);
            if (entity is not null && string.Equals(
                    index.CreateIndexKeyFromEntity(entity),
                    indexKey,
                    StringComparison.Ordinal))
            {
                results.Add(entity);
            }
        }
    }

    private sealed class NullableGuidConverter : JsonConverter<Guid>
    {
        public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? Guid.Empty : reader.GetGuid();

        public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }

    private async Task RemoveIndexesAsync(
        JsonColdStoreEntityDescriptor descriptor,
        string recordId,
        CancellationToken cancellationToken)
    {
        foreach (var index in descriptor.Indexes)
        {
            await _indexStore.RemoveAsync(
                descriptor.EntityName,
                index.StorageName,
                recordId,
                cancellationToken);
        }
    }
}

internal sealed record JsonColdStoreEntityVerificationResult(
    int VerifiedRecords,
    int VerifiedLegacyRecords);
