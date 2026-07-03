using Microsoft.EntityFrameworkCore.Storage;

namespace JSONColdStore.Infrastructure;

internal sealed class JsonColdStoreTransactionManager : IDbContextTransactionManager
{
    public IDbContextTransaction? CurrentTransaction => null;

    public IDbContextTransaction BeginTransaction() =>
        throw Unsupported("Explicit transactions are not implemented yet.");

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<IDbContextTransaction>(
            Unsupported("Explicit async transactions are not implemented yet."));
    }

    public void CommitTransaction() =>
        throw Unsupported("CommitTransaction is not implemented because explicit transactions are not implemented yet.");

    public Task CommitTransactionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(
            Unsupported("CommitTransactionAsync is not implemented because explicit transactions are not implemented yet."));
    }

    public void RollbackTransaction() =>
        throw Unsupported("RollbackTransaction is not implemented because explicit transactions are not implemented yet.");

    public Task RollbackTransactionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(
            Unsupported("RollbackTransactionAsync is not implemented because explicit transactions are not implemented yet."));
    }

    public void ResetState()
    {
    }

    public Task ResetStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static NotSupportedException Unsupported(string message) =>
        new("JSONColdStore EF provider support is incomplete: " + message);
}
