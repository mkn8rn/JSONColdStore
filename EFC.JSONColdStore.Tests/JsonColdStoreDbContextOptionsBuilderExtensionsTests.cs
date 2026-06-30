using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    public void EnsureCreatedCreatesRootAndModelMetadataOnce()
    {
        var directory = TestDirectory("ensure-created-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);

        Assert.True(context.Database.EnsureCreated());
        Assert.False(context.Database.EnsureCreated());
        Assert.True(File.Exists(Path.Combine(directory, "_store.json")));
        Assert.True(File.Exists(Path.Combine(directory, "_model.json")));
    }

    [Fact]
    public async Task EnsureDeletedAsyncRemovesCreatedStore()
    {
        var directory = TestDirectory("ensure-deleted-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);
        context.Database.EnsureCreated();
        await File.WriteAllTextAsync(Path.Combine(directory, "extra.tmp"), "delete me");

        Assert.True(await context.Database.EnsureDeletedAsync());

        Assert.False(Directory.Exists(directory));
        Assert.False(await context.Database.EnsureDeletedAsync());
    }

    [Fact]
    public void EnsureDeletedReturnsFalseForMissingDirectory()
    {
        var directory = TestDirectory("ensure-deleted-missing-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);

        Assert.False(context.Database.EnsureDeleted());
    }

    [Fact]
    public async Task EnsureDeletedAsyncHonorsCancellation()
    {
        var directory = TestDirectory("ensure-deleted-canceled-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.Database.EnsureDeletedAsync(cts.Token));
    }

    [Fact]
    public async Task EnsureDeletedRejectsActiveWriterLock()
    {
        var directory = TestDirectory("ensure-deleted-locked-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        var storageOptions = new JsonColdStoreOptionsBuilder(directory)
            .UseFsyncOnWrite(false)
            .Build();
        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(storageOptions);
        using var context = new WritableDbContext(builder.Options);

        var exception = Assert.Throws<InvalidOperationException>(
            () => context.Database.EnsureDeleted());

        Assert.Contains("writer lock", exception.Message);
        Assert.True(Directory.Exists(directory));
        Assert.True(File.Exists(Path.Combine(directory, "_store.json")));
    }

    [Fact]
    public void EnsureCreatedRejectsChangedModelCatalog()
    {
        var directory = TestDirectory("ensure-created-catalog-mismatch-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using (var context = new WritableDbContext(builder.Options))
        {
            Assert.True(context.Database.EnsureCreated());
        }

        var changedBuilder = new DbContextOptionsBuilder<WritableDbContextWithoutIndex>();
        changedBuilder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var changedContext = new WritableDbContextWithoutIndex(changedBuilder.Options);

        var exception = Assert.Throws<InvalidOperationException>(
            () => changedContext.Database.EnsureCreated());

        Assert.Contains("model catalog", exception.Message);
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
    public void SaveChangesRejectsChangedModelCatalog()
    {
        var directory = TestDirectory("savechanges-catalog-mismatch-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using (var context = new WritableDbContext(builder.Options))
        {
            context.Entities.Add(new WritableEntity
            {
                Id = Guid.Parse("60000000-0000-0000-0000-000000000001"),
                Value = "first shape",
            });
            context.SaveChanges();
        }

        var changedBuilder = new DbContextOptionsBuilder<WritableDbContextWithoutIndex>();
        changedBuilder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var changedContext = new WritableDbContextWithoutIndex(changedBuilder.Options);
        changedContext.Entities.Add(new WritableEntity
        {
            Id = Guid.Parse("60000000-0000-0000-0000-000000000002"),
            Value = "changed shape",
        });

        var exception = Assert.Throws<InvalidOperationException>(() => changedContext.SaveChanges());

        Assert.Contains("model catalog", exception.Message);
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
    public async Task ReadJsonColdStoreIndexAsyncUsesDeclaredIndexFiles()
    {
        var directory = TestDirectory("facade-index-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);
        context.Entities.AddRange(
            new WritableEntity
            {
                Id = Guid.Parse("40000000-0000-0000-0000-000000000001"),
                Value = "match",
            },
            new WritableEntity
            {
                Id = Guid.Parse("40000000-0000-0000-0000-000000000002"),
                Value = "skip",
            });
        context.SaveChanges();

        var matches = await context.Database.ReadJsonColdStoreIndexAsync<WritableEntity>("Value", "match");

        Assert.Single(matches);
        Assert.Equal("match", matches[0].Value);
    }

    [Fact]
    public async Task ReadJsonColdStoreIndexAsyncRejectsUndeclaredIndex()
    {
        var directory = TestDirectory("facade-index-missing-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);
        context.Database.EnsureCreated();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.Database.ReadJsonColdStoreIndexAsync<WritableEntity>("Missing", "value"));
    }

    [Fact]
    public async Task RebuildJsonColdStoreIndexesAsyncRepairsDeletedIndexFile()
    {
        var directory = TestDirectory("facade-index-rebuild-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);
        context.Entities.Add(new WritableEntity
        {
            Id = Guid.Parse("50000000-0000-0000-0000-000000000001"),
            Value = "repair",
        });
        context.SaveChanges();
        var indexPath = Path.Combine(
            directory,
            "entities",
            JsonColdStoreNameEncoder.EncodePathSegment(typeof(WritableEntity).FullName!),
            "indexes",
            JsonColdStoreNameEncoder.EncodePathSegment("Value") + ".json");
        File.Delete(indexPath);

        Assert.Empty(await context.Database.ReadJsonColdStoreIndexAsync<WritableEntity>("Value", "repair"));
        var rebuilt = await context.Database.RebuildJsonColdStoreIndexesAsync<WritableEntity>();
        var repaired = await context.Database.ReadJsonColdStoreIndexAsync<WritableEntity>("Value", "repair");

        Assert.Equal(1, rebuilt);
        Assert.Single(repaired);
        Assert.Equal("repair", repaired[0].Value);
    }

    [Fact]
    public async Task VerifyJsonColdStoreAsyncCountsNewFormatRecords()
    {
        var directory = TestDirectory("verify-new-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);
        context.Entities.AddRange(
            new WritableEntity
            {
                Id = Guid.Parse("51000000-0000-0000-0000-000000000001"),
                Value = "first",
            },
            new WritableEntity
            {
                Id = Guid.Parse("51000000-0000-0000-0000-000000000002"),
                Value = "second",
            });
        context.SaveChanges();

        var result = await context.Database.VerifyJsonColdStoreAsync();

        Assert.Equal(2, result.VerifiedRecords);
        Assert.Equal(0, result.VerifiedLegacyRecords);
    }

    [Fact]
    public async Task VerifyJsonColdStoreAsyncCountsLegacyRecordsWithoutCreatingMetadata()
    {
        var directory = TestDirectory("verify-legacy-" + Guid.NewGuid().ToString("N"));
        var id = Guid.Parse("51000000-0000-0000-0000-000000000003");
        await WriteLegacyEntityAsync(directory, new WritableEntity
        {
            Id = id,
            Value = "legacy",
        });
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        var result = await context.Database.VerifyJsonColdStoreAsync();

        Assert.Equal(0, result.VerifiedRecords);
        Assert.Equal(1, result.VerifiedLegacyRecords);
        Assert.False(File.Exists(Path.Combine(directory, "_store.json")));
        Assert.False(File.Exists(Path.Combine(directory, "_model.json")));
    }

    [Fact]
    public async Task VerifyJsonColdStoreAsyncUsesConfiguredKeyForEncryptedLegacyRecords()
    {
        var directory = TestDirectory("verify-encrypted-legacy-" + Guid.NewGuid().ToString("N"));
        using var key = JsonColdStoreEncryptionKey.FromBytes(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        var id = Guid.Parse("51000000-0000-0000-0000-000000000004");
        await WriteLegacyEntityAsync(
            directory,
            new WritableEntity
            {
                Id = id,
                Value = "encrypted legacy",
            },
            key);
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(
            directory,
            store => store
                .UseFsyncOnWrite(false)
                .UseEncryptionKey(key));

        using var context = new WritableDbContext(builder.Options);
        var result = await context.Database.VerifyJsonColdStoreAsync();

        Assert.Equal(0, result.VerifiedRecords);
        Assert.Equal(1, result.VerifiedLegacyRecords);
    }

    [Fact]
    public async Task VerifyJsonColdStoreAsyncRejectsChecksumCorruptionWithoutQuarantine()
    {
        var directory = TestDirectory("verify-corrupt-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        var id = Guid.Parse("51000000-0000-0000-0000-000000000005");
        using var context = new WritableDbContext(builder.Options);
        context.Entities.Add(new WritableEntity
        {
            Id = id,
            Value = "corrupt me",
        });
        context.SaveChanges();
        var recordPath = await CorruptStoredRecordAsync(directory, id);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => context.Database.VerifyJsonColdStoreAsync());

        Assert.True(File.Exists(recordPath));
        Assert.False(Directory.Exists(Path.Combine(directory, "_quarantine", "records")));
    }

    [Fact]
    public async Task ReadJsonColdStoreAsyncReadsLegacyPlainEntityWithoutCreatingMetadata()
    {
        var directory = TestDirectory("legacy-plain-" + Guid.NewGuid().ToString("N"));
        var id = Guid.Parse("70000000-0000-0000-0000-000000000001");
        await WriteLegacyEntityAsync(directory, new WritableEntity
        {
            Id = id,
            Value = "plain legacy",
        });
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        var read = await context.Database.ReadJsonColdStoreAsync<WritableEntity>(id);

        Assert.NotNull(read);
        Assert.Equal("plain legacy", read.Value);
        Assert.False(File.Exists(Path.Combine(directory, "_store.json")));
        Assert.False(File.Exists(Path.Combine(directory, "_model.json")));
    }

    [Fact]
    public async Task ReadJsonColdStoreAsyncReadsLegacyEncryptedEntityWithConfiguredKey()
    {
        var directory = TestDirectory("legacy-encrypted-" + Guid.NewGuid().ToString("N"));
        using var key = JsonColdStoreEncryptionKey.FromBytes(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        var id = Guid.Parse("70000000-0000-0000-0000-000000000002");
        await WriteLegacyEntityAsync(
            directory,
            new WritableEntity
            {
                Id = id,
                Value = "encrypted legacy",
            },
            key);
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(
            directory,
            store => store
                .UseFsyncOnWrite(false)
                .UseEncryptionKey(key));

        using var context = new WritableDbContext(builder.Options);
        var read = await context.Database.ReadJsonColdStoreAsync<WritableEntity>(id);

        Assert.NotNull(read);
        Assert.Equal("encrypted legacy", read.Value);
    }

    [Fact]
    public async Task ReadJsonColdStoreIndexAsyncUsesLegacyIndexShard()
    {
        var directory = TestDirectory("legacy-index-" + Guid.NewGuid().ToString("N"));
        var matchId = Guid.Parse("70000000-0000-0000-0000-000000000003");
        var skipId = Guid.Parse("70000000-0000-0000-0000-000000000004");
        await WriteLegacyEntityAsync(directory, new WritableEntity
        {
            Id = matchId,
            Value = "legacy-match",
        });
        await WriteLegacyEntityAsync(directory, new WritableEntity
        {
            Id = skipId,
            Value = "legacy-skip",
        });
        await WriteLegacyIndexAsync(directory, "Value", "legacy-match", [matchId.ToString()]);
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        var matches = await context.Database.ReadJsonColdStoreIndexAsync<WritableEntity>(
            "Value",
            "legacy-match");

        Assert.Single(matches);
        Assert.Equal(matchId, matches[0].Id);
    }

    [Fact]
    public async Task SaveChangesRetiresSameKeyLegacyRecordAfterNewFormatWrite()
    {
        var directory = TestDirectory("legacy-retire-" + Guid.NewGuid().ToString("N"));
        var id = Guid.Parse("70000000-0000-0000-0000-000000000005");
        await WriteLegacyEntityAsync(directory, new WritableEntity
        {
            Id = id,
            Value = "old",
        });
        await WriteLegacyIndexAsync(directory, "Value", "old", [id.ToString()]);
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        context.Entities.Update(new WritableEntity
        {
            Id = id,
            Value = "new",
        });
        Assert.Equal(1, context.SaveChanges());

        Assert.False(File.Exists(Path.Combine(directory, nameof(WritableEntity), $"{id}.json")));
        var read = await context.Database.ReadJsonColdStoreAsync<WritableEntity>(id);
        var staleIndexResults = await context.Database.ReadJsonColdStoreIndexAsync<WritableEntity>("Value", "old");
        Assert.NotNull(read);
        Assert.Equal("new", read.Value);
        Assert.Empty(staleIndexResults);
    }

    [Fact]
    public void LinqSingleOrDefaultReadsByPrimaryKey()
    {
        var directory = TestDirectory("query-primary-key-" + Guid.NewGuid().ToString("N"));
        var id = Guid.Parse("80000000-0000-0000-0000-000000000001");
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        context.Entities.AddRange(
            new WritableEntity
            {
                Id = id,
                Value = "match",
            },
            new WritableEntity
            {
                Id = Guid.Parse("80000000-0000-0000-0000-000000000002"),
                Value = "skip",
            });
        context.SaveChanges();

        var read = context.Entities.SingleOrDefault(entity => entity.Id == id);

        Assert.NotNull(read);
        Assert.Equal("match", read.Value);
    }

    [Fact]
    public void LinqWhereUsesDeclaredSinglePropertyIndex()
    {
        var directory = TestDirectory("query-index-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        context.Entities.AddRange(
            new WritableEntity
            {
                Id = Guid.Parse("80000000-0000-0000-0000-000000000003"),
                Value = "match",
            },
            new WritableEntity
            {
                Id = Guid.Parse("80000000-0000-0000-0000-000000000004"),
                Value = "skip",
            });
        context.SaveChanges();

        var matches = context.Entities.Where(entity => entity.Value == "match").ToList();

        Assert.Single(matches);
        Assert.Equal("match", matches[0].Value);
    }

    [Fact]
    public async Task LinqWhereUsesLegacyIndexShardWithoutCreatingMetadata()
    {
        var directory = TestDirectory("query-legacy-index-" + Guid.NewGuid().ToString("N"));
        var id = Guid.Parse("80000000-0000-0000-0000-000000000007");
        await WriteLegacyEntityAsync(directory, new WritableEntity
        {
            Id = id,
            Value = "legacy-query",
        });
        await WriteLegacyIndexAsync(directory, "Value", "legacy-query", [id.ToString()]);
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        var matches = context.Entities.Where(entity => entity.Value == "legacy-query").ToList();

        Assert.Single(matches);
        Assert.Equal(id, matches[0].Id);
        Assert.False(File.Exists(Path.Combine(directory, "_store.json")));
        Assert.False(File.Exists(Path.Combine(directory, "_model.json")));
    }

    [Fact]
    public async Task LinqToListAsyncUsesDeclaredSinglePropertyIndex()
    {
        var directory = TestDirectory("query-async-index-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        context.Entities.AddRange(
            new WritableEntity
            {
                Id = Guid.Parse("81000000-0000-0000-0000-000000000001"),
                Value = "match",
            },
            new WritableEntity
            {
                Id = Guid.Parse("81000000-0000-0000-0000-000000000002"),
                Value = "skip",
            });
        context.SaveChanges();

        var matches = await context.Entities
            .Where(entity => entity.Value == "match")
            .ToListAsync();

        Assert.Single(matches);
        Assert.Equal("match", matches[0].Value);
    }

    [Fact]
    public async Task LinqSingleOrDefaultAsyncReadsByPrimaryKey()
    {
        var directory = TestDirectory("query-async-primary-key-" + Guid.NewGuid().ToString("N"));
        var id = Guid.Parse("81000000-0000-0000-0000-000000000003");
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        context.Entities.Add(new WritableEntity
        {
            Id = id,
            Value = "async-key",
        });
        context.SaveChanges();

        var read = await context.Entities.SingleOrDefaultAsync(entity => entity.Id == id);

        Assert.NotNull(read);
        Assert.Equal("async-key", read.Value);
    }

    [Fact]
    public async Task LinqCountAsyncUsesDeclaredSinglePropertyIndex()
    {
        var directory = TestDirectory("query-async-count-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        context.Entities.AddRange(
            new WritableEntity
            {
                Id = Guid.Parse("81000000-0000-0000-0000-000000000007"),
                Value = "count-me",
            },
            new WritableEntity
            {
                Id = Guid.Parse("81000000-0000-0000-0000-000000000008"),
                Value = "count-me",
            },
            new WritableEntity
            {
                Id = Guid.Parse("81000000-0000-0000-0000-000000000009"),
                Value = "skip",
            });
        context.SaveChanges();

        var count = await context.Entities.CountAsync(entity => entity.Value == "count-me");

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task LinqToListAsyncUsesLegacyIndexShardWithoutCreatingMetadata()
    {
        var directory = TestDirectory("query-async-legacy-index-" + Guid.NewGuid().ToString("N"));
        var id = Guid.Parse("81000000-0000-0000-0000-000000000004");
        await WriteLegacyEntityAsync(directory, new WritableEntity
        {
            Id = id,
            Value = "async-legacy-query",
        });
        await WriteLegacyIndexAsync(directory, "Value", "async-legacy-query", [id.ToString()]);
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        var matches = await context.Entities
            .Where(entity => entity.Value == "async-legacy-query")
            .ToListAsync();

        Assert.Single(matches);
        Assert.Equal(id, matches[0].Id);
        Assert.False(File.Exists(Path.Combine(directory, "_store.json")));
        Assert.False(File.Exists(Path.Combine(directory, "_model.json")));
    }

    [Fact]
    public async Task LinqToListAsyncScansWhenSilentScansAreAllowed()
    {
        var directory = TestDirectory("query-async-silent-scan-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(
            directory,
            store => store
                .UseFsyncOnWrite(false)
                .UseFullScanPolicy(JsonColdStoreScanPolicy.AllowSilentScans));

        using var context = new WritableDbContext(builder.Options);
        context.Entities.AddRange(
            new WritableEntity
            {
                Id = Guid.Parse("81000000-0000-0000-0000-000000000005"),
                Value = "first",
            },
            new WritableEntity
            {
                Id = Guid.Parse("81000000-0000-0000-0000-000000000006"),
                Value = "second",
            });
        context.SaveChanges();

        var entities = await context.Entities.ToListAsync();
        var values = entities
            .Select(entity => entity.Value)
            .Order()
            .ToArray();

        Assert.Equal(["first", "second"], values);
    }

    [Fact]
    public async Task LinqToListAsyncThrowsWhenFullScanWouldBeRequiredByDefault()
    {
        var directory = TestDirectory("query-async-unsupported-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => context.Entities.ToListAsync());

        Assert.Contains("full scan", exception.Message);
    }

    [Fact]
    public void LinqToListScansWhenSilentScansAreAllowed()
    {
        var directory = TestDirectory("query-silent-scan-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(
            directory,
            store => store
                .UseFsyncOnWrite(false)
                .UseFullScanPolicy(JsonColdStoreScanPolicy.AllowSilentScans));

        using var context = new WritableDbContext(builder.Options);
        context.Entities.AddRange(
            new WritableEntity
            {
                Id = Guid.Parse("80000000-0000-0000-0000-000000000005"),
                Value = "first",
            },
            new WritableEntity
            {
                Id = Guid.Parse("80000000-0000-0000-0000-000000000006"),
                Value = "second",
            });
        context.SaveChanges();

        var values = context.Entities
            .ToList()
            .Select(entity => entity.Value)
            .Order()
            .ToArray();

        Assert.Equal(["first", "second"], values);
    }

    [Fact]
    public void LinqToListThrowsWhenFullScanWouldBeRequiredByDefault()
    {
        var directory = TestDirectory("query-unsupported-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);

        var exception = Assert.Throws<NotSupportedException>(() => context.Entities.ToList());

        Assert.Contains("full scan", exception.Message);
    }

    [Fact]
    public void LinqProjectionOrdersPagesAndScansWhenSilentScansAreAllowed()
    {
        var directory = TestDirectory("query-projection-page-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(
            directory,
            store => store
                .UseFsyncOnWrite(false)
                .UseFullScanPolicy(JsonColdStoreScanPolicy.AllowSilentScans));
        using var context = new WritableDbContext(builder.Options);
        context.Entities.AddRange(
            new WritableEntity
            {
                Id = Guid.Parse("82000000-0000-0000-0000-000000000001"),
                Value = "b",
            },
            new WritableEntity
            {
                Id = Guid.Parse("82000000-0000-0000-0000-000000000002"),
                Value = "a",
            },
            new WritableEntity
            {
                Id = Guid.Parse("82000000-0000-0000-0000-000000000003"),
                Value = "c",
            });
        context.SaveChanges();

        var values = context.Entities
            .OrderBy(entity => entity.Value)
            .Skip(1)
            .Take(1)
            .Select(entity => entity.Value)
            .ToList();

        Assert.Equal(["b"], values);
    }

    [Fact]
    public void LinqProjectionUsesDeclaredIndexWithoutSilentScan()
    {
        var directory = TestDirectory("query-projection-index-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);
        var id = Guid.Parse("82000000-0000-0000-0000-000000000004");
        context.Entities.AddRange(
            new WritableEntity
            {
                Id = id,
                Value = "match",
            },
            new WritableEntity
            {
                Id = Guid.Parse("82000000-0000-0000-0000-000000000005"),
                Value = "skip",
            });
        context.SaveChanges();

        var read = context.Entities
            .Where(entity => entity.Value == "match")
            .Select(entity => entity.Id)
            .Single();

        Assert.Equal(id, read);
    }

    [Fact]
    public async Task LinqProjectionToListAsyncUsesDeclaredIndexWithoutSilentScan()
    {
        var directory = TestDirectory("query-async-projection-index-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);
        var id = Guid.Parse("82000000-0000-0000-0000-000000000006");
        context.Entities.AddRange(
            new WritableEntity
            {
                Id = id,
                Value = "async-match",
            },
            new WritableEntity
            {
                Id = Guid.Parse("82000000-0000-0000-0000-000000000007"),
                Value = "skip",
            });
        context.SaveChanges();

        var reads = await context.Entities
            .Where(entity => entity.Value == "async-match")
            .Select(entity => entity.Id)
            .ToListAsync();

        Assert.Equal([id], reads);
    }

    [Fact]
    public void LinqOrderingAppliesToIndexedResults()
    {
        var directory = TestDirectory("query-index-ordering-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);
        context.Entities.AddRange(
            new WritableEntity
            {
                Id = Guid.Parse("82000000-0000-0000-0000-000000000008"),
                Value = "group",
            },
            new WritableEntity
            {
                Id = Guid.Parse("82000000-0000-0000-0000-000000000009"),
                Value = "group",
            },
            new WritableEntity
            {
                Id = Guid.Parse("82000000-0000-0000-0000-000000000010"),
                Value = "skip",
            });
        context.SaveChanges();

        var ids = context.Entities
            .Where(entity => entity.Value == "group")
            .OrderByDescending(entity => entity.Id)
            .Select(entity => entity.Id)
            .ToList();

        Assert.Equal(
            [
                Guid.Parse("82000000-0000-0000-0000-000000000009"),
                Guid.Parse("82000000-0000-0000-0000-000000000008"),
            ],
            ids);
    }

    [Fact]
    public void LinqProjectionStillThrowsWhenFullScanWouldBeRequiredByDefault()
    {
        var directory = TestDirectory("query-projection-unsupported-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);

        var exception = Assert.Throws<NotSupportedException>(
            () => context.Entities.Select(entity => entity.Value).ToList());

        Assert.Contains("full scan", exception.Message);
    }

    private static string TestDirectory(string name) =>
        Path.Combine(Path.GetTempPath(), "jsoncoldstore-tests", name);

    private static async Task WriteLegacyEntityAsync(
        string directory,
        WritableEntity entity,
        JsonColdStoreEncryptionKey? key = null)
    {
        var entityDirectory = Path.Combine(directory, nameof(WritableEntity));
        Directory.CreateDirectory(entityDirectory);
        var json = JsonSerializer.Serialize(
            entity,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var bytes = Encoding.UTF8.GetBytes(json);
        if (key is not null)
            bytes = EncryptLegacyPayload(bytes, key);

        await File.WriteAllBytesAsync(Path.Combine(entityDirectory, $"{entity.Id}.json"), bytes);
    }

    private static async Task WriteLegacyIndexAsync(
        string directory,
        string propertyName,
        string indexKey,
        IEnumerable<string> recordIds)
    {
        var entityDirectory = Path.Combine(directory, nameof(WritableEntity));
        Directory.CreateDirectory(entityDirectory);
        var shard = new Dictionary<string, List<string>>
        {
            [indexKey] = recordIds.ToList(),
        };
        await File.WriteAllTextAsync(
            Path.Combine(entityDirectory, $"_index_{propertyName}.json"),
            JsonSerializer.Serialize(shard, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<string> CorruptStoredRecordAsync(string directory, Guid id)
    {
        var recordPath = JsonColdStorePathValidator.GetSafeChildPath(
            directory,
            [.. JsonColdStoreRecordStore.GetRecordPathSegments(
                typeof(WritableEntity).FullName!,
                id.ToString())]);
        var bytes = await File.ReadAllBytesAsync(recordPath);
        bytes[^1] ^= 0x7F;
        await File.WriteAllBytesAsync(recordPath, bytes);
        return recordPath;
    }

    private static byte[] EncryptLegacyPayload(ReadOnlySpan<byte> plaintext, JsonColdStoreEncryptionKey key)
    {
        var keyBytes = key.CopyBytes();
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(keyBytes, 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

            var envelope = new byte[1 + nonce.Length + ciphertext.Length + tag.Length];
            envelope[0] = 0x01;
            nonce.CopyTo(envelope.AsSpan(1));
            ciphertext.CopyTo(envelope.AsSpan(1 + nonce.Length));
            tag.CopyTo(envelope.AsSpan(1 + nonce.Length + ciphertext.Length));
            return envelope;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options);

    private static Microsoft.EntityFrameworkCore.Metadata.IModel CreateWritableModel()
    {
        var modelBuilder = new ModelBuilder(new Microsoft.EntityFrameworkCore.Metadata.Conventions.ConventionSet());
        modelBuilder.Entity<WritableEntity>(entity =>
        {
            entity.HasKey(value => value.Id);
            entity.HasIndex(value => value.Value);
        });
        return modelBuilder.FinalizeModel();
    }

    private sealed class WritableDbContext(DbContextOptions<WritableDbContext> options) : DbContext(options)
    {
        public DbSet<WritableEntity> Entities => Set<WritableEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<WritableEntity>(entity =>
            {
                entity.HasKey(value => value.Id);
                entity.HasIndex(value => value.Value);
            });
        }
    }

    private sealed class WritableDbContextWithoutIndex(DbContextOptions<WritableDbContextWithoutIndex> options)
        : DbContext(options)
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
