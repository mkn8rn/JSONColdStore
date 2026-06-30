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
        }
        finally
        {
            writerLock.Dispose();
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.Delete(databaseDirectory, recursive: true);
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
        await modelCatalog.EnsureCompatibleAsync(
            JsonColdStoreModelDescriptor.Create(_currentDbContext.Context.Model),
            createIfMissing: true,
            cancellationToken).ConfigureAwait(false);

        return !existed;
    }

    public bool CanConnect() =>
        Directory.Exists(_options.DatabaseDirectory);

    public Task<bool> CanConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CanConnect());
    }
}
