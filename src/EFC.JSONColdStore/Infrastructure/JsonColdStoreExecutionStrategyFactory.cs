using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFC.JSONColdStore.Infrastructure;

internal sealed class JsonColdStoreExecutionStrategyFactory(ICurrentDbContext currentContext)
    : IExecutionStrategyFactory
{
    public IExecutionStrategy Create() => new JsonColdStoreExecutionStrategy(currentContext.Context);
}

internal sealed class JsonColdStoreExecutionStrategy(DbContext context) : IExecutionStrategy
{
    public bool RetriesOnFailure => false;

    public TResult Execute<TState, TResult>(
        TState state,
        Func<DbContext, TState, TResult> operation,
        Func<DbContext, TState, ExecutionResult<TResult>>? verifySucceeded)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation(context, state);
    }

    public Task<TResult> ExecuteAsync<TState, TResult>(
        TState state,
        Func<DbContext, TState, CancellationToken, Task<TResult>> operation,
        Func<DbContext, TState, CancellationToken, Task<ExecutionResult<TResult>>>? verifySucceeded,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        return operation(context, state, cancellationToken);
    }
}
