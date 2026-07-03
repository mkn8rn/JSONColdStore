using JSONColdStore.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace JSONColdStore;

/// <summary>
/// Extension methods for configuring DbContext instances to use JSONColdStore.
/// </summary>
public static class JsonColdStoreDbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Configures the context to use a JSONColdStore database rooted at the given directory.
    /// </summary>
    public static DbContextOptionsBuilder UseJsonColdStoreDatabase(
        this DbContextOptionsBuilder optionsBuilder,
        string databaseDirectory,
        Action<JsonColdStoreOptionsBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        var storeOptionsBuilder = new JsonColdStoreOptionsBuilder(databaseDirectory);
        configure?.Invoke(storeOptionsBuilder);

        var extension = new JsonColdStoreOptionsExtension(storeOptionsBuilder.Build());
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);

        return optionsBuilder;
    }

    /// <summary>
    /// Configures the context to use a JSONColdStore database rooted at the given directory.
    /// </summary>
    public static DbContextOptionsBuilder<TContext> UseJsonColdStoreDatabase<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        string databaseDirectory,
        Action<JsonColdStoreOptionsBuilder>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        UseJsonColdStoreDatabase((DbContextOptionsBuilder)optionsBuilder, databaseDirectory, configure);
        return optionsBuilder;
    }
}
