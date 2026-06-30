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

    internal JsonColdStoreEntityRecordStore(
        JsonColdStoreDatabaseSession session,
        JsonColdStoreModelDescriptor modelDescriptor)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _modelDescriptor = modelDescriptor ?? throw new ArgumentNullException(nameof(modelDescriptor));
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
    }
}
