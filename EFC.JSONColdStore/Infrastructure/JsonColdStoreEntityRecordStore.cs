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
        await WriteEntityAsync(
            entity,
            entityType,
            ignoredRecordIds: null,
            cancellationToken);
    }

    internal async Task WriteEntityAsync(
        object entity,
        Type entityType,
        IReadOnlySet<string>? ignoredRecordIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(entityType);

        var descriptor = _modelDescriptor.FindEntity(entityType);
        await EnsureModelCatalogAsync(createIfMissing: true, cancellationToken);
        var recordId = descriptor.CreateRecordIdFromEntity(entity);
        await EnsureUniqueIndexesAsync(
            descriptor,
            entity,
            recordId,
            ignoredRecordIds,
            cancellationToken);
        var payload = JsonSerializer.SerializeToUtf8Bytes(entity, entityType, EntityWriteJsonOptions);

        await _session.Records.WriteRecordAsync(
            descriptor.EntityName,
            recordId,
            payload,
            cancellationToken);

        await UpsertIndexesAsync(descriptor, entity, recordId, cancellationToken);
        _session.LegacyRecords.DeleteRecordIfExists(descriptor, recordId);
    }

    internal async Task ValidateUniqueIndexesAsync(
        object entity,
        Type entityType,
        IReadOnlySet<string>? ignoredRecordIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(entityType);

        var descriptor = _modelDescriptor.FindEntity(entityType);
        await EnsureModelCatalogAsync(createIfMissing: true, cancellationToken);
        var recordId = descriptor.CreateRecordIdFromEntity(entity);
        await EnsureUniqueIndexesAsync(
            descriptor,
            entity,
            recordId,
            ignoredRecordIds,
            cancellationToken);
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
        CancellationToken cancellationToken = default,
        int? maxResults = null)
        where TEntity : class
    {
        var descriptor = _modelDescriptor.FindEntity(typeof(TEntity));
        var index = descriptor.FindSinglePropertyIndex(propertyName);
        await EnsureModelCatalogAsync(createIfMissing: false, cancellationToken);
        if (maxResults <= 0)
            return [];

        EnsureCurrentIndexAvailable(descriptor, index);

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
                if (HasReachedLimit(results, maxResults))
                    return results;
            }
        }

        await AddLegacyIndexResultsAsync(
            descriptor,
            index,
            indexKey,
            indexValue,
            seenRecordIds,
            results,
            maxResults,
            cancellationToken);

        return results;
    }

    internal async Task<IReadOnlyList<TEntity>> ReadEntitiesByIndexedPropertyAsync<TEntity>(
        string propertyName,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        var descriptor = _modelDescriptor.FindEntity(typeof(TEntity));
        var index = descriptor.FindSinglePropertyIndex(propertyName);
        await EnsureModelCatalogAsync(createIfMissing: false, cancellationToken);
        EnsureCurrentIndexAvailable(descriptor, index);

        var recordIds = await _indexStore.ReadAllRecordIdsAsync(
            descriptor.EntityName,
            index.StorageName,
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

        await foreach (var legacyRecord in _session.LegacyRecords.ReadAllRecordsAsync(
            descriptor,
            cancellationToken))
        {
            if (!seenRecordIds.Add(legacyRecord.RecordId))
                continue;

            var entity = JsonSerializer.Deserialize<TEntity>(
                legacyRecord.Payload,
                EntityReadJsonOptions);
            if (entity is not null)
                results.Add(entity);
        }

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
        return await RebuildIndexesAsync(descriptor, cancellationToken);
    }

    internal async Task<int> RebuildIndexesAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureModelCatalogAsync(createIfMissing: true, cancellationToken);
        var records = 0;

        foreach (var descriptor in _modelDescriptor.Entities)
            records += await RebuildIndexesAsync(descriptor, cancellationToken);

        return records;
    }

    private async Task<int> RebuildIndexesAsync(
        JsonColdStoreEntityDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var bucketsByIndex = descriptor.Indexes.ToDictionary(
            index => index,
            _ => new Dictionary<string, List<string>>(StringComparer.Ordinal));
        var records = 0;
        var seenRecordIds = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var payload in _session.Records.ReadAllRecordsAsync(
            descriptor.EntityName,
            cancellationToken))
        {
            var entity = JsonSerializer.Deserialize(payload, descriptor.ClrType, EntityReadJsonOptions);
            if (entity is null)
                continue;

            var recordId = descriptor.CreateRecordIdFromEntity(entity);
            seenRecordIds.Add(recordId);
            records++;
            AddEntityToIndexBuckets(descriptor, bucketsByIndex, entity, recordId);
        }

        await foreach (var legacyRecord in _session.LegacyRecords.ReadAllRecordsAsync(
            descriptor,
            cancellationToken))
        {
            if (!seenRecordIds.Add(legacyRecord.RecordId))
                continue;

            var entity = JsonSerializer.Deserialize(
                legacyRecord.Payload,
                descriptor.ClrType,
                EntityReadJsonOptions);
            if (entity is null)
                continue;

            records++;
            AddEntityToIndexBuckets(descriptor, bucketsByIndex, entity, legacyRecord.RecordId);
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

    private static void AddEntityToIndexBuckets(
        JsonColdStoreEntityDescriptor descriptor,
        Dictionary<JsonColdStoreIndexDescriptor, Dictionary<string, List<string>>> bucketsByIndex,
        object entity,
        string recordId)
    {
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

    internal async Task<JsonColdStoreEntityVerificationResult> VerifyEntitiesAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureModelCatalogAsync(createIfMissing: false, cancellationToken);
        var verifiedRecords = 0;
        var verifiedLegacyRecords = 0;
        var verifiedIndexes = 0;

        foreach (var descriptor in _modelDescriptor.Entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentRecords = new List<JsonColdStoreVerifiedEntityRecord>();

            await foreach (var record in _session.Records.ReadAllNamedRecordsAsync(
                descriptor.EntityName,
                cancellationToken))
            {
                var entity = VerifyPayload(record.Payload, descriptor.ClrType, descriptor.EntityName);
                currentRecords.Add(new JsonColdStoreVerifiedEntityRecord(record.RecordId, entity));
                verifiedRecords++;
            }

            await foreach (var legacyRecord in _session.LegacyRecords.ReadAllRecordsAsync(
                descriptor,
                cancellationToken))
            {
                _ = VerifyPayload(legacyRecord.Payload, descriptor.ClrType, descriptor.EntityName);
                verifiedLegacyRecords++;
            }

            verifiedIndexes += await VerifyIndexesAsync(descriptor, currentRecords, cancellationToken);
        }

        return new JsonColdStoreEntityVerificationResult(
            verifiedRecords,
            verifiedLegacyRecords,
            verifiedIndexes);
    }

    private async Task<int> VerifyIndexesAsync(
        JsonColdStoreEntityDescriptor descriptor,
        IReadOnlyList<JsonColdStoreVerifiedEntityRecord> currentRecords,
        CancellationToken cancellationToken)
    {
        var verifiedIndexes = 0;
        var currentRecordIds = currentRecords
            .Select(record => record.RecordId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var index in descriptor.Indexes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_indexStore.DocumentExists(descriptor.EntityName, index.StorageName))
            {
                if (currentRecords.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"The JSONColdStore index '{index.StorageName}' for entity '{descriptor.EntityName}' is missing. "
                        + "Rebuild JSONColdStore indexes before verification can complete.");
                }

                continue;
            }

            var actualBuckets = await _indexStore.ReadBucketsAsync(
                descriptor.EntityName,
                index.StorageName,
                cancellationToken);

            foreach (var recordId in actualBuckets.Values.SelectMany(recordIds => recordIds).Distinct(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (currentRecordIds.Contains(recordId)
                    || _session.LegacyRecords.RecordExists(descriptor, recordId))
                {
                    continue;
                }

                throw new InvalidDataException(
                    $"The JSONColdStore index '{index.StorageName}' for entity '{descriptor.EntityName}' "
                    + $"references missing record '{recordId}'.");
            }

            var expectedBuckets = CreateExpectedIndexBuckets(index, currentRecords);
            if (!IndexBucketsEqual(expectedBuckets, actualBuckets))
            {
                throw new InvalidDataException(
                    $"The JSONColdStore index '{index.StorageName}' for entity '{descriptor.EntityName}' "
                    + "does not match current record values. Rebuild JSONColdStore indexes before verification can complete.");
            }

            verifiedIndexes++;
        }

        return verifiedIndexes;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> CreateExpectedIndexBuckets(
        JsonColdStoreIndexDescriptor index,
        IReadOnlyList<JsonColdStoreVerifiedEntityRecord> currentRecords)
    {
        var buckets = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var record in currentRecords)
        {
            var indexKey = index.CreateIndexKeyFromEntity(record.Entity);
            if (!buckets.TryGetValue(indexKey, out var recordIds))
            {
                recordIds = [];
                buckets[indexKey] = recordIds;
            }

            recordIds.Add(record.RecordId);
        }

        return buckets
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value.Count > 0)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
    }

    private static bool IndexBucketsEqual(
        IReadOnlyDictionary<string, IReadOnlyList<string>> expected,
        IReadOnlyDictionary<string, IReadOnlyList<string>> actual)
    {
        if (expected.Count != actual.Count)
            return false;

        foreach (var expectedBucket in expected)
        {
            if (!actual.TryGetValue(expectedBucket.Key, out var actualRecordIds))
                return false;
            if (!expectedBucket.Value.SequenceEqual(actualRecordIds, StringComparer.Ordinal))
                return false;
        }

        return true;
    }

    private static object VerifyPayload(
        byte[] payload,
        Type entityType,
        string entityName)
    {
        var entity = JsonSerializer.Deserialize(payload, entityType, EntityReadJsonOptions);
        if (entity is null)
            throw new InvalidDataException(
                $"The JSONColdStore record for '{entityName}' deserialized to null.");

        return entity;
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

    private async Task EnsureUniqueIndexesAsync(
        JsonColdStoreEntityDescriptor descriptor,
        object entity,
        string recordId,
        IReadOnlySet<string>? ignoredRecordIds,
        CancellationToken cancellationToken)
    {
        foreach (var index in descriptor.Indexes.Where(index => index.IsUnique))
        {
            var indexKey = index.CreateIndexKeyFromEntity(entity);
            EnsureCurrentIndexAvailable(descriptor, index);
            var currentRecordIds = await _indexStore.ReadRecordIdsAsync(
                descriptor.EntityName,
                index.StorageName,
                indexKey,
                cancellationToken);

            foreach (var currentRecordId in currentRecordIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsSameRecord(currentRecordId, recordId)
                    || IsIgnoredRecord(currentRecordId, ignoredRecordIds))
                {
                    continue;
                }

                if (_session.Records.RecordExists(descriptor.EntityName, currentRecordId))
                    ThrowUniqueIndexConflict(descriptor, index, indexKey, currentRecordId);
            }

            await EnsureLegacyUniqueIndexAsync(
                descriptor,
                index,
                entity,
                recordId,
                indexKey,
                ignoredRecordIds,
                cancellationToken);
        }
    }

    private async Task EnsureLegacyUniqueIndexAsync(
        JsonColdStoreEntityDescriptor descriptor,
        JsonColdStoreIndexDescriptor index,
        object entity,
        string recordId,
        string indexKey,
        IReadOnlySet<string>? ignoredRecordIds,
        CancellationToken cancellationToken)
    {
        if (index.Properties.Length == 1)
        {
            var lookup = await _session.LegacyRecords.LookupIndexAsync(
                descriptor,
                index,
                indexKey,
                index.Properties[0].GetValue(entity) ?? string.Empty,
                cancellationToken);

            if (lookup.UseIndex)
            {
                foreach (var legacyRecordId in lookup.RecordIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsSameRecord(legacyRecordId, recordId)
                        || IsIgnoredRecord(legacyRecordId, ignoredRecordIds))
                    {
                        continue;
                    }

                    if (_session.LegacyRecords.RecordExists(descriptor, legacyRecordId))
                        ThrowUniqueIndexConflict(descriptor, index, indexKey, legacyRecordId);
                }

                return;
            }
        }

        await foreach (var legacyRecord in _session.LegacyRecords.ReadAllRecordsAsync(
            descriptor,
            cancellationToken))
        {
            if (IsSameRecord(legacyRecord.RecordId, recordId)
                || IsIgnoredRecord(legacyRecord.RecordId, ignoredRecordIds))
            {
                continue;
            }

            var legacyEntity = JsonSerializer.Deserialize(
                legacyRecord.Payload,
                descriptor.ClrType,
                EntityReadJsonOptions);
            if (legacyEntity is not null
                && string.Equals(
                    index.CreateIndexKeyFromEntity(legacyEntity),
                    indexKey,
                    StringComparison.Ordinal))
            {
                ThrowUniqueIndexConflict(descriptor, index, indexKey, legacyRecord.RecordId);
            }
        }
    }

    private void EnsureCurrentIndexAvailable(
        JsonColdStoreEntityDescriptor descriptor,
        JsonColdStoreIndexDescriptor index)
    {
        if (_indexStore.DocumentExists(descriptor.EntityName, index.StorageName)
            || !_session.Records.EntityHasRecords(descriptor.EntityName))
        {
            return;
        }

        throw new InvalidOperationException(
            $"The JSONColdStore index '{index.StorageName}' for entity '{descriptor.EntityName}' is missing. "
            + "Rebuild JSONColdStore indexes before using this index.");
    }

    private static bool IsSameRecord(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private static bool IsIgnoredRecord(string recordId, IReadOnlySet<string>? ignoredRecordIds) =>
        ignoredRecordIds?.Contains(recordId) == true;

    private static void ThrowUniqueIndexConflict(
        JsonColdStoreEntityDescriptor descriptor,
        JsonColdStoreIndexDescriptor index,
        string indexKey,
        string existingRecordId)
    {
        throw new InvalidOperationException(
            $"The JSONColdStore unique index '{index.StorageName}' for entity '{descriptor.EntityName}' "
            + $"already contains value '{indexKey}' on record '{existingRecordId}'.");
    }

    private async Task AddLegacyIndexResultsAsync<TEntity>(
        JsonColdStoreEntityDescriptor descriptor,
        JsonColdStoreIndexDescriptor index,
        string indexKey,
        object indexValue,
        HashSet<string> seenRecordIds,
        List<TEntity> results,
        int? maxResults,
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
                {
                    results.Add(entity);
                    if (HasReachedLimit(results, maxResults))
                        return;
                }
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
                if (HasReachedLimit(results, maxResults))
                    return;
            }
        }
    }

    private static bool HasReachedLimit<TEntity>(List<TEntity> results, int? maxResults) =>
        maxResults is not null && results.Count >= maxResults.Value;

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
    int VerifiedLegacyRecords,
    int VerifiedIndexes);

internal sealed record JsonColdStoreVerifiedEntityRecord(string RecordId, object Entity);
