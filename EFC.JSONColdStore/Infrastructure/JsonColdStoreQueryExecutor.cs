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
    private static readonly MethodInfo CreateSequenceListMethod =
        typeof(JsonColdStoreQueryExecutor)
            .GetMethod(nameof(CreateSequenceList), BindingFlags.NonPublic | BindingFlags.Static)
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
                .MakeGenericMethod(plan.EntityType, typeof(TResult))
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
                .MakeGenericMethod(plan.EntityType, entityType)
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

    private static object? ExecuteTyped<TEntity, TResult>(
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

        return plan.Terminal == JsonColdStoreQueryTerminal.Sequence
            ? CreateSequenceResult<TEntity, TResult>(results, queryContext, plan)
            : ApplyTerminal<TEntity, TResult>(results, queryContext, plan);
    }

    private static async IAsyncEnumerable<TElement> ExecuteAsyncEnumerable<TEntity, TElement>(
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
        var projected = CreateSequenceList<TEntity, TElement>(results, queryContext, plan);

        foreach (var entity in projected)
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
        var terminal = ApplyTerminal<TEntity, TTerminal>(results, queryContext, plan);
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
        var filtered = candidates
            .Where(entity => predicates.All(predicate => predicate(entity)))
            .ToList();

        ApplyOrdering(filtered, queryContext, plan);
        ApplyPaging(filtered, queryContext, plan);
        return filtered;
    }

    private static object? CreateSequenceResult<TEntity, TResult>(
        List<TEntity> results,
        QueryContext queryContext,
        JsonColdStoreQueryPlan plan)
        where TEntity : class
    {
        var elementType = TryGetSequenceElementType(typeof(TResult))
            ?? throw Unsupported("The LINQ sequence result shape is not supported.");

        return CreateSequenceListMethod
            .MakeGenericMethod(typeof(TEntity), elementType)
            .Invoke(null, [results, queryContext, plan]);
    }

    private static List<TElement> CreateSequenceList<TEntity, TElement>(
        List<TEntity> results,
        QueryContext queryContext,
        JsonColdStoreQueryPlan plan)
        where TEntity : class
    {
        if (plan.Projection is null)
        {
            if (!typeof(TElement).IsAssignableFrom(typeof(TEntity)))
                throw Unsupported("The LINQ sequence result element type does not match the entity type.");

            return results.Cast<TElement>().ToList();
        }

        var projector = CreateProjection<TEntity, TElement>(queryContext, plan.Projection);
        return results.Select(projector).ToList();
    }

    private static object? ApplyTerminal<TEntity, TTerminal>(
        List<TEntity> results,
        QueryContext queryContext,
        JsonColdStoreQueryPlan plan)
        where TEntity : class
    {
        if (plan.Terminal is JsonColdStoreQueryTerminal.Count
            or JsonColdStoreQueryTerminal.LongCount
            or JsonColdStoreQueryTerminal.Any)
        {
            return plan.Terminal switch
            {
                JsonColdStoreQueryTerminal.Count => results.Count,
                JsonColdStoreQueryTerminal.LongCount => (long)results.Count,
                JsonColdStoreQueryTerminal.Any => results.Count > 0,
                _ => throw Unsupported("The LINQ query terminal is not supported."),
            };
        }

        if (plan.Projection is not null)
        {
            var projected = CreateSequenceList<TEntity, TTerminal>(results, queryContext, plan);
            return ApplyTerminal(projected, plan.Terminal);
        }

        var entities = results.Cast<TTerminal>().ToList();
        return ApplyTerminal(entities, plan.Terminal);
    }

    private static object? ApplyTerminal<TElement>(
        List<TElement> results,
        JsonColdStoreQueryTerminal terminal)
    {
        return terminal switch
        {
            JsonColdStoreQueryTerminal.Sequence => results,
            JsonColdStoreQueryTerminal.First => results.First(),
            JsonColdStoreQueryTerminal.FirstOrDefault => results.FirstOrDefault(),
            JsonColdStoreQueryTerminal.Single => results.Single(),
            JsonColdStoreQueryTerminal.SingleOrDefault => results.SingleOrDefault(),
            _ => throw Unsupported("The LINQ query terminal is not supported."),
        };
    }

    private static Type? TryGetSequenceElementType(Type resultType)
    {
        if (resultType.IsGenericType)
        {
            var genericDefinition = resultType.GetGenericTypeDefinition();
            if (genericDefinition == typeof(IEnumerable<>)
                || genericDefinition == typeof(List<>)
                || genericDefinition == typeof(IReadOnlyList<>))
            {
                return resultType.GetGenericArguments()[0];
            }
        }

        return resultType.GetInterfaces()
            .Where(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(type => type.GetGenericArguments()[0])
            .FirstOrDefault();
    }

    private static void ApplyOrdering<TEntity>(
        List<TEntity> results,
        QueryContext queryContext,
        JsonColdStoreQueryPlan plan)
        where TEntity : class
    {
        IOrderedEnumerable<TEntity>? ordered = null;
        foreach (var ordering in plan.Orderings)
        {
            var selector = CreateKeySelector<TEntity>(queryContext, ordering.KeySelector);
            ordered = ordered is null
                ? ordering.Descending
                    ? results.OrderByDescending(selector)
                    : results.OrderBy(selector)
                : ordering.Descending
                    ? ordered.ThenByDescending(selector)
                    : ordered.ThenBy(selector);
        }

        if (ordered is null)
            return;

        var orderedResults = ordered.ToList();
        results.Clear();
        results.AddRange(orderedResults);
    }

    private static void ApplyPaging<TEntity>(
        List<TEntity> results,
        QueryContext queryContext,
        JsonColdStoreQueryPlan plan)
    {
        if (plan.Skip is null && plan.Take is null)
            return;

        IEnumerable<TEntity> paged = results;
        if (plan.Skip is not null)
            paged = paged.Skip(EvaluateNonNegativeInt(queryContext, plan.Skip, "Skip"));
        if (plan.Take is not null)
            paged = paged.Take(EvaluateNonNegativeInt(queryContext, plan.Take, "Take"));

        var pagedResults = paged.ToList();
        results.Clear();
        results.AddRange(pagedResults);
    }

    private static int EvaluateNonNegativeInt(
        QueryContext queryContext,
        Expression expression,
        string operatorName)
    {
        var value = Convert.ToInt32(Evaluate(queryContext, expression), CultureInfo.InvariantCulture);
        if (value < 0)
            throw new ArgumentOutOfRangeException(operatorName, $"{operatorName} cannot be negative.");

        return value;
    }

    private static Func<TEntity, object?> CreateKeySelector<TEntity>(
        QueryContext queryContext,
        LambdaExpression keySelector)
        where TEntity : class =>
        CompileLambda<TEntity, object?>(queryContext, keySelector);

    private static Func<TEntity, TElement> CreateProjection<TEntity, TElement>(
        QueryContext queryContext,
        LambdaExpression projection)
        where TEntity : class =>
        CompileLambda<TEntity, TElement>(queryContext, projection);

    private static Func<TEntity, TResult> CompileLambda<TEntity, TResult>(
        QueryContext queryContext,
        LambdaExpression expression)
        where TEntity : class
    {
        if (expression.Parameters.Count != 1)
            throw Unsupported("Only single-input LINQ lambdas are supported.");

        var body = new QueryParameterBindingVisitor(queryContext).Visit(expression.Body)
            ?? throw Unsupported("The LINQ expression body is not supported.");
        if (body.Type != typeof(TResult))
            body = Expression.Convert(body, typeof(TResult));

        var lambda = Expression.Lambda<Func<TEntity, TResult>>(
            body,
            (ParameterExpression)expression.Parameters[0]);
        return lambda.Compile();
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
            if (IsSinglePropertyKeySeek(entityDescriptor, seek))
            {
                var entity = await entityStore.ReadEntityAsync<TEntity>(
                        seek.Value!,
                        cancellationToken)
                    .ConfigureAwait(false);
                return entity is null ? [] : [entity];
            }

            var maxResults = TryCreateUnorderedPureSeekLimit(queryContext, plan, seek);
            var indexed = await entityStore.ReadEntitiesByIndexAsync<TEntity>(
                    seek.PropertyName,
                    seek.Value!,
                    cancellationToken,
                    maxResults)
                .ConfigureAwait(false);
            return indexed.ToList();
        }

        var range = TryCreateRangeCandidate(queryContext, entityDescriptor, plan);
        if (range is not null)
        {
            var indexed = await entityStore.ReadEntitiesByIndexedRangeAsync<TEntity>(
                    range.PropertyName,
                    range.Constraints,
                    cancellationToken)
                .ConfigureAwait(false);
            return indexed.ToList();
        }

        var boundedScanLimit = TryCreateUnorderedPureScanLimit(queryContext, plan);
        if (boundedScanLimit is not null)
        {
            var boundedResults = new List<TEntity>();
            if (boundedScanLimit.Value <= 0)
                return boundedResults;

            await foreach (var entity in entityStore.ScanEntitiesAsync<TEntity>(cancellationToken))
            {
                boundedResults.Add(entity);
                if (boundedResults.Count >= boundedScanLimit.Value)
                    break;
            }

            return boundedResults;
        }

        if (!CanExecuteFullScan(options, plan))
        {
            throw Unsupported(
                "LINQ query execution would require a full scan. Declare a single-property index, "
                + "query by primary key, call AsJsonColdStoreExplicitScan with AllowExplicitScans, "
                + "or allow silent scans for small stores.");
        }

        var results = new List<TEntity>();
        await foreach (var entity in entityStore.ScanEntitiesAsync<TEntity>(cancellationToken))
            results.Add(entity);

        return results;
    }

    private static bool CanExecuteFullScan(
        JsonColdStoreOptions options,
        JsonColdStoreQueryPlan plan) =>
        options.FullScanPolicy == JsonColdStoreScanPolicy.AllowSilentScans
        || (options.FullScanPolicy == JsonColdStoreScanPolicy.AllowExplicitScans && plan.ExplicitScan);

    private static int? TryCreateUnorderedPureSeekLimit(
        QueryContext queryContext,
        JsonColdStoreQueryPlan plan,
        JsonColdStoreQuerySeek seek)
    {
        if (plan.Orderings.Count > 0 || plan.Skip is not null)
            return null;

        if (!HasOnlyEquivalentSeekFilter(queryContext, plan, seek))
            return null;

        var take = plan.Take is null
            ? (int?)null
            : EvaluateNonNegativeInt(queryContext, plan.Take, "Take");

        return plan.Terminal switch
        {
            JsonColdStoreQueryTerminal.Sequence when take is not null => take.Value,
            JsonColdStoreQueryTerminal.First => take is null ? 1 : Math.Min(take.Value, 1),
            JsonColdStoreQueryTerminal.FirstOrDefault => take is null ? 1 : Math.Min(take.Value, 1),
            JsonColdStoreQueryTerminal.Any => take is null ? 1 : Math.Min(take.Value, 1),
            _ => null,
        };
    }

    private static int? TryCreateUnorderedPureScanLimit(
        QueryContext queryContext,
        JsonColdStoreQueryPlan plan)
    {
        if (plan.Filters.Count > 0 || plan.Orderings.Count > 0 || plan.Skip is not null)
            return null;

        var take = plan.Take is null
            ? (int?)null
            : EvaluateNonNegativeInt(queryContext, plan.Take, "Take");

        return plan.Terminal switch
        {
            JsonColdStoreQueryTerminal.Sequence when take is not null => take.Value,
            JsonColdStoreQueryTerminal.First => take is null ? 1 : Math.Min(take.Value, 1),
            JsonColdStoreQueryTerminal.FirstOrDefault => take is null ? 1 : Math.Min(take.Value, 1),
            JsonColdStoreQueryTerminal.Any => take is null ? 1 : Math.Min(take.Value, 1),
            _ => null,
        };
    }

    private static bool HasOnlyEquivalentSeekFilter(
        QueryContext queryContext,
        JsonColdStoreQueryPlan plan,
        JsonColdStoreQuerySeek seek)
    {
        if (plan.Filters.Count != 1)
            return false;

        var filterSeek = TryCreateSeek(queryContext, plan.Filters[0]);
        return filterSeek is not null
            && string.Equals(filterSeek.PropertyName, seek.PropertyName, StringComparison.Ordinal)
            && ValuesEqual(filterSeek.Value, seek.Value);
    }

    private static JsonColdStoreQueryRangeCandidate? TryCreateRangeCandidate(
        QueryContext queryContext,
        JsonColdStoreEntityDescriptor entityDescriptor,
        JsonColdStoreQueryPlan plan)
    {
        var indexedRanges = plan.Filters
            .Select(filter => TryCreateRange(queryContext, filter))
            .Where(range => range is not null && CanUseIndexedRange(entityDescriptor, range))
            .Cast<JsonColdStoreQueryRange>()
            .GroupBy(range => range.PropertyName, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .FirstOrDefault();

        if (indexedRanges is null)
            return null;

        var constraints = indexedRanges
            .Select(range => new JsonColdStoreRangeConstraint(range.Value!, range.OperatorType))
            .ToArray();

        return new JsonColdStoreQueryRangeCandidate(indexedRanges.Key, constraints);
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

        return CompileLambda<TEntity, bool>(queryContext, filter);
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

        if (IsSinglePropertyKeySeek(descriptor, seek))
            return true;

        return descriptor.Indexes.Any(index =>
            index.PropertyNames.Length == 1
            && string.Equals(index.PropertyNames[0], seek.PropertyName, StringComparison.Ordinal));
    }

    private static bool CanUseIndexedRange(
        JsonColdStoreEntityDescriptor descriptor,
        JsonColdStoreQueryRange? range)
    {
        if (range is null || range.Value is null)
            return false;

        return descriptor.Indexes.Any(index =>
            index.PropertyNames.Length == 1
            && string.Equals(index.PropertyNames[0], range.PropertyName, StringComparison.Ordinal));
    }

    private static bool IsSinglePropertyKeySeek(
        JsonColdStoreEntityDescriptor descriptor,
        JsonColdStoreQuerySeek seek) =>
        descriptor.Key.PropertyNames.Count == 1
        && string.Equals(descriptor.Key.PropertyNames[0], seek.PropertyName, StringComparison.Ordinal);

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

    private static JsonColdStoreQueryRange? TryCreateRange(
        QueryContext queryContext,
        LambdaExpression filter)
    {
        if (filter.Body is not BinaryExpression binary
            || binary.NodeType is not (ExpressionType.GreaterThan
                or ExpressionType.GreaterThanOrEqual
                or ExpressionType.LessThan
                or ExpressionType.LessThanOrEqual))
        {
            return null;
        }

        return TryCreateRange(
                queryContext,
                filter.Parameters[0],
                binary.Left,
                binary.Right,
                binary.NodeType)
            ?? TryCreateRange(
                queryContext,
                filter.Parameters[0],
                binary.Right,
                binary.Left,
                InvertRangeOperator(binary.NodeType));
    }

    private static JsonColdStoreQueryRange? TryCreateRange(
        QueryContext queryContext,
        ParameterExpression parameter,
        Expression memberExpression,
        Expression valueExpression,
        ExpressionType operatorType)
    {
        var unwrappedMember = UnwrapConvert(memberExpression);
        if (unwrappedMember is not MemberExpression member
            || member.Expression != parameter
            || member.Member is not PropertyInfo property)
        {
            return null;
        }

        return new JsonColdStoreQueryRange(
            property.Name,
            Evaluate(queryContext, valueExpression),
            operatorType);
    }

    private static ExpressionType InvertRangeOperator(ExpressionType operatorType) =>
        operatorType switch
        {
            ExpressionType.GreaterThan => ExpressionType.LessThan,
            ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
            ExpressionType.LessThan => ExpressionType.GreaterThan,
            ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
            _ => operatorType,
        };

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

    private sealed class QueryParameterBindingVisitor(QueryContext queryContext) : ExpressionVisitor
    {
        protected override Expression VisitExtension(Expression node)
        {
            if (node is QueryParameterExpression parameter)
            {
                var value = queryContext.Parameters[parameter.Name];
                return Expression.Constant(value, node.Type);
            }

            return base.VisitExtension(node);
        }
    }

    private static NotSupportedException Unsupported(string message) =>
        new("JSONColdStore EF provider support is incomplete: " + message);
}

