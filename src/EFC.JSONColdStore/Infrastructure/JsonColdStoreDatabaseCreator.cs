using EFC.JSONColdStore.Storage;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFC.JSONColdStore.Infrastructure;

internal sealed class JsonColdStoreDatabaseCreator : IDatabaseCreator
{
    private readonly JsonColdStoreOptions _options;

    public JsonColdStoreDatabaseCreator(IDbContextOptions contextOptions)
    {
        ArgumentNullException.ThrowIfNull(contextOptions);
        _options = contextOptions.FindExtension<JsonColdStoreOptionsExtension>()?.Options
            ?? throw new InvalidOperationException("JSONColdStore options are not configured.");
    }

    public bool EnsureDeleted() =>
        throw new NotSupportedException("JSONColdStore EnsureDeleted is not implemented.");

    public Task<bool> EnsureDeletedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<bool>(
            new NotSupportedException("JSONColdStore EnsureDeletedAsync is not implemented."));
    }

    public bool EnsureCreated()
    {
        var storeFilePath = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            JsonColdStoreCatalog.StoreFileName);
        var existed = File.Exists(storeFilePath);

        new JsonColdStoreCatalog(_options)
            .EnsureInitializedAsync()
            .GetAwaiter()
            .GetResult();

        return !existed;
    }

    public async Task<bool> EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        var storeFilePath = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            JsonColdStoreCatalog.StoreFileName);
        var existed = File.Exists(storeFilePath);

        await new JsonColdStoreCatalog(_options)
            .EnsureInitializedAsync(cancellationToken)
            .ConfigureAwait(false);

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
