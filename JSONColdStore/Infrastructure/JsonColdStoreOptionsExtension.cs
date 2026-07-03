using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JSONColdStore.Infrastructure;

internal sealed class JsonColdStoreOptionsExtension(JsonColdStoreOptions options) : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public JsonColdStoreOptions Options { get; } = options
        ?? throw new ArgumentNullException(nameof(options));

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<JsonColdStoreTransactionManager>();
        services.TryAddScoped<IDbContextTransactionManager>(provider =>
            provider.GetRequiredService<JsonColdStoreTransactionManager>());
        services.TryAddScoped<IRelationalTransactionManager>(provider =>
            provider.GetRequiredService<JsonColdStoreTransactionManager>());

        new EntityFrameworkServicesBuilder(services)
            .TryAdd<LoggingDefinitions, JsonColdStoreLoggingDefinitions>()
            .TryAdd<IDatabaseProvider, DatabaseProvider<JsonColdStoreOptionsExtension>>()
            .TryAdd<IDatabase, JsonColdStoreDatabase>()
            .TryAdd<IDatabaseCreator, JsonColdStoreDatabaseCreator>()
            .TryAdd<IQueryContextFactory, JsonColdStoreQueryContextFactory>()
            .TryAdd<IProviderConventionSetBuilder, JsonColdStoreConventionSetBuilder>()
            .TryAdd<ITypeMappingSource, JsonColdStoreTypeMappingSource>()
            .TryAdd<IExecutionStrategyFactory, JsonColdStoreExecutionStrategyFactory>()
            .TryAddCoreServices();
    }

    public void ApplyDefaults(IDbContextOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
    }

    public void Validate(IDbContextOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
    }

    private sealed class ExtensionInfo(JsonColdStoreOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        private const string DebugPrefix = "JSONColdStore:";

        public override bool IsDatabaseProvider => true;

        public override string LogFragment => "using JSONColdStore ";

        private JsonColdStoreOptionsExtension JsonColdStoreExtension
            => (JsonColdStoreOptionsExtension)base.Extension;

        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            ArgumentNullException.ThrowIfNull(debugInfo);

            var options = JsonColdStoreExtension.Options;
            debugInfo[DebugPrefix + "Configured"] = "1";
            debugInfo[DebugPrefix + "Compression"] = options.Compression.ToString();
            debugInfo[DebugPrefix + "Encrypted"] = (options.Encryption is not null).ToString();
            debugInfo[DebugPrefix + "StartupMode"] = options.StartupMode.ToString();
            debugInfo[DebugPrefix + "FullScanPolicy"] = options.FullScanPolicy.ToString();
        }
    }
}

internal sealed class JsonColdStoreLoggingDefinitions : LoggingDefinitions;
