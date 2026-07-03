using Microsoft.EntityFrameworkCore.Query;

namespace JSONColdStore.Infrastructure;

internal sealed class JsonColdStoreQueryContextFactory(QueryContextDependencies dependencies)
    : IQueryContextFactory
{
    public QueryContext Create() => new JsonColdStoreQueryContext(dependencies);
}

internal sealed class JsonColdStoreQueryContext(QueryContextDependencies dependencies)
    : QueryContext(dependencies);
