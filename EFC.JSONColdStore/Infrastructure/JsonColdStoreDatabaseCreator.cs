using System.Security.Cryptography;
using System.Text.Json;
using EFC.JSONColdStore.Storage;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFC.JSONColdStore.Infrastructure;

internal sealed class JsonColdStoreDatabaseCreator : IDatabaseCreator
{
    private readonly JsonColdStoreOptions _options;
    private readonly ICurrentDbContext _currentDbContext;

    public JsonColdStoreDatabaseCreator(
        IDbContextOptions contextOptions,
        ICurrentDbContext currentDbContext)
    {
        ArgumentNullException.ThrowIfNull(contextOptions);
        _options = contextOptions.FindExtension<JsonColdStoreOptionsExtension>()?.Options
            ?? throw new InvalidOperationException("JSONColdStore options are not configured.");
        _currentDbContext = currentDbContext ?? throw new ArgumentNullException(nameof(currentDbContext));
    }

    public bool EnsureDeleted()
    {
        return EnsureDeletedAsync()
            .GetAwaiter()
            .GetResult();
    }

    public Task<bool> EnsureDeletedAsync(CancellationToken cancellationToken = default) =>
        EnsureDeletedInternalAsync(cancellationToken);

    private async Task<bool> EnsureDeletedInternalAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var databaseDirectory = JsonColdStorePathValidator.GetSafeChildPath(_options.DatabaseDirectory);
        if (!Directory.Exists(databaseDirectory))
            return false;

        var storeFilePath = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            JsonColdStoreCatalog.StoreFileName);
        if (!File.Exists(storeFilePath))
        {
            if (Directory.EnumerateFileSystemEntries(databaseDirectory).Any())
            {
                throw new InvalidOperationException(
                    "Refusing to delete the configured directory because it does not contain JSONColdStore metadata.");
            }

            return false;
        }

        var writerLock = await JsonColdStoreDatabaseLock.AcquireAsync(
                _options,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(storeFilePath))
                return false;

            var catalog = new JsonColdStoreCatalog(_options);
            await catalog.LoadAndValidateAsync(cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Delete(databaseDirectory, recursive: true);
        }
        finally
        {
            writerLock.Dispose();
        }

        return true;
    }

    public bool EnsureCreated()
    {
        return EnsureCreatedAsync()
            .GetAwaiter()
            .GetResult();
    }

    public Task<bool> EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        return EnsureCreatedInternalAsync(cancellationToken);
    }

    private async Task<bool> EnsureCreatedInternalAsync(CancellationToken cancellationToken)
    {
        var storeFilePath = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            JsonColdStoreCatalog.StoreFileName);
        var modelFilePath = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            JsonColdStoreModelCatalog.ModelFileName);
        var existed = File.Exists(storeFilePath) && File.Exists(modelFilePath);

        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(
            _options,
            acquireWriterLock: true,
            cancellationToken).ConfigureAwait(false);
        var modelCatalog = new JsonColdStoreModelCatalog(
            _options,
            session.Metadata.Policy.EncryptionEnabled);
        var modelDescriptor = JsonColdStoreModelDescriptor.Create(_currentDbContext.Context.Model);
        await modelCatalog.EnsureCompatibleAsync(
            modelDescriptor,
            createIfMissing: true,
            cancellationToken).ConfigureAwait(false);
        var entityStore = new JsonColdStoreEntityRecordStore(session, modelDescriptor);
        await entityStore.ImportLegacyRecordsAsync(cancellationToken).ConfigureAwait(false);

        return !existed;
    }

    public bool CanConnect()
    {
        return CanConnectAsync()
            .GetAwaiter()
            .GetResult();
    }

    public Task<bool> CanConnectAsync(CancellationToken cancellationToken = default) =>
        CanConnectInternalAsync(cancellationToken);

    private async Task<bool> CanConnectInternalAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var databaseDirectory = JsonColdStorePathValidator.GetSafeChildPath(_options.DatabaseDirectory);
        if (!Directory.Exists(databaseDirectory))
            return false;

        var storeFilePath = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            JsonColdStoreCatalog.StoreFileName);
        if (File.Exists(storeFilePath))
            return await CanLoadCurrentStoreAsync(cancellationToken).ConfigureAwait(false);

        var modelDescriptor = JsonColdStoreModelDescriptor.Create(_currentDbContext.Context.Model);
        var legacyRecords = new JsonColdStoreLegacyRecordStore(_options);
        foreach (var descriptor in modelDescriptor.Entities)
        {
            if (await HasReadableLegacyRecordAsync(
                    legacyRecords,
                    descriptor,
                    cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> CanLoadCurrentStoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var catalog = new JsonColdStoreCatalog(_options);
            var metadata = await catalog.LoadAndValidateAsync(cancellationToken).ConfigureAwait(false);
            var modelFilePath = JsonColdStorePathValidator.GetSafeChildPath(
                _options.DatabaseDirectory,
                JsonColdStoreModelCatalog.ModelFileName);
            if (!File.Exists(modelFilePath))
                return true;

            var modelCatalog = new JsonColdStoreModelCatalog(
                _options,
                metadata.Policy.EncryptionEnabled);
            return await modelCatalog.EnsureCompatibleAsync(
                JsonColdStoreModelDescriptor.Create(_currentDbContext.Context.Model),
                createIfMissing: false,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsConnectionProbeFailure(ex))
        {
            return false;
        }
    }

    private static async Task<bool> HasReadableLegacyRecordAsync(
        JsonColdStoreLegacyRecordStore legacyRecords,
        JsonColdStoreEntityDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in legacyRecords.ReadAllRecordsAsync(descriptor, cancellationToken))
                return true;
        }
        catch (Exception ex) when (IsConnectionProbeFailure(ex))
        {
            return false;
        }

        return false;
    }

    private static bool IsConnectionProbeFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException
            or NotSupportedException
            or InvalidOperationException
            or CryptographicException;
}
