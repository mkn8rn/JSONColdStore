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

        var descriptor = _modelDescriptor.FindEntity(typeof(TEntity));
        var recordId = descriptor.CreateRecordIdFromEntity(entity);
        var payload = JsonSerializer.SerializeToUtf8Bytes(entity, EntityJsonOptions);

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
}