internal sealed record JsonColdStoreQueryPlan(
    Type EntityType,
    IReadOnlyList<LambdaExpression> Filters,
    IReadOnlyList<JsonColdStoreQueryOrdering> Orderings,
    Expression? Skip,
    Expression? Take,
    LambdaExpression? Projection,
    JsonColdStoreQueryTerminal Terminal,
    bool ExplicitScan)
{
    private static readonly MethodInfo ExplicitScanMethod =
        typeof(JsonColdStoreQueryableExtensions)
            .GetMethod(nameof(JsonColdStoreQueryableExtensions.AsJsonColdStoreExplicitScan))
        ?? throw new InvalidOperationException("The JSONColdStore query planner is misconfigured.");

    internal static JsonColdStoreQueryPlan Create(Expression query)
    {
        var builder = Parse(query);
        return new JsonColdStoreQueryPlan(
            builder.EntityType,
            builder.Filters,
            builder.Orderings,
            builder.Skip,
            builder.Take,
            builder.Projection,
            builder.Terminal,
            builder.ExplicitScan);
    }

    private static JsonColdStoreQueryPlanBuilder Parse(Expression expression)
    {
        if (expression is EntityQueryRootExpression root)
            return new JsonColdStoreQueryPlanBuilder(root.EntityType.ClrType);

        if (expression is not MethodCallExpression call)
            throw Unsupported("The LINQ query expression is not supported.");

        if (IsExplicitScanCall(call))
        {
            var builder = Parse(call.Arguments[0]);
            builder.ExplicitScan = true;
            return builder;
        }

        var methodName = call.Method.Name;
        switch (methodName)
        {
            case nameof(Queryable.Where):
            {
                var builder = Parse(call.Arguments[0]);
                if (builder.Projection is not null)
                    throw Unsupported("Filtering after projection is not supported.");

                AddFilters(builder, call.Arguments[1]);
                return builder;
            }

            case nameof(Queryable.OrderBy):
            case nameof(Queryable.OrderByDescending):
            case nameof(Queryable.ThenBy):
            case nameof(Queryable.ThenByDescending):
            {
                var builder = Parse(call.Arguments[0]);
                if (builder.Projection is not null)
                    throw Unsupported("Ordering after projection is not supported.");
                if (builder.Skip is not null || builder.Take is not null)
                    throw Unsupported("Ordering after paging is not supported.");

                builder.Orderings.Add(new JsonColdStoreQueryOrdering(
                    UnquoteLambda(call.Arguments[1]),
                    methodName is nameof(Queryable.OrderByDescending) or nameof(Queryable.ThenByDescending)));
                return builder;
            }

            case nameof(Queryable.Skip):
            {
                var builder = Parse(call.Arguments[0]);
                if (builder.Skip is not null)
                    throw Unsupported("Only one Skip operator is supported.");

                builder.Skip = call.Arguments[1];
                return builder;
            }

            case nameof(Queryable.Take):
            {
                var builder = Parse(call.Arguments[0]);
                if (builder.Take is not null)
                    throw Unsupported("Only one Take operator is supported.");

                builder.Take = call.Arguments[1];
                return builder;
            }

            case nameof(Queryable.Select):
            {
                var builder = Parse(call.Arguments[0]);
                if (builder.Projection is not null)
                    throw Unsupported("Only one projection operator is supported.");

                builder.Projection = UnquoteLambda(call.Arguments[1]);
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
                {
                    if (builder.Projection is not null)
                        throw Unsupported("Predicate terminals after projection are not supported.");

                    AddFilters(builder, call.Arguments[1]);
                }

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

    private static bool IsExplicitScanCall(MethodCallExpression call) =>
        call.Method.IsGenericMethod
        && call.Method.GetGenericMethodDefinition() == ExplicitScanMethod;

    private static void AddFilters(JsonColdStoreQueryPlanBuilder builder, Expression expression)
    {
        var lambda = UnquoteLambda(expression);
        foreach (var filterBody in SplitConjunction(lambda.Body))
            builder.Filters.Add(Expression.Lambda(filterBody, lambda.Parameters));
    }

    private static IEnumerable<Expression> SplitConjunction(Expression expression)
    {
        if (expression is BinaryExpression { NodeType: ExpressionType.AndAlso } binary)
        {
            foreach (var left in SplitConjunction(binary.Left))
                yield return left;
            foreach (var right in SplitConjunction(binary.Right))
                yield return right;
            yield break;
        }

        yield return expression;
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

    internal List<JsonColdStoreQueryOrdering> Orderings { get; } = [];

    internal Expression? Skip { get; set; }

    internal Expression? Take { get; set; }

    internal LambdaExpression? Projection { get; set; }

    internal JsonColdStoreQueryTerminal Terminal { get; set; } = JsonColdStoreQueryTerminal.Sequence;

    internal bool ExplicitScan { get; set; }
}

internal sealed record JsonColdStoreQueryOrdering(LambdaExpression KeySelector, bool Descending);

internal sealed record JsonColdStoreQuerySeek(string PropertyName, object? Value);

internal sealed record JsonColdStoreQueryRange(
    string PropertyName,
    object? Value,
    ExpressionType OperatorType);

internal sealed record JsonColdStoreQueryRangeCandidate(
    string PropertyName,
    IReadOnlyList<JsonColdStoreRangeConstraint> Constraints);

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
