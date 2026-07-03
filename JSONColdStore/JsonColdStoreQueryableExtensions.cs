using System.Linq.Expressions;
using System.Reflection;

namespace JSONColdStore;

/// <summary>
/// JSONColdStore-specific LINQ query helpers.
/// </summary>
public static class JsonColdStoreQueryableExtensions
{
    /// <summary>
    /// Marks a LINQ query as intentionally allowed to scan all records when configured.
    /// </summary>
    public static IQueryable<TEntity> AsJsonColdStoreExplicitScan<TEntity>(
        this IQueryable<TEntity> source)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(source);

        var method = ((MethodInfo)MethodBase.GetCurrentMethod()!)
            .GetGenericMethodDefinition()
            .MakeGenericMethod(typeof(TEntity));
        return source.Provider.CreateQuery<TEntity>(
            Expression.Call(null, method, source.Expression));
    }
}
