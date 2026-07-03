using System.Linq.Expressions;
using JSONColdStore.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;

namespace JSONColdStore.Infrastructure;

internal sealed class JsonColdStoreDatabase : IDatabase
{
    private readonly JsonColdStoreOptions _options;

    public JsonColdStoreDatabase(IDbContextOptions contextOptions)
    {
        ArgumentNullException.ThrowIfNull(contextOptions);
        _options = contextOptions.FindExtension<JsonColdStoreOptionsExtension>()?.Options
            ?? throw new InvalidOperationException("JSONColdStore options are not configured.");
    }

    public int SaveChanges(IList<IUpdateEntry> entries)
    {
        return SaveChangesAsync(entries).GetAwaiter().GetResult();
    }

    public Task<int> SaveChangesAsync(
        IList<IUpdateEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return SaveChangesInternalAsync(entries, cancellationToken);
    }

    public Func<QueryContext, TResult> CompileQuery<TResult>(Expression query, bool async)
    {
        ArgumentNullException.ThrowIfNull(query);
        return queryContext => JsonColdStoreQueryExecutor.Execute<TResult>(
            _options,
            queryContext,
            query,
            async);
    }

    public Expression<Func<QueryContext, TResult>> CompileQueryExpression<TResult>(Expression query, bool async)
    {
        ArgumentNullException.ThrowIfNull(query);

        var parameter = Expression.Parameter(typeof(QueryContext), "queryContext");
        var body = Expression.Call(
            typeof(JsonColdStoreQueryExecutor),
            nameof(JsonColdStoreQueryExecutor.Execute),
            [typeof(TResult)],
            Expression.Constant(_options),
            parameter,
            Expression.Constant(query, typeof(Expression)),
            Expression.Constant(async));

        return Expression.Lambda<Func<QueryContext, TResult>>(body, parameter);
    }

    private static NotSupportedException Unsupported(string message) =>
        new("JSONColdStore EF provider support is incomplete: " + message);

    private async Task<int> SaveChangesInternalAsync(
        IList<IUpdateEntry> entries,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
            return 0;

        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(
            _options,
            acquireWriterLock: true,
            cancellationToken);
        var modelDescriptor = JsonColdStoreModelDescriptor.Create(entries[0].Context.Model);
        var entityStore = new JsonColdStoreEntityRecordStore(session, modelDescriptor);
        var pendingChanges = CreatePendingChanges(entries, modelDescriptor);
        await ValidatePendingUniqueIndexesAsync(
            pendingChanges,
            entityStore,
            cancellationToken);
        var saved = 0;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (entry.EntityState)
            {
                case EntityState.Added:
                case EntityState.Modified:
                    var entity = entry.ToEntityEntry().Entity;
                    var descriptor = modelDescriptor.FindEntity(entry.EntityType);
                    pendingChanges.DeletedRecordIdsByEntity.TryGetValue(
                        descriptor.EntityName,
                        out var ignoredRecordIds);
                    await entityStore.WriteEntityAsync(
                        entity,
                        descriptor,
                        ignoredRecordIds,
                        cancellationToken);
                    saved++;
                    break;

                case EntityState.Deleted:
                    var deletedEntity = entry.ToEntityEntry().Entity;
                    var deletedDescriptor = modelDescriptor.FindEntity(entry.EntityType);
                    await entityStore.DeleteEntityAsync(
                        deletedEntity,
                        deletedDescriptor,
                        cancellationToken);
                    saved++;
                    break;

                default:
                    break;
            }
        }

        return saved;
    }

    private static JsonColdStorePendingChanges CreatePendingChanges(
        IEnumerable<IUpdateEntry> entries,
        JsonColdStoreModelDescriptor modelDescriptor)
    {
        var writes = new List<JsonColdStorePendingWrite>();
        var deletedRecordIdsByEntity = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (entry.EntityState is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            var entity = entry.ToEntityEntry().Entity;
            var descriptor = modelDescriptor.FindEntity(entry.EntityType);
            var recordId = descriptor.CreateRecordIdFromEntity(entity);

            if (entry.EntityState == EntityState.Deleted)
            {
                if (!deletedRecordIdsByEntity.TryGetValue(descriptor.EntityName, out var deletedRecordIds))
                {
                    deletedRecordIds = new HashSet<string>(StringComparer.Ordinal);
                    deletedRecordIdsByEntity[descriptor.EntityName] = deletedRecordIds;
                }

                deletedRecordIds.Add(recordId);
                continue;
            }

            writes.Add(new JsonColdStorePendingWrite(
                entity,
                entry.EntityType.ClrType,
                descriptor,
                recordId));
        }

        return new JsonColdStorePendingChanges(writes, deletedRecordIdsByEntity);
    }

    private static async Task ValidatePendingUniqueIndexesAsync(
        JsonColdStorePendingChanges pendingChanges,
        JsonColdStoreEntityRecordStore entityStore,
        CancellationToken cancellationToken)
    {
        var pendingUniqueKeys = new Dictionary<string, JsonColdStorePendingUniqueKey>(
            StringComparer.Ordinal);

        foreach (var write in pendingChanges.Writes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var index in write.Descriptor.Indexes.Where(index => index.IsUnique))
            {
                var indexKey = index.CreateIndexKeyFromEntity(write.Entity);
                var pendingKey = string.Join(
                    '\u001F',
                    write.Descriptor.EntityName,
                    index.StorageName,
                    indexKey);

                if (pendingUniqueKeys.TryGetValue(pendingKey, out var existing)
                    && !string.Equals(existing.RecordId, write.RecordId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"The JSONColdStore unique index '{index.StorageName}' for entity "
                        + $"'{write.Descriptor.EntityName}' contains a duplicate value "
                        + "inside the current SaveChanges batch.");
                }

                pendingUniqueKeys[pendingKey] = new JsonColdStorePendingUniqueKey(write.RecordId);
            }
        }

        foreach (var write in pendingChanges.Writes)
        {
            pendingChanges.DeletedRecordIdsByEntity.TryGetValue(
                write.Descriptor.EntityName,
                out var ignoredRecordIds);
            await entityStore.ValidateUniqueIndexesAsync(
                write.Entity,
                write.Descriptor,
                ignoredRecordIds,
                cancellationToken);
        }
    }

    private sealed record JsonColdStorePendingChanges(
        IReadOnlyList<JsonColdStorePendingWrite> Writes,
        IReadOnlyDictionary<string, HashSet<string>> DeletedRecordIdsByEntity);

    private sealed record JsonColdStorePendingWrite(
        object Entity,
        Type EntityType,
        JsonColdStoreEntityDescriptor Descriptor,
        string RecordId);

    private sealed record JsonColdStorePendingUniqueKey(string RecordId);
}
