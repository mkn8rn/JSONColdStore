using EFC.JSONColdStore.Infrastructure;
using EFC.JSONColdStore.Storage;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EFC.JSONColdStore;

/// <summary>
/// Database facade helpers for JSONColdStore-specific operations.
/// </summary>
public static class JsonColdStoreDatabaseFacadeExtensions
{
    /// <summary>
    /// Reads one entity by primary key directly from JSONColdStore storage.
    /// </summary>
    public static async Task<TEntity?> ReadJsonColdStoreAsync<TEntity>(
        this DatabaseFacade database,
        object keyValue,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(keyValue);

        var context = database.GetService<ICurrentDbContext>().Context;
        var storeOptions = database.GetService<IDbContextOptions>()
            .FindExtension<JsonColdStoreOptionsExtension>()?.Options
            ?? throw new InvalidOperationException("JSONColdStore options are not configured.");

        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(
            storeOptions,
            acquireWriterLock: false,
            cancellationToken);
        var entityStore = new JsonColdStoreEntityRecordStore(
            session,
            JsonColdStoreModelDescriptor.Create(context.Model));

        return await entityStore.ReadEntityAsync<TEntity>(keyValue, cancellationToken);
    }

    /// <summary>
    /// Explicitly scans all stored records for an entity type from JSONColdStore storage.
    /// </summary>
    public static async Task<IReadOnlyList<TEntity>> ScanJsonColdStoreAsync<TEntity>(
        this DatabaseFacade database,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(database);

        var context = database.GetService<ICurrentDbContext>().Context;
        var storeOptions = database.GetService<IDbContextOptions>()
            .FindExtension<JsonColdStoreOptionsExtension>()?.Options
            ?? throw new InvalidOperationException("JSONColdStore options are not configured.");

        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(
            storeOptions,
            acquireWriterLock: false,
            cancellationToken);
        var entityStore = new JsonColdStoreEntityRecordStore(
            session,
            JsonColdStoreModelDescriptor.Create(context.Model));
        var results = new List<TEntity>();

        await foreach (var entity in entityStore.ScanEntitiesAsync<TEntity>(cancellationToken))
            results.Add(entity);

        return results;
    }

    /// <summary>
    /// Reads stored entities by a declared single-property JSONColdStore index.
    /// </summary>
    public static async Task<IReadOnlyList<TEntity>> ReadJsonColdStoreIndexAsync<TEntity>(
        this DatabaseFacade database,
        string propertyName,
        object indexValue,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        ArgumentNullException.ThrowIfNull(indexValue);

        var context = database.GetService<ICurrentDbContext>().Context;
        var storeOptions = database.GetService<IDbContextOptions>()
            .FindExtension<JsonColdStoreOptionsExtension>()?.Options
            ?? throw new InvalidOperationException("JSONColdStore options are not configured.");

        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(
            storeOptions,
            acquireWriterLock: false,
            cancellationToken);
        var entityStore = new JsonColdStoreEntityRecordStore(
            session,
            JsonColdStoreModelDescriptor.Create(context.Model));

        return await entityStore.ReadEntitiesByIndexAsync<TEntity>(
            propertyName,
            indexValue,
            cancellationToken);
    }

    /// <summary>
    /// Rebuilds declared JSONColdStore index files for one entity type from stored records.
    /// </summary>
    public static async Task<int> RebuildJsonColdStoreIndexesAsync<TEntity>(
        this DatabaseFacade database,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(database);

        var context = database.GetService<ICurrentDbContext>().Context;
        var storeOptions = database.GetService<IDbContextOptions>()
            .FindExtension<JsonColdStoreOptionsExtension>()?.Options
            ?? throw new InvalidOperationException("JSONColdStore options are not configured.");

        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(
            storeOptions,
            acquireWriterLock: true,
            cancellationToken);
        var entityStore = new JsonColdStoreEntityRecordStore(
            session,
            JsonColdStoreModelDescriptor.Create(context.Model));

        return await entityStore.RebuildIndexesAsync<TEntity>(cancellationToken);
    }
}
