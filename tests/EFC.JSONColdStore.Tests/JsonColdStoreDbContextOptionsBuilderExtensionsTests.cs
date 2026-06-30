using EFC.JSONColdStore;
using EFC.JSONColdStore.Infrastructure;
using EFC.JSONColdStore.Storage;
using Microsoft.EntityFrameworkCore;

namespace EFC.JSONColdStore.Tests;

public sealed class JsonColdStoreDbContextOptionsBuilderExtensionsTests
{
    [Fact]
    public void UseJsonColdStoreDatabaseStoresValidatedProviderOptions()
    {
        var directory = TestDirectory("provider-options");
        var builder = new DbContextOptionsBuilder();

        builder.UseJsonColdStoreDatabase(directory);

        var extension = builder.Options.FindExtension<JsonColdStoreOptionsExtension>();

        Assert.NotNull(extension);
        Assert.Equal(Path.GetFullPath(directory), extension.Options.DatabaseDirectory);
        Assert.Equal(JsonColdStoreCompression.Auto, extension.Options.Compression);
        Assert.Equal(JsonColdStoreStartupMode.MetadataOnly, extension.Options.StartupMode);
        Assert.Equal(JsonColdStoreScanPolicy.FailUnlessExplicit, extension.Options.FullScanPolicy);
        Assert.True(extension.Info.IsDatabaseProvider);
        Assert.Equal("using JSONColdStore ", extension.Info.LogFragment);
    }

    [Fact]
    public void UseJsonColdStoreDatabaseAppliesAdvancedConfigurationDelegate()
    {
        using var key = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);
        var builder = new DbContextOptionsBuilder<TestDbContext>();

        builder.UseJsonColdStoreDatabase(
            TestDirectory("advanced-provider-options"),
            store => store
                .UseCompression(JsonColdStoreCompression.Brotli)
                .UseEncryption(new JsonColdStoreEncryptionOptions
                {
                    Key = key,
                    KeyId = "test-key",
                    RequireEncryptedStore = true,
                })
                .UseFullScanPolicy(JsonColdStoreScanPolicy.AllowExplicitScans));

        var extension = builder.Options.FindExtension<JsonColdStoreOptionsExtension>();

