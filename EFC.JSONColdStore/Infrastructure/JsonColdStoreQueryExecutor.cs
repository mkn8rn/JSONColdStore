using System.Linq.Expressions;
using System.Reflection;
using System.Globalization;
using System.Runtime.CompilerServices;
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
    private static readonly MethodInfo ExecuteAsyncEnumerableMethod =
        typeof(JsonColdStoreQueryExecutor)
            .GetMethod(nameof(ExecuteAsyncEnumerable), BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("The JSONColdStore query executor is misconfigured.");
    private static readonly MethodInfo ExecuteTaskMethod =
        typeof(JsonColdStoreQueryExecutor)
            .GetMethod(nameof(ExecuteTask), BindingFlags.NonPublic | BindingFlags.Static)
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

        var plan = JsonColdStoreQueryPlan.Create(query);
        var currentOptions = queryContext.Context.GetService<IDbContextOptions>()
            .FindExtension<JsonColdStoreOptionsExtension>()?.Options
            ?? options;

        if (async)
        {
            var asyncResult = CreateAsyncResult<TResult>(
                currentOptions,
                queryContext,
                plan);
            return (TResult)asyncResult;
        }

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

    private static object CreateAsyncResult<TResult>(
        JsonColdStoreOptions options,
        QueryContext queryContext,
        JsonColdStoreQueryPlan plan)
    {
        var resultType = typeof(TResult);
        if (resultType.IsGenericType
            && resultType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
        {
            var entityType = resultType.GetGenericArguments()[0];
            return ExecuteAsyncEnumerableMethod
                .MakeGenericMethod(entityType)
                .Invoke(null, [options, queryContext, plan, CancellationToken.None])!;
        }

        if (resultType.IsGenericType
            && resultType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var terminalType = resultType.GetGenericArguments()[0];
            return ExecuteTaskMethod
                .MakeGenericMethod(plan.EntityType, terminalType)
                .Invoke(null, [options, queryContext, plan])!;
        }

        throw Unsupported("The async LINQ query result shape is not supported.");
    }

    private static object? ExecuteTyped<TEntity>(
        JsonColdStoreOptions options,
        QueryContext queryContext,
        JsonColdStoreQueryPlan plan)
        where TEntity : class
    {
        var results = ExecuteSequenceAsync<TEntity>(
                options,
                queryContext,
                plan,
                queryContext.CancellationToken)
            .GetAwaiter()
            .GetResult();

        return ApplyTerminal(results, plan.Terminal);
    }

    private static async IAsyncEnumerable<TEntity> ExecuteAsyncEnumerable<TEntity>(
        JsonColdStoreOptions options,
        QueryContext queryContext,
        JsonColdStoreQueryPlan plan,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
        where TEntity : class
    {
        var effectiveCancellationToken = cancellationToken.CanBeCanceled
            ? cancellationToken
            : queryContext.CancellationToken;
        var results = await ExecuteSequenceAsync<TEntity>(
                options,
                queryContext,
                plan,
                effectiveCancellationToken)
            .ConfigureAwait(false);

        foreach (var entity in results)
        {
            effectiveCancellationToken.ThrowIfCancellationRequested();
            yield return entity;
        }
    }

    private static async Task<TTerminal> ExecuteTask<TEntity, TTerminal>(
        JsonColdStoreOptions options,
        QueryContext queryContext,
        JsonColdStoreQueryPlan plan)
        where TEntity : class
    {
        var results = await ExecuteSequenceAsync<TEntity>(
                options,
                queryContext,
                plan,
                queryContext.CancellationToken)
            .ConfigureAwait(false);
        var terminal = ApplyTerminal(results, plan.Terminal);
        return (TTerminal)terminal!;
    }

    private static async Task<List<TEntity>> ExecuteSequenceAsync<TEntity>(
        JsonColdStoreOptions options,
        QueryContext queryContext,
        JsonColdStoreQueryPlan plan,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(
                options,
                acquireWriterLock: false,
                cancellationToken)
            .ConfigureAwait(false);
        var modelDescriptor = JsonColdStoreModelDescriptor.Create(queryContext.Context.Model);
        var entityStore = new JsonColdStoreEntityRecordStore(session, modelDescriptor);
        var entityDescriptor = modelDescriptor.FindEntity(typeof(TEntity));
        var candidates = await ReadCandidatesAsync<TEntity>(
                options,
                queryContext,
                entityStore,
                entityDescriptor,
                plan,
                cancellationToken)
            .ConfigureAwait(false);
        var predicates = plan.Filters
            .Select(filter => CreatePredicate<TEntity>(queryContext, filter))
            .ToArray();

        return candidates
            .Where(entity => predicates.All(predicate => predicate(entity)))
            .ToList();
    }

    private static object? ApplyTerminal<TEntity>(
        List<TEntity> results,
        JsonColdStoreQueryTerminal terminal)
    {
        return terminal switch
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

    private static async Task<List<TEntity>> ReadCandidatesAsync<TEntity>(
        JsonColdStoreOptions options,
        QueryContext queryContext,
        JsonColdStoreEntityRecordStore entityStore,
        JsonColdStoreEntityDescriptor entityDescriptor,
        JsonColdStoreQueryPlan plan,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var seek = plan.Filters
            .Select(filter => TryCreateSeek(queryContext, filter))
            .FirstOrDefault(candidate => candidate is not null && CanUseSeek(entityDescriptor, candidate));

        if (seek is not null)
        {
            if (string.Equals(seek.PropertyName, entityDescriptor.Key.PropertyName, StringComparison.Ordinal))
            {
                var entity = await entityStore.ReadEntityAsync<TEntity>(
                        seek.Value!,
                        cancellationToken)
                    .ConfigureAwait(false);
                return entity is null ? [] : [entity];
            }

            var indexed = await entityStore.ReadEntitiesByIndexAsync<TEntity>(
                    seek.PropertyName,
                    seek.Value!,
                    cancellationToken)
                .ConfigureAwait(false);
            return indexed.ToList();
        }

        if (options.FullScanPolicy != JsonColdStoreScanPolicy.AllowSilentScans)
        {
            throw Unsupported(
                "LINQ query execution would require a full scan. Declare a single-property index, "
                + "query by primary key, or allow silent scans for small stores.");
        }

        var results = new List<TEntity>();
        await foreach (var entity in entityStore.ScanEntitiesAsync<TEntity>(cancellationToken))
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
