using System.Text.Json;
using EFC.JSONColdStore.Storage;

namespace EFC.JSONColdStore.Infrastructure;

internal sealed class JsonColdStoreEntityRecordStore
{
    private static readonly JsonSerializerOptions EntityJsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly JsonColdStoreDatabaseSession _session;
    private readonly JsonColdStoreModelDescriptor _modelDescriptor;
    private readonly JsonColdStoreIndexStore _indexStore;

    internal JsonColdStoreEntityRecordStore(
        JsonColdStoreDatabaseSession session,
        JsonColdStoreModelDescriptor modelDescriptor)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _modelDescriptor = modelDescriptor ?? throw new ArgumentNullException(nameof(modelDescriptor));
        _indexStore = new JsonColdStoreIndexStore(session.Options);
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
        var recordId = descriptor.CreateRecordIdFromEntity(entity);
        var payload = JsonSerializer.SerializeToUtf8Bytes(entity, entityType, EntityJsonOptions);

        await _session.Records.WriteRecordAsync(
            descriptor.EntityName,
            recordId,
            payload,
            cancellationToken);

        await UpsertIndexesAsync(descriptor, entity, recordId, cancellationToken);
    }

    internal async Task<TEntity?> ReadEntityAsync<TEntity>(
        object keyValue,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        var descriptor = _modelDescriptor.FindEntity(typeof(TEntity));
        var recordId = descriptor.CreateRecordId(keyValue);
        if (!_session.Records.RecordExists(descriptor.EntityName, recordId))
            return null;

        var payload = await _session.Records.ReadRecordAsync(
            descriptor.EntityName,
            recordId,
            cancellationToken);

        return JsonSerializer.Deserialize<TEntity>(payload, EntityJsonOptions);
    }

    internal async Task<IReadOnlyList<TEntity>> ReadEntitiesByIndexAsync<TEntity>(
        string propertyName,
        object indexValue,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        var descriptor = _modelDescriptor.FindEntity(typeof(TEntity));
        var index = descriptor.FindSinglePropertyIndex(propertyName);
        var indexKey = index.CreateIndexKeyFromValues(indexValue);
        var recordIds = await _indexStore.ReadRecordIdsAsync(
            descriptor.EntityName,
            index.StorageName,
            indexKey,
            cancellationToken);
        var results = new List<TEntity>();

        foreach (var recordId in recordIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_session.Records.RecordExists(descriptor.EntityName, recordId))
                continue;

            var payload = await _session.Records.ReadRecordAsync(
                descriptor.EntityName,
                recordId,
                cancellationToken);
            var entity = JsonSerializer.Deserialize<TEntity>(payload, EntityJsonOptions);
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
        await foreach (var payload in _session.Records.ReadAllRecordsAsync(
            descriptor.EntityName,
            cancellationToken))
        {
            var entity = JsonSerializer.Deserialize<TEntity>(payload, EntityJsonOptions);
            if (entity is not null)
                yield return entity;
        }
    }

    internal async Task DeleteEntityAsync(
        object entity,
        Type entityType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(entityType);

        var descriptor = _modelDescriptor.FindEntity(entityType);
        var recordId = descriptor.CreateRecordIdFromEntity(entity);

        await _session.Records.DeleteRecordAsync(
            descriptor.EntityName,
            recordId,
            cancellationToken);

        await RemoveIndexesAsync(descriptor, recordId, cancellationToken);
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
