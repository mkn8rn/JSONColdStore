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

    /// <summary>
    /// Rebuilds declared JSONColdStore index files for every mapped entity type.
    /// </summary>
    public static async Task<int> RebuildJsonColdStoreIndexesAsync(
        this DatabaseFacade database,
        CancellationToken cancellationToken = default)
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

        return await entityStore.RebuildIndexesAsync(cancellationToken);
    }

    /// <summary>
    /// Verifies JSONColdStore records and compatible legacy records without mutating storage.
    /// </summary>
    public static async Task<JsonColdStoreVerificationResult> VerifyJsonColdStoreAsync(
        this DatabaseFacade database,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        var context = database.GetService<ICurrentDbContext>().Context;
        var storeOptions = database.GetService<IDbContextOptions>()
            .FindExtension<JsonColdStoreOptionsExtension>()?.Options
            ?? throw new InvalidOperationException("JSONColdStore options are not configured.");
        var verificationOptions = storeOptions with
        {
            Integrity = storeOptions.Integrity with
            {
                VerifyOnRead = storeOptions.Integrity.EnableChecksums,
            },
        };

        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(
            verificationOptions,
            acquireWriterLock: false,
            cancellationToken);
        var entityStore = new JsonColdStoreEntityRecordStore(
            session,
            JsonColdStoreModelDescriptor.Create(context.Model));

        var result = await entityStore.VerifyEntitiesAsync(cancellationToken);
        return new JsonColdStoreVerificationResult(
            result.VerifiedRecords,
            result.VerifiedLegacyRecords);
    }

    /// <summary>
    /// Verifies JSONColdStore records and quarantines corrupt JSONColdStore record files.
    /// </summary>
    public static async Task<JsonColdStoreRepairResult> RepairJsonColdStoreAsync(
        this DatabaseFacade database,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        var context = database.GetService<ICurrentDbContext>().Context;
        var storeOptions = database.GetService<IDbContextOptions>()
            .FindExtension<JsonColdStoreOptionsExtension>()?.Options
            ?? throw new InvalidOperationException("JSONColdStore options are not configured.");
        var repairOptions = storeOptions with
        {
            Integrity = storeOptions.Integrity with
            {
                VerifyOnRead = storeOptions.Integrity.EnableChecksums,
            },
        };

        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(
            repairOptions,
            acquireWriterLock: true,
            cancellationToken);

        var entityStore = new JsonColdStoreEntityRecordStore(
            session,
            JsonColdStoreModelDescriptor.Create(context.Model));
        var result = await session.Records.RepairAllRecordsAsync(cancellationToken);
        var rebuiltIndexRecordCount = result.QuarantinedRecords > 0
            ? await entityStore.RebuildIndexesAsync(cancellationToken)
            : 0;

        return new JsonColdStoreRepairResult(
            result.VerifiedRecords,
            result.QuarantinedRecords,
            rebuiltIndexRecordCount);
    }

    /// <summary>
    /// Creates a manual JSONColdStore snapshot while holding the writer lock.
    /// </summary>
    public static async Task<JsonColdStoreSnapshotResult> CreateJsonColdStoreSnapshotAsync(
        this DatabaseFacade database,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        var storeOptions = database.GetService<IDbContextOptions>()
            .FindExtension<JsonColdStoreOptionsExtension>()?.Options
            ?? throw new InvalidOperationException("JSONColdStore options are not configured.");

        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(
            storeOptions,
            acquireWriterLock: true,
            cancellationToken);
        var snapshots = new JsonColdStoreSnapshotStore(session.Options);
        return await snapshots.CreateSnapshotAsync(cancellationToken);
    }

    /// <summary>
    /// Reads redacted JSONColdStore operational diagnostics without mutating storage.
    /// </summary>
    public static Task<JsonColdStoreDiagnosticsResult> GetJsonColdStoreDiagnosticsAsync(
        this DatabaseFacade database,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        var context = database.GetService<ICurrentDbContext>().Context;
        var storeOptions = database.GetService<IDbContextOptions>()
            .FindExtension<JsonColdStoreOptionsExtension>()?.Options
            ?? throw new InvalidOperationException("JSONColdStore options are not configured.");
        var diagnostics = new JsonColdStoreDiagnosticsStore(
            storeOptions,
            JsonColdStoreModelDescriptor.Create(context.Model));

        return diagnostics.ReadAsync(cancellationToken);
    }
}
