using System.Linq.Expressions;
using System.Reflection;
using System.Globalization;
using EFC.JSONColdStore.Storage;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;

namespace EFC.JSONColdStore.Infrastructure;

internal static class JsonColdStoreQueryExecutor
{
    private static readonly MethodInfo ExecuteTypedMethod =
        typeof(JsonColdStoreQueryExecutor)
            .GetMethod(nameof(ExecuteTyped), BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("The JSONColdStore query executor is misconfigured.");

    internal static TResult Execute<TResult>(
        JsonColdStoreOptions options,
        QueryContext queryContext,
        Expression query,
        bool async)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(queryContext);
        ArgumentNullException.ThrowIfNull(query);

        if (async)
            throw Unsupported("Async LINQ query execution is not implemented yet.");

        var plan = JsonColdStoreQueryPlan.Create(query);
        var currentOptions = queryContext.Context.GetService<IDbContextOptions>()
            .FindExtension<JsonColdStoreOptionsExtension>()?.Options
            ?? options;
        try
        {
            var result = ExecuteTypedMethod
                .MakeGenericMethod(plan.EntityType)
                .Invoke(null, [currentOptions, queryContext, plan]);
            return (TResult)result!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static object? ExecuteTyped<TEntity>(
        JsonColdStoreOptions options,
        QueryContext queryContext,
        JsonColdStoreQueryPlan plan)
        where TEntity : class
    {
        using var session = JsonColdStoreDatabaseSession.OpenAsync(
                options,
                acquireWriterLock: false)
            .GetAwaiter()
            .GetResult();
        var modelDescriptor = JsonColdStoreModelDescriptor.Create(queryContext.Context.Model);
        var entityStore = new JsonColdStoreEntityRecordStore(session, modelDescriptor);
        var entityDescriptor = modelDescriptor.FindEntity(typeof(TEntity));
        var candidates = ReadCandidates<TEntity>(
            options,
            queryContext,
            entityStore,
            entityDescriptor,
            plan);
        var predicates = plan.Filters
            .Select(filter => CreatePredicate<TEntity>(queryContext, filter))
            .ToArray();
        var results = candidates
            .Where(entity => predicates.All(predicate => predicate(entity)))
            .ToList();

        return plan.Terminal switch
        {
            JsonColdStoreQueryTerminal.Sequence => results,
            JsonColdStoreQueryTerminal.First => results.First(),
            JsonColdStoreQueryTerminal.FirstOrDefault => results.FirstOrDefault(),
            JsonColdStoreQueryTerminal.Single => results.Single(),
            JsonColdStoreQueryTerminal.SingleOrDefault => results.SingleOrDefault(),
            JsonColdStoreQueryTerminal.Count => results.Count,
            JsonColdStoreQueryTerminal.LongCount => (long)results.Count,
            JsonColdStoreQueryTerminal.Any => results.Count > 0,
            _ => throw Unsupported("The LINQ query terminal is not supported."),
        };
    }

    private static List<TEntity> ReadCandidates<TEntity>(
        JsonColdStoreOptions options,
        QueryContext queryContext,
        JsonColdStoreEntityRecordStore entityStore,
        JsonColdStoreEntityDescriptor entityDescriptor,
        JsonColdStoreQueryPlan plan)
        where TEntity : class
    {
        var seek = plan.Filters
            .Select(filter => TryCreateSeek(queryContext, filter))
            .FirstOrDefault(candidate => candidate is not null && CanUseSeek(entityDescriptor, candidate));

        if (seek is not null)
        {
            if (string.Equals(seek.PropertyName, entityDescriptor.Key.PropertyName, StringComparison.Ordinal))
            {
                var entity = entityStore.ReadEntityAsync<TEntity>(seek.Value!)
                    .GetAwaiter()
                    .GetResult();
                return entity is null ? [] : [entity];
            }

            return entityStore.ReadEntitiesByIndexAsync<TEntity>(seek.PropertyName, seek.Value!)
                .GetAwaiter()
                .GetResult()
                .ToList();
        }

        if (options.FullScanPolicy != JsonColdStoreScanPolicy.AllowSilentScans)
        {
            throw Unsupported(
                "LINQ query execution would require a full scan. Declare a single-property index, "
                + "query by primary key, or allow silent scans for small stores.");
        }

        return ScanAll<TEntity>(entityStore)
            .GetAwaiter()
            .GetResult();
    }

    private static async Task<List<TEntity>> ScanAll<TEntity>(
        JsonColdStoreEntityRecordStore entityStore)
        where TEntity : class
    {
        var results = new List<TEntity>();
        await foreach (var entity in entityStore.ScanEntitiesAsync<TEntity>())
            results.Add(entity);

        return results;
    }

    private static Func<TEntity, bool> CreatePredicate<TEntity>(
        QueryContext queryContext,
        LambdaExpression filter)
        where TEntity : class
    {
        var seek = TryCreateSeek(queryContext, filter);
        if (seek is not null)
        {
            var property = typeof(TEntity).GetProperty(seek.PropertyName)
                ?? throw Unsupported($"The LINQ query property '{seek.PropertyName}' is not mapped to a CLR property.");

            return entity => ValuesEqual(property.GetValue(entity), seek.Value);
        }

        return (Func<TEntity, bool>)filter.Compile();
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (Equals(left, right))
            return true;

        if (left is null || right is null)
            return false;

        return string.Equals(
            Convert.ToString(left, CultureInfo.InvariantCulture),
            Convert.ToString(right, CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    private static bool CanUseSeek(
        JsonColdStoreEntityDescriptor descriptor,
        JsonColdStoreQuerySeek? seek)
    {
        if (seek is null || seek.Value is null)
            return false;

        if (string.Equals(seek.PropertyName, descriptor.Key.PropertyName, StringComparison.Ordinal))
            return true;

        return descriptor.Indexes.Any(index =>
            index.PropertyNames.Length == 1
            && string.Equals(index.PropertyNames[0], seek.PropertyName, StringComparison.Ordinal));
    }

    private static JsonColdStoreQuerySeek? TryCreateSeek(
        QueryContext queryContext,
        LambdaExpression filter)
    {
        if (filter.Body is not BinaryExpression binary || binary.NodeType != ExpressionType.Equal)
            return null;

        return TryCreateSeek(queryContext, filter.Parameters[0], binary.Left, binary.Right)
            ?? TryCreateSeek(queryContext, filter.Parameters[0], binary.Right, binary.Left);
    }

    private static JsonColdStoreQuerySeek? TryCreateSeek(
        QueryContext queryContext,
        ParameterExpression parameter,
        Expression memberExpression,
        Expression valueExpression)
    {
        var unwrappedMember = UnwrapConvert(memberExpression);
        if (unwrappedMember is not MemberExpression member
            || member.Expression != parameter
            || member.Member is not PropertyInfo property)
        {
            return null;
        }

        return new JsonColdStoreQuerySeek(property.Name, Evaluate(queryContext, valueExpression));
    }

    private static object? Evaluate(QueryContext queryContext, Expression expression)
    {
        var unwrapped = UnwrapConvert(expression);
        if (unwrapped is ConstantExpression constant)
            return constant.Value;
        if (unwrapped is QueryParameterExpression parameter)
            return queryContext.Parameters[parameter.Name];

        var boxed = Expression.Convert(unwrapped, typeof(object));
        return Expression.Lambda<Func<object?>>(boxed).Compile().Invoke();
    }

    private static Expression UnwrapConvert(Expression expression)
    {
        while (expression.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
            expression = ((UnaryExpression)expression).Operand;

        return expression;
    }

    private static NotSupportedException Unsupported(string message) =>
        new("JSONColdStore EF provider support is incomplete: " + message);
}

internal sealed record JsonColdStoreQueryPlan(
    Type EntityType,
    IReadOnlyList<LambdaExpression> Filters,
    JsonColdStoreQueryTerminal Terminal)
{
    internal static JsonColdStoreQueryPlan Create(Expression query)
    {
        var builder = Parse(query);
        return new JsonColdStoreQueryPlan(
            builder.EntityType,
            builder.Filters,
            builder.Terminal);
    }

    private static JsonColdStoreQueryPlanBuilder Parse(Expression expression)
    {
        if (expression is EntityQueryRootExpression root)
            return new JsonColdStoreQueryPlanBuilder(root.EntityType.ClrType);

        if (expression is not MethodCallExpression call)
            throw Unsupported("The LINQ query expression is not supported.");

        var methodName = call.Method.Name;
        switch (methodName)
        {
            case nameof(Queryable.Where):
            {
                var builder = Parse(call.Arguments[0]);
                builder.Filters.Add(UnquoteLambda(call.Arguments[1]));
                return builder;
            }

            case nameof(Queryable.First):
            case nameof(Queryable.FirstOrDefault):
            case nameof(Queryable.Single):
            case nameof(Queryable.SingleOrDefault):
            case nameof(Queryable.Count):
            case nameof(Queryable.LongCount):
            case nameof(Queryable.Any):
            {
                var builder = Parse(call.Arguments[0]);
                if (call.Arguments.Count == 2)
                    builder.Filters.Add(UnquoteLambda(call.Arguments[1]));

                builder.Terminal = methodName switch
                {
                    nameof(Queryable.First) => JsonColdStoreQueryTerminal.First,
                    nameof(Queryable.FirstOrDefault) => JsonColdStoreQueryTerminal.FirstOrDefault,
                    nameof(Queryable.Single) => JsonColdStoreQueryTerminal.Single,
                    nameof(Queryable.SingleOrDefault) => JsonColdStoreQueryTerminal.SingleOrDefault,
                    nameof(Queryable.Count) => JsonColdStoreQueryTerminal.Count,
                    nameof(Queryable.LongCount) => JsonColdStoreQueryTerminal.LongCount,
                    nameof(Queryable.Any) => JsonColdStoreQueryTerminal.Any,
                    _ => JsonColdStoreQueryTerminal.Sequence,
                };
                return builder;
            }

            default:
                throw Unsupported(
                    $"The LINQ query method '{methodName}' is not supported by JSONColdStore yet.");
        }
    }

    private static LambdaExpression UnquoteLambda(Expression expression)
    {
        if (expression is UnaryExpression { NodeType: ExpressionType.Quote } quoted)
            expression = quoted.Operand;

        return expression as LambdaExpression
            ?? throw Unsupported("The LINQ query predicate is not supported.");
    }

    private static NotSupportedException Unsupported(string message) =>
        new("JSONColdStore EF provider support is incomplete: " + message);
}

internal sealed class JsonColdStoreQueryPlanBuilder(Type entityType)
{
    internal Type EntityType { get; } = entityType;

    internal List<LambdaExpression> Filters { get; } = [];

    internal JsonColdStoreQueryTerminal Terminal { get; set; } = JsonColdStoreQueryTerminal.Sequence;
}

internal sealed record JsonColdStoreQuerySeek(string PropertyName, object? Value);

internal enum JsonColdStoreQueryTerminal
{
    Sequence,
    First,
    FirstOrDefault,
    Single,
    SingleOrDefault,
    Count,
    LongCount,
    Any,
}
