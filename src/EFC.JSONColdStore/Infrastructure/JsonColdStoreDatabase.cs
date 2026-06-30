using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;

namespace EFC.JSONColdStore.Infrastructure;

internal sealed class JsonColdStoreDatabase : IDatabase
{
    public int SaveChanges(IList<IUpdateEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        throw Unsupported("SaveChanges is not implemented yet.");
    }

    public Task<int> SaveChangesAsync(
        IList<IUpdateEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<int>(Unsupported("SaveChangesAsync is not implemented yet."));
    }

    public Func<QueryContext, TResult> CompileQuery<TResult>(Expression query, bool async)
    {
        ArgumentNullException.ThrowIfNull(query);
        return _ => throw Unsupported("LINQ query execution is not implemented yet.");
    }

    public Expression<Func<QueryContext, TResult>> CompileQueryExpression<TResult>(Expression query, bool async)
    {
        ArgumentNullException.ThrowIfNull(query);

        var exception = Expression.New(
            typeof(NotSupportedException).GetConstructor([typeof(string)])!,
            Expression.Constant("LINQ query expression compilation is not implemented yet."));
        var body = Expression.Throw(exception, typeof(TResult));
        var parameter = Expression.Parameter(typeof(QueryContext), "queryContext");

        return Expression.Lambda<Func<QueryContext, TResult>>(body, parameter);
    }

    private static NotSupportedException Unsupported(string message) =>
        new("JSONColdStore EF provider support is incomplete: " + message);
}