        Assert.NotNull(extension);
        Assert.Equal(JsonColdStoreCompression.Brotli, extension.Options.Compression);
        Assert.Equal("test-key", extension.Options.Encryption?.KeyId);
        Assert.True(extension.Options.Encryption?.RequireEncryptedStore);
        Assert.Equal(JsonColdStoreScanPolicy.AllowExplicitScans, extension.Options.FullScanPolicy);
    }

    [Fact]
    public void UseJsonColdStoreDatabaseRejectsUnsafeDatabaseDirectory()
    {
        var builder = new DbContextOptionsBuilder();

        Assert.Throws<ArgumentException>(() => builder.UseJsonColdStoreDatabase("   "));
    }

    [Fact]
    public void DebugInfoDoesNotExposeDirectoryOrKeyId()
    {
        using var key = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);
        var directory = TestDirectory("sensitive-provider-options");
        var builder = new DbContextOptionsBuilder();
        builder.UseJsonColdStoreDatabase(
            directory,
            store => store.UseEncryption(new JsonColdStoreEncryptionOptions
            {
                Key = key,
                KeyId = "do-not-log",
            }));

        var extension = builder.Options.FindExtension<JsonColdStoreOptionsExtension>();
        var debugInfo = new Dictionary<string, string>();

        extension!.Info.PopulateDebugInfo(debugInfo);
        var debugText = string.Join(' ', debugInfo.Keys.Concat(debugInfo.Values));

        Assert.DoesNotContain(directory, debugText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("do-not-log", debugText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("True", debugInfo["JSONColdStore:Encrypted"]);
    }

    [Fact]
    public void ConfiguredContextReportsJsonColdStoreProviderName()
    {
        var builder = new DbContextOptionsBuilder<TestDbContext>();
        builder.UseJsonColdStoreDatabase(TestDirectory("provider-name"));

        using var context = new TestDbContext(builder.Options);

        Assert.Equal("EFC.JSONColdStore", context.Database.ProviderName);
    }

    [Fact]
    public void EnsureCreatedCreatesRootMetadataOnce()
    {
        var directory = TestDirectory("ensure-created-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<TestDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new TestDbContext(builder.Options);

        Assert.True(context.Database.EnsureCreated());
        Assert.False(context.Database.EnsureCreated());
        Assert.True(File.Exists(Path.Combine(directory, "_store.json")));
    }

    [Fact]
    public void BeginTransactionThrowsUntilTransactionsAreImplemented()
    {
        var builder = new DbContextOptionsBuilder<TestDbContext>();
        builder.UseJsonColdStoreDatabase(TestDirectory("transaction-unsupported"));
        using var context = new TestDbContext(builder.Options);

        var exception = Assert.Throws<NotSupportedException>(
            () => context.Database.BeginTransaction());

        Assert.Contains("Explicit transactions are not implemented yet", exception.Message);
    }

    [Fact]
    public async Task SaveChangesPersistsAddedEntityThroughStorageSession()
    {
        var directory = TestDirectory("savechanges-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        var entity = new WritableEntity
        {
            Id = Guid.Parse("4c263762-5d19-49d8-a5a3-62af6de12a96"),
            Value = "saved through EF",
        };

        using (var context = new WritableDbContext(builder.Options))
        {
            context.Entities.Add(entity);
            Assert.Equal(1, context.SaveChanges());
        }

        var storageOptions = new JsonColdStoreOptionsBuilder(directory)
            .UseFsyncOnWrite(false)
            .Build();
        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(storageOptions);
        var model = CreateWritableModel();
        var store = new JsonColdStoreEntityRecordStore(
            session,
            JsonColdStoreModelDescriptor.Create(model));

        var read = await store.ReadEntityAsync<WritableEntity>(entity.Id);
        Assert.NotNull(read);
        Assert.Equal(entity.Id, read.Id);
        Assert.Equal(entity.Value, read.Value);
    }

    [Fact]
    public async Task SaveChangesDeletesEntityThroughStorageSession()
    {
        var directory = TestDirectory("delete-unsupported-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        var entity = new WritableEntity
        {
            Id = Guid.NewGuid(),
            Value = "delete me later",
        };

        using var context = new WritableDbContext(builder.Options);
        context.Entities.Add(entity);
        context.SaveChanges();
        context.Entities.Remove(entity);
        Assert.Equal(1, context.SaveChanges());

        var storageOptions = new JsonColdStoreOptionsBuilder(directory)
            .UseFsyncOnWrite(false)
            .Build();
        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(storageOptions);
        var model = CreateWritableModel();
        var store = new JsonColdStoreEntityRecordStore(
            session,
            JsonColdStoreModelDescriptor.Create(model));

        var read = await store.ReadEntityAsync<WritableEntity>(entity.Id);
        Assert.Null(read);
    }

    [Fact]
    public async Task ReadJsonColdStoreAsyncReadsSavedEntityByPrimaryKey()
    {
        var directory = TestDirectory("facade-read-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        var entity = new WritableEntity
        {
            Id = Guid.Parse("4df6776d-4747-49ef-87d8-f0f60a19016a"),
            Value = "read through facade",
        };

        using var context = new WritableDbContext(builder.Options);
        context.Entities.Add(entity);
        context.SaveChanges();

        var read = await context.Database.ReadJsonColdStoreAsync<WritableEntity>(entity.Id);

        Assert.NotNull(read);
        Assert.Equal(entity.Id, read.Id);
        Assert.Equal(entity.Value, read.Value);
    }

    [Fact]
    public async Task ReadJsonColdStoreAsyncReturnsNullForMissingPrimaryKey()
    {
        var directory = TestDirectory("facade-read-missing-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);
        context.Database.EnsureCreated();

        var read = await context.Database.ReadJsonColdStoreAsync<WritableEntity>(Guid.NewGuid());

        Assert.Null(read);
    }

    [Fact]
    public async Task ScanJsonColdStoreAsyncExplicitlyReadsAllSavedEntities()
    {
        var directory = TestDirectory("facade-scan-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);
        context.Entities.AddRange(
            new WritableEntity
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000001"),
                Value = "first",
            },
            new WritableEntity
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000002"),
                Value = "second",
            });
        context.SaveChanges();

        var scanned = await context.Database.ScanJsonColdStoreAsync<WritableEntity>();

        Assert.Equal(["first", "second"], scanned.Select(entity => entity.Value).Order().ToArray());
    }

    [Fact]
    public async Task ScanJsonColdStoreAsyncReturnsEmptyForEntityWithoutRecords()
    {
        var directory = TestDirectory("facade-scan-empty-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);
        context.Database.EnsureCreated();

        var scanned = await context.Database.ScanJsonColdStoreAsync<WritableEntity>();

        Assert.Empty(scanned);
    }

    [Fact]
    public void QueryThrowsClearUnsupportedMessageUntilQueryPipelineExists()
    {
        var directory = TestDirectory("query-unsupported-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);

        var exception = Assert.Throws<NotSupportedException>(() => context.Entities.ToList());

        Assert.Contains("LINQ query", exception.Message);
    }

    private static string TestDirectory(string name) =>
        Path.Combine(Path.GetTempPath(), "jsoncoldstore-tests", name);

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options);

    private static Microsoft.EntityFrameworkCore.Metadata.IModel CreateWritableModel()
    {
        var modelBuilder = new ModelBuilder(new Microsoft.EntityFrameworkCore.Metadata.Conventions.ConventionSet());
        modelBuilder.Entity<WritableEntity>(entity => entity.HasKey(value => value.Id));
        return modelBuilder.FinalizeModel();
    }

    private sealed class WritableDbContext(DbContextOptions<WritableDbContext> options) : DbContext(options)
    {
        public DbSet<WritableEntity> Entities => Set<WritableEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<WritableEntity>(entity => entity.HasKey(value => value.Id));
        }
    }

    private sealed class WritableEntity
    {
        public Guid Id { get; set; }

        public string Value { get; set; } = string.Empty;
    }
}
