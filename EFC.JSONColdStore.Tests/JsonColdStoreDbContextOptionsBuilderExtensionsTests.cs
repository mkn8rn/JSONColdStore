using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EFC.JSONColdStore;
using EFC.JSONColdStore.Infrastructure;
using EFC.JSONColdStore.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFC.JSONColdStore.Tests;

public sealed class JsonColdStoreDbContextOptionsBuilderExtensionsTests
{
    private static readonly JsonSerializerOptions StoreJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions ModelCatalogJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions ModelHashJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

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
    public void CanConnectReturnsTrueForCurrentStoreMetadata()
    {
        var directory = TestDirectory("can-connect-current-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);

        context.Database.EnsureCreated();

        Assert.True(context.Database.CanConnect());
    }

    [Fact]
    public void EnsureCreatedAllowsModelWithSharedTypeJoinEntity()
    {
        var directory = TestDirectory("shared-join-model-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<ManyToManyDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new ManyToManyDbContext(builder.Options);

        context.Database.EnsureCreated();

        Assert.True(context.Database.CanConnect());
    }

    [Fact]
    public async Task SaveChangesPersistsSharedTypeJoinEntityWrites()
    {
        var directory = TestDirectory("shared-join-write-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<ManyToManyDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new ManyToManyDbContext(builder.Options);
        var post = new ManyToManyPost { Id = Guid.NewGuid() };
        var tag = new ManyToManyTag { Id = Guid.NewGuid() };
        post.Tags.Add(tag);
        context.Posts.Add(post);

        var saved = context.SaveChanges();
        var verification = await context.Database.VerifyJsonColdStoreAsync();

        Assert.Equal(3, saved);
        Assert.Equal(3, verification.VerifiedRecords);
        Assert.Equal(0, verification.VerifiedLegacyRecords);
    }

    [Fact]
    public void CanConnectReturnsFalseForInvalidModelCatalog()
    {
        var directory = TestDirectory("can-connect-invalid-model-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);
        context.Database.EnsureCreated();
        File.WriteAllText(Path.Combine(directory, "_model.json"), "not json");

        Assert.False(context.Database.CanConnect());
    }

    [Fact]
    public void CanConnectReturnsFalseForChangedModelCatalog()
    {
        var directory = TestDirectory("can-connect-model-mismatch-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using (var context = new WritableDbContext(builder.Options))
        {
            context.Database.EnsureCreated();
        }

        var changedBuilder = new DbContextOptionsBuilder<WritableDbContextWithoutIndex>();
        changedBuilder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var changedContext = new WritableDbContextWithoutIndex(changedBuilder.Options);

        Assert.False(changedContext.Database.CanConnect());
    }

    [Fact]
    public void CanConnectReturnsTrueWhenModelCatalogIsMissing()
    {
        var directory = TestDirectory("can-connect-missing-model-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using (var setupContext = new WritableDbContext(builder.Options))
        {
            setupContext.Database.EnsureCreated();
        }

        File.Delete(Path.Combine(directory, JsonColdStoreModelCatalog.ModelFileName));
        using var context = new WritableDbContext(builder.Options);

        Assert.True(context.Database.CanConnect());
    }

    [Fact]
    public async Task CanConnectReturnsFalseForReparsePointModelCatalog()
    {
        var directory = TestDirectory("can-connect-linked-model-" + Guid.NewGuid().ToString("N"));
        var outside = TestDirectory("can-connect-linked-model-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using (var setupContext = new WritableDbContext(builder.Options))
        {
            setupContext.Database.EnsureCreated();
        }

        var outsideFile = Path.Combine(outside, JsonColdStoreModelCatalog.ModelFileName);
        await File.WriteAllTextAsync(outsideFile, "outside model catalog");
        var modelPath = Path.Combine(directory, JsonColdStoreModelCatalog.ModelFileName);
        File.Delete(modelPath);
        JsonColdStoreReparsePointTestHelper.CreateRequiredFileLink(
            modelPath,
            outsideFile,
            nameof(CanConnectReturnsFalseForReparsePointModelCatalog));
        using var context = new WritableDbContext(builder.Options);

        Assert.False(context.Database.CanConnect());
        Assert.False(await context.Database.CanConnectAsync());
        Assert.Equal("outside model catalog", await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public async Task CanConnectReturnsFalseForReparsePointModelCatalogDirectory()
    {
        var directory = TestDirectory("can-connect-linked-model-dir-" + Guid.NewGuid().ToString("N"));
        var outside = TestDirectory("can-connect-linked-model-dir-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using (var setupContext = new WritableDbContext(builder.Options))
        {
            setupContext.Database.EnsureCreated();
        }

        var outsideFile = Path.Combine(outside, "outside-model-directory.txt");
        await File.WriteAllTextAsync(outsideFile, "outside model directory payload");
        var modelPath = Path.Combine(directory, JsonColdStoreModelCatalog.ModelFileName);
        File.Delete(modelPath);
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
            modelPath,
            outside,
            nameof(CanConnectReturnsFalseForReparsePointModelCatalogDirectory));
        using var context = new WritableDbContext(builder.Options);

        Assert.False(context.Database.CanConnect());
        Assert.False(await context.Database.CanConnectAsync());
        Assert.Equal("outside model directory payload", await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public async Task CanConnectReturnsTrueForLegacyEntityDirectory()
    {
        var directory = TestDirectory("can-connect-legacy-" + Guid.NewGuid().ToString("N"));
        await WriteLegacyEntityAsync(
            directory,
            new WritableEntity
            {
                Id = Guid.Parse("62000000-0000-0000-0000-000000000001"),
                Value = "legacy-connect",
            });
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);

        Assert.True(context.Database.CanConnect());
        Assert.True(await context.Database.CanConnectAsync());
    }

    [Fact]
    public async Task CanConnectReturnsTrueForEncryptedLegacyEntityWithConfiguredKey()
    {
        var directory = TestDirectory("can-connect-encrypted-legacy-" + Guid.NewGuid().ToString("N"));
        using var key = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);
        await WriteLegacyEntityAsync(
            directory,
            new WritableEntity
            {
                Id = Guid.Parse("62000000-0000-0000-0000-000000000002"),
                Value = "encrypted legacy connect",
            },
            key);
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(
            directory,
            store => store
                .UseFsyncOnWrite(false)
                .UseEncryptionKey(key));
        using var context = new WritableDbContext(builder.Options);

        Assert.True(context.Database.CanConnect());
        Assert.True(await context.Database.CanConnectAsync());
    }

    [Fact]
    public async Task CanConnectReturnsFalseForEncryptedLegacyEntityWithoutKey()
    {
        var directory = TestDirectory("can-connect-encrypted-legacy-missing-key-" + Guid.NewGuid().ToString("N"));
        using var key = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);
        await WriteLegacyEntityAsync(
            directory,
            new WritableEntity
            {
                Id = Guid.Parse("62000000-0000-0000-0000-000000000003"),
                Value = "encrypted legacy blocked",
            },
            key);
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);

        Assert.False(context.Database.CanConnect());
        Assert.False(await context.Database.CanConnectAsync());
    }

    [Fact]
    public async Task CanConnectAsyncHonorsCancellation()
    {
        var directory = TestDirectory("can-connect-canceled-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.Database.CanConnectAsync(cts.Token));
    }

    [Fact]
    public void CanConnectReturnsFalseForUnrelatedDirectory()
    {
        var directory = TestDirectory("can-connect-unrelated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "unrelated.txt"), "not a store");
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);

        Assert.False(context.Database.CanConnect());
    }

    [Fact]
    public void CanConnectReturnsFalseForInvalidStoreMetadata()
    {
        var directory = TestDirectory("can-connect-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "_store.json"), "not json");
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);

        Assert.False(context.Database.CanConnect());
    }

    [Fact]
    public async Task CanConnectReturnsFalseForProtectedStoreMetadataWithoutKey()
    {
        var directory = TestDirectory("can-connect-protected-store-" + Guid.NewGuid().ToString("N"));
        using var key = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);
        var encryptedBuilder = new DbContextOptionsBuilder<WritableDbContext>();
        encryptedBuilder.UseJsonColdStoreDatabase(
            directory,
            store => store
                .UseEncryptionKey(key)
                .UseFsyncOnWrite(false));
        using (var encryptedContext = new WritableDbContext(encryptedBuilder.Options))
        {
            encryptedContext.Database.EnsureCreated();
        }

        var plaintextBuilder = new DbContextOptionsBuilder<WritableDbContext>();
        plaintextBuilder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var plaintextContext = new WritableDbContext(plaintextBuilder.Options);

        Assert.False(plaintextContext.Database.CanConnect());
        Assert.False(await plaintextContext.Database.CanConnectAsync());
        Assert.True(File.Exists(Path.Combine(directory, JsonColdStoreCatalog.StoreFileName)));
    }

    [Fact]
    public async Task CanConnectReturnsFalseForReparsePointStoreMetadata()
    {
        var directory = TestDirectory("can-connect-linked-store-" + Guid.NewGuid().ToString("N"));
        var outside = TestDirectory("can-connect-linked-store-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(outside);
        var id = Guid.Parse("62000000-0000-0000-0000-000000000014");
        await WriteLegacyEntityAsync(
            directory,
            new WritableEntity
            {
                Id = id,
                Value = "legacy behind linked store metadata",
            });
        var storePath = Path.Combine(directory, JsonColdStoreCatalog.StoreFileName);
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
            storePath,
            outside,
            nameof(CanConnectReturnsFalseForReparsePointStoreMetadata));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);

        Assert.False(context.Database.CanConnect());
        Assert.False(await context.Database.CanConnectAsync());
        Assert.True(File.Exists(Path.Combine(directory, nameof(WritableEntity), $"{id}.json")));
        Assert.False(Directory.Exists(Path.Combine(directory, "_locks")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
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
    public async Task EnsureCreatedRejectsReparsePointStoreMetadataBeforeLock()
    {
        var directory = TestDirectory("ensure-created-linked-store-" + Guid.NewGuid().ToString("N"));
        var outside = TestDirectory("ensure-created-linked-store-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, JsonColdStoreCatalog.StoreFileName);
        await File.WriteAllTextAsync(outsideFile, "outside ensure-created store metadata");
        var storePath = Path.Combine(directory, JsonColdStoreCatalog.StoreFileName);
        JsonColdStoreReparsePointTestHelper.CreateRequiredFileLink(
            storePath,
            outsideFile,
            nameof(EnsureCreatedRejectsReparsePointStoreMetadataBeforeLock));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);

        var exception = Assert.Throws<JsonColdStoreUnsafePathException>(
            () => context.Database.EnsureCreated());

        Assert.Contains("metadata", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(directory, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(storePath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outsideFile, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outside ensure-created store metadata", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("outside ensure-created store metadata", await File.ReadAllTextAsync(outsideFile));
        Assert.False(Directory.Exists(Path.Combine(directory, "_locks")));
        Assert.False(File.Exists(Path.Combine(directory, JsonColdStoreModelCatalog.ModelFileName)));
    }

    [Fact]
    public async Task EnsureCreatedRejectsReparsePointModelCatalog()
    {
        var directory = TestDirectory("ensure-created-linked-model-" + Guid.NewGuid().ToString("N"));
        var outside = TestDirectory("ensure-created-linked-model-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using (var setupContext = new WritableDbContext(builder.Options))
        {
            setupContext.Database.EnsureCreated();
        }

        var outsideFile = Path.Combine(outside, JsonColdStoreModelCatalog.ModelFileName);
        await File.WriteAllTextAsync(outsideFile, "outside ensure-created model");
        var modelPath = Path.Combine(directory, JsonColdStoreModelCatalog.ModelFileName);
        File.Delete(modelPath);
        JsonColdStoreReparsePointTestHelper.CreateRequiredFileLink(
            modelPath,
            outsideFile,
            nameof(EnsureCreatedRejectsReparsePointModelCatalog));
        using var context = new WritableDbContext(builder.Options);

        var exception = Assert.Throws<JsonColdStoreUnsafePathException>(
            () => context.Database.EnsureCreated());

        Assert.Contains("model catalog", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(directory, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(modelPath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outsideFile, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outside ensure-created model", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("outside ensure-created model", await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public async Task EnsureCreatedRejectsReparsePointModelCatalogBeforeLock()
    {
        var directory = TestDirectory("ensure-created-linked-model-dir-" + Guid.NewGuid().ToString("N"));
        var outside = TestDirectory("ensure-created-linked-model-dir-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(outside);
        var options = new JsonColdStoreOptionsBuilder(directory)
            .UseFsyncOnWrite(false)
            .Build();
        await WriteStoreMetadataAsync(
            directory,
            JsonColdStoreStoreMetadata.CreateNew(options, JsonColdStoreProviderInfo.Version));
        var outsideFile = Path.Combine(outside, "outside-model-directory.txt");
        await File.WriteAllTextAsync(outsideFile, "outside ensure-created model directory");
        var modelPath = Path.Combine(directory, JsonColdStoreModelCatalog.ModelFileName);
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
            modelPath,
            outside,
            nameof(EnsureCreatedRejectsReparsePointModelCatalogBeforeLock));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);

        var exception = Assert.Throws<JsonColdStoreUnsafePathException>(
            () => context.Database.EnsureCreated());

        Assert.Contains("model catalog", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(directory, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(modelPath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outside, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outside ensure-created model directory", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("outside ensure-created model directory", await File.ReadAllTextAsync(outsideFile));
        Assert.False(Directory.Exists(Path.Combine(directory, "_locks")));
    }

    [Fact]
    public async Task EnsureCreatedImportsLegacyRecordsIntoCurrentStore()
    {
        var directory = TestDirectory("ensure-created-imports-legacy-" + Guid.NewGuid().ToString("N"));
        var id = Guid.Parse("62000000-0000-0000-0000-000000000004");
        await WriteLegacyEntityAsync(
            directory,
            new WritableEntity
            {
                Id = id,
                Value = "legacy-import",
                Score = 42,
            });
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);

        Assert.True(context.Database.EnsureCreated());

        var read = await context.Database.ReadJsonColdStoreAsync<WritableEntity>(id);
        var verification = await context.Database.VerifyJsonColdStoreAsync();

        Assert.NotNull(read);
        Assert.Equal("legacy-import", read.Value);
        Assert.Equal(42, read.Score);
        Assert.False(File.Exists(Path.Combine(directory, nameof(WritableEntity), $"{id}.json")));
        Assert.True(File.Exists(CurrentRecordPath(directory, id)));
        Assert.Equal(1, verification.VerifiedRecords);
        Assert.Equal(0, verification.VerifiedLegacyRecords);
    }

    [Fact]
    public async Task EnsureCreatedImportsEncryptedLegacySharedJoinRowsIntoCurrentStore()
    {
        var directory = TestDirectory("ensure-created-imports-legacy-join-" + Guid.NewGuid().ToString("N"));
        var postId = Guid.Parse("62000000-0000-0000-0000-000000000011");
        var tagId = Guid.Parse("62000000-0000-0000-0000-000000000012");
        using var key = JsonColdStoreEncryptionKey.FromBytes(
            Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        await WriteLegacyEntityAsync(
            directory,
            new ManyToManyPost { Id = postId },
            postId,
            key);
        await WriteLegacyEntityAsync(
            directory,
            new ManyToManyTag { Id = tagId },
            tagId,
            key);
        var rowsPath = await WriteLegacySharedRowsAsync(
            directory,
            "ManyToManyPostTag",
            [
                new Dictionary<string, Guid>
                {
                    ["PostId"] = postId,
                    ["TagId"] = tagId,
                },
            ],
            key);
        var builder = new DbContextOptionsBuilder<ManyToManyDbContext>();
        builder.UseJsonColdStoreDatabase(
            directory,
            store => store
                .UseEncryptionKey(key)
                .UseFsyncOnWrite(false));
        using var context = new ManyToManyDbContext(builder.Options);

        Assert.True(context.Database.EnsureCreated());

        var modelDescriptor = JsonColdStoreModelDescriptor.Create(context.Model);
        var sharedDescriptor = Assert.Single(modelDescriptor.Entities, entity => entity.IsSharedType);
        var sharedRecordId = sharedDescriptor.CreateRecordIdFromEntity(
            new Dictionary<string, object?>
            {
                ["PostId"] = postId,
                ["TagId"] = tagId,
            });
        var verification = await context.Database.VerifyJsonColdStoreAsync();

        Assert.False(File.Exists(rowsPath));
        Assert.True(File.Exists(CurrentRecordPath(directory, sharedDescriptor.EntityName, sharedRecordId)));
        Assert.Equal(3, verification.VerifiedRecords);
        Assert.Equal(0, verification.VerifiedLegacyRecords);
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
    public void EnsureDeletedReturnsFalseForEmptyDirectoryWithoutMetadata()
    {
        var directory = TestDirectory("ensure-deleted-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);

        Assert.False(context.Database.EnsureDeleted());
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void EnsureDeletedRejectsDirectoryWithoutStoreMetadata()
    {
        var directory = TestDirectory("ensure-deleted-unrelated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var unrelatedFile = Path.Combine(directory, "unrelated.txt");
        File.WriteAllText(unrelatedFile, "keep me");
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);

        var exception = Assert.Throws<InvalidOperationException>(
            () => context.Database.EnsureDeleted());

        Assert.Contains("metadata", exception.Message);
        Assert.True(File.Exists(unrelatedFile));
        Assert.False(Directory.Exists(Path.Combine(directory, "_locks")));
    }

    [Fact]
    public async Task EnsureDeletedRejectsReparsePointStoreMetadataBeforeLock()
    {
        var directory = TestDirectory("ensure-deleted-linked-store-" + Guid.NewGuid().ToString("N"));
        var outside = TestDirectory("ensure-deleted-linked-store-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, JsonColdStoreCatalog.StoreFileName);
        await File.WriteAllTextAsync(outsideFile, "outside ensure-deleted store metadata");
        var storePath = Path.Combine(directory, JsonColdStoreCatalog.StoreFileName);
        JsonColdStoreReparsePointTestHelper.CreateRequiredFileLink(
            storePath,
            outsideFile,
            nameof(EnsureDeletedRejectsReparsePointStoreMetadataBeforeLock));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);

        var exception = Assert.Throws<JsonColdStoreUnsafePathException>(
            () => context.Database.EnsureDeleted());

        Assert.Contains("metadata", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(directory, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(storePath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outsideFile, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outside ensure-deleted store metadata", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(directory));
        Assert.Equal("outside ensure-deleted store metadata", await File.ReadAllTextAsync(outsideFile));
        Assert.False(Directory.Exists(Path.Combine(directory, "_locks")));
    }

    [Fact]
    public async Task EnsureDeletedRejectsReparsePointDatabaseRoot()
    {
        var parent = TestDirectory("ensure-deleted-reparse-parent-" + Guid.NewGuid().ToString("N"));
        var outside = TestDirectory("ensure-deleted-reparse-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(outside);
        var link = Path.Combine(parent, "linked-store");
        var outsideFile = Path.Combine(outside, "outside.txt");
        await File.WriteAllTextAsync(outsideFile, "keep me");
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
            link,
            outside,
            nameof(EnsureDeletedRejectsReparsePointDatabaseRoot));

        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(link, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);

        var exception = Assert.ThrowsAny<UnauthorizedAccessException>(
            () => context.Database.EnsureDeleted());

        Assert.Contains("reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(link));
        Assert.True(File.Exists(outsideFile));
        Assert.Single(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task EnsureDeletedRejectsReparsePointChildDirectory()
    {
        var directory = TestDirectory("ensure-deleted-linked-child-" + Guid.NewGuid().ToString("N"));
        var outside = TestDirectory("ensure-deleted-linked-child-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "outside.txt");
        await File.WriteAllTextAsync(outsideFile, "outside child");
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);
        context.Database.EnsureCreated();
        var linkParent = Path.Combine(directory, "entities");
        Directory.CreateDirectory(linkParent);
        var link = Path.Combine(linkParent, "linked-child");
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
            link,
            outside,
            nameof(EnsureDeletedRejectsReparsePointChildDirectory));

        var exception = await Assert.ThrowsAsync<JsonColdStoreUnsafePathException>(
            () => context.Database.EnsureDeletedAsync());

        Assert.Contains("reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(directory, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(link, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outside, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(directory));
        Assert.True(Directory.Exists(link));
        Assert.Equal("outside child", await File.ReadAllTextAsync(outsideFile));
        Assert.Single(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task EnsureDeletedRejectsReparsePointChildFile()
    {
        var directory = TestDirectory("ensure-deleted-linked-file-" + Guid.NewGuid().ToString("N"));
        var outside = TestDirectory("ensure-deleted-linked-file-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "outside-file.jcs");
        await File.WriteAllTextAsync(outsideFile, "outside file");
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);
        context.Database.EnsureCreated();
        var link = Path.Combine(directory, "linked-file.jcs");
        JsonColdStoreReparsePointTestHelper.CreateRequiredFileLink(
            link,
            outsideFile,
            nameof(EnsureDeletedRejectsReparsePointChildFile));

        var exception = await Assert.ThrowsAsync<JsonColdStoreUnsafePathException>(
            () => context.Database.EnsureDeletedAsync());

        Assert.Contains("reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(directory, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(link, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outsideFile, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(directory));
        Assert.True(File.Exists(link));
        Assert.Equal("outside file", await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public void EnsureCreatedRejectsReparsePointDatabaseRoot()
    {
        var parent = TestDirectory("ensure-created-reparse-parent-" + Guid.NewGuid().ToString("N"));
        var outside = TestDirectory("ensure-created-reparse-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(outside);
        var link = Path.Combine(parent, "linked-store");
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
            link,
            outside,
            nameof(EnsureCreatedRejectsReparsePointDatabaseRoot));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(link, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);

        var exception = Assert.Throws<JsonColdStoreUnsafePathException>(
            () => context.Database.EnsureCreated());

        Assert.Contains("database directory", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(link, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task CanConnectReturnsFalseForReparsePointDatabaseRoot()
    {
        var parent = TestDirectory("can-connect-reparse-parent-" + Guid.NewGuid().ToString("N"));
        var outside = TestDirectory("can-connect-reparse-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(outside);
        var id = Guid.Parse("62000000-0000-0000-0000-000000000013");
        await WriteLegacyEntityAsync(
            outside,
            new WritableEntity
            {
                Id = id,
                Value = "outside-root-legacy",
            });
        var link = Path.Combine(parent, "linked-store");
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
            link,
            outside,
            nameof(CanConnectReturnsFalseForReparsePointDatabaseRoot));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(link, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);

        Assert.False(context.Database.CanConnect());
        Assert.False(await context.Database.CanConnectAsync());
        Assert.True(File.Exists(Path.Combine(outside, nameof(WritableEntity), $"{id}.json")));
        Assert.False(Directory.Exists(Path.Combine(outside, "_locks")));
    }

    [Fact]
    public void EnsureDeletedRejectsInvalidStoreMetadata()
    {
        var directory = TestDirectory("ensure-deleted-invalid-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var storeFile = Path.Combine(directory, "_store.json");
        File.WriteAllText(storeFile, "not json");
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);

        Assert.Throws<JsonException>(() => context.Database.EnsureDeleted());

        Assert.True(File.Exists(storeFile));
        Assert.True(Directory.Exists(directory));
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
    public async Task SaveChangesRejectsDuplicateCurrentUniqueIndexValue()
    {
        var directory = TestDirectory("savechanges-current-unique-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<UniqueWritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        var existingId = Guid.Parse("61000000-0000-0000-0000-000000000001");
        var duplicateId = Guid.Parse("61000000-0000-0000-0000-000000000002");

        using (var context = new UniqueWritableDbContext(builder.Options))
        {
            context.Entities.Add(new WritableEntity
            {
                Id = existingId,
                Value = "current-sensitive-value",
            });
            context.SaveChanges();
            context.Entities.Add(new WritableEntity
            {
                Id = duplicateId,
                Value = "current-sensitive-value",
            });

            var exception = Assert.Throws<InvalidOperationException>(() => context.SaveChanges());

            Assert.Contains("unique index", exception.Message);
            Assert.DoesNotContain("current-sensitive-value", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(existingId.ToString(), exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(duplicateId.ToString(), exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(CurrentRecordPath(directory, duplicateId)));
        }

        using var readContext = new UniqueWritableDbContext(builder.Options);
        var matches = await readContext.Database.ReadJsonColdStoreIndexAsync<WritableEntity>(
            nameof(WritableEntity.Value),
            "current-sensitive-value");

        Assert.Single(matches);
        Assert.Equal(existingId, matches[0].Id);
    }

    [Fact]
    public void SaveChangesRejectsDuplicateUniqueIndexBatchBeforeWritingRecords()
    {
        var directory = TestDirectory("savechanges-batch-unique-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<UniqueWritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        var firstId = Guid.Parse("61000000-0000-0000-0000-000000000007");
        var secondId = Guid.Parse("61000000-0000-0000-0000-000000000008");
        using var context = new UniqueWritableDbContext(builder.Options);
        context.Entities.AddRange(
            new WritableEntity
            {
                Id = firstId,
                Value = "batch-sensitive-value",
            },
            new WritableEntity
            {
                Id = secondId,
                Value = "batch-sensitive-value",
            });

        var exception = Assert.Throws<InvalidOperationException>(() => context.SaveChanges());

        Assert.Contains("unique index", exception.Message);
        Assert.DoesNotContain("batch-sensitive-value", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(firstId.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secondId.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(CurrentRecordPath(directory, firstId)));
        Assert.False(File.Exists(CurrentRecordPath(directory, secondId)));
    }

    [Fact]
    public async Task SaveChangesRejectsPersistedUniqueConflictBeforeWritingEarlierBatchRecord()
    {
        var directory = TestDirectory("savechanges-preflight-unique-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<UniqueWritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        var existingId = Guid.Parse("61000000-0000-0000-0000-000000000009");
        var validId = Guid.Parse("61000000-0000-0000-0000-000000000010");
        var duplicateId = Guid.Parse("61000000-0000-0000-0000-000000000011");

        using (var setupContext = new UniqueWritableDbContext(builder.Options))
        {
            setupContext.Entities.Add(new WritableEntity
            {
                Id = existingId,
                Value = "persisted-sensitive-value",
            });
            setupContext.SaveChanges();
        }

        using (var context = new UniqueWritableDbContext(builder.Options))
        {
            context.Entities.AddRange(
                new WritableEntity
                {
                    Id = validId,
                    Value = "valid-new",
                },
                new WritableEntity
                {
                    Id = duplicateId,
                    Value = "persisted-sensitive-value",
                });

            var exception = Assert.Throws<InvalidOperationException>(() => context.SaveChanges());

            Assert.Contains("unique index", exception.Message);
            Assert.DoesNotContain("persisted-sensitive-value", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(existingId.ToString(), exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(validId.ToString(), exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(duplicateId.ToString(), exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(CurrentRecordPath(directory, validId)));
            Assert.False(File.Exists(CurrentRecordPath(directory, duplicateId)));
        }

        using var readContext = new UniqueWritableDbContext(builder.Options);
        var matches = await readContext.Database.ReadJsonColdStoreIndexAsync<WritableEntity>(
            nameof(WritableEntity.Value),
            "persisted-sensitive-value");

        Assert.Single(matches);
        Assert.Equal(existingId, matches[0].Id);
    }

    [Fact]
    public async Task SaveChangesAllowsSameRecordCurrentUniqueIndexValue()
    {
        var directory = TestDirectory("savechanges-current-unique-same-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<UniqueWritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        var id = Guid.Parse("61000000-0000-0000-0000-000000000003");

        using (var context = new UniqueWritableDbContext(builder.Options))
        {
            var entity = new WritableEntity
            {
                Id = id,
                Value = "same-record",
                Score = 1,
            };
            context.Entities.Add(entity);
            context.SaveChanges();
            entity.Score = 2;

            Assert.Equal(1, context.SaveChanges());
        }

        using var readContext = new UniqueWritableDbContext(builder.Options);
        var read = await readContext.Database.ReadJsonColdStoreAsync<WritableEntity>(id);

        Assert.NotNull(read);
        Assert.Equal(2, read.Score);
    }

    [Fact]
    public async Task SaveChangesAllowsReplacingDeletedUniqueIndexValueInSameBatch()
    {
        var directory = TestDirectory("savechanges-replace-unique-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<UniqueWritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        var deletedId = Guid.Parse("61000000-0000-0000-0000-000000000012");
        var replacementId = Guid.Parse("61000000-0000-0000-0000-000000000013");

        using (var setupContext = new UniqueWritableDbContext(builder.Options))
        {
            setupContext.Entities.Add(new WritableEntity
            {
                Id = deletedId,
                Value = "replace-me",
            });
            setupContext.SaveChanges();
        }

        using (var context = new UniqueWritableDbContext(builder.Options))
        {
            context.Entities.Remove(new WritableEntity
            {
                Id = deletedId,
                Value = "replace-me",
            });
            context.Entities.Add(new WritableEntity
            {
                Id = replacementId,
                Value = "replace-me",
                Score = 3,
            });

            Assert.Equal(2, context.SaveChanges());
        }

        using var readContext = new UniqueWritableDbContext(builder.Options);
        var deleted = await readContext.Database.ReadJsonColdStoreAsync<WritableEntity>(deletedId);
        var replacement = await readContext.Database.ReadJsonColdStoreAsync<WritableEntity>(replacementId);
        var matches = await readContext.Database.ReadJsonColdStoreIndexAsync<WritableEntity>(
            nameof(WritableEntity.Value),
            "replace-me");

        Assert.Null(deleted);
        Assert.NotNull(replacement);
        Assert.Equal(3, replacement.Score);
        Assert.Single(matches);
        Assert.Equal(replacementId, matches[0].Id);
    }

    [Fact]
    public async Task SaveChangesRejectsDuplicateLegacyUniqueIndexValue()
    {
        var directory = TestDirectory("savechanges-legacy-unique-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<UniqueWritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        var existingId = Guid.Parse("61000000-0000-0000-0000-000000000004");
        var duplicateId = Guid.Parse("61000000-0000-0000-0000-000000000005");
        await WriteLegacyEntityAsync(
            directory,
            new WritableEntity
            {
                Id = existingId,
                Value = "legacy-sensitive-value",
            });
        await WriteLegacyIndexAsync(
            directory,
            nameof(WritableEntity.Value),
            "legacy-sensitive-value",
            [existingId.ToString()]);

        using var context = new UniqueWritableDbContext(builder.Options);
        context.Entities.Add(new WritableEntity
        {
            Id = duplicateId,
            Value = "legacy-sensitive-value",
        });

        var exception = Assert.Throws<InvalidOperationException>(() => context.SaveChanges());

        Assert.Contains("unique index", exception.Message);
        Assert.DoesNotContain("legacy-sensitive-value", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(existingId.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(duplicateId.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(CurrentRecordPath(directory, duplicateId)));
    }

    [Fact]
    public async Task SaveChangesAllowsReplacingSameLegacyUniqueIndexRecord()
    {
        var directory = TestDirectory("savechanges-legacy-unique-same-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<UniqueWritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        var id = Guid.Parse("61000000-0000-0000-0000-000000000006");
        await WriteLegacyEntityAsync(
            directory,
            new WritableEntity
            {
                Id = id,
                Value = "legacy-same-record",
                Score = 1,
            });
        await WriteLegacyIndexAsync(
            directory,
            nameof(WritableEntity.Value),
            "legacy-same-record",
            [id.ToString()]);

        using (var context = new UniqueWritableDbContext(builder.Options))
        {
            context.Entities.Add(new WritableEntity
            {
                Id = id,
                Value = "legacy-same-record",
                Score = 2,
            });

            Assert.Equal(1, context.SaveChanges());
        }

        using var readContext = new UniqueWritableDbContext(builder.Options);
        var read = await readContext.Database.ReadJsonColdStoreAsync<WritableEntity>(id);

        Assert.NotNull(read);
        Assert.Equal(2, read.Score);
        Assert.False(File.Exists(Path.Combine(directory, nameof(WritableEntity), $"{id}.json")));
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
    public async Task ScanJsonColdStoreAsyncRejectsReparsePointCurrentRecordsDirectory()
    {
        var directory = TestDirectory("facade-scan-reparse-current-records-" + Guid.NewGuid().ToString("N"));
        var outside = TestDirectory("facade-scan-reparse-current-records-target-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using (var setupContext = new WritableDbContext(builder.Options))
        {
            setupContext.Database.EnsureCreated();
        }

        var recordsDirectory = CurrentRecordsDirectory(directory, typeof(WritableEntity).FullName!);
        Directory.CreateDirectory(Path.GetDirectoryName(recordsDirectory)!);
        var outsideFile = Path.Combine(outside, "outside.jcs");
        await WriteTextFileAsync(outsideFile, "outside scan current record");
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
            recordsDirectory,
            outside,
            nameof(ScanJsonColdStoreAsyncRejectsReparsePointCurrentRecordsDirectory));

        using var context = new WritableDbContext(builder.Options);
        var exception = await Assert.ThrowsAsync<JsonColdStoreUnsafePathException>(
            () => context.Database.ScanJsonColdStoreAsync<WritableEntity>());

        Assert.Contains("records directory", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(recordsDirectory, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outside, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("outside scan current record", await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public async Task ScanJsonColdStoreAsyncIgnoresUnsafeLegacyRecordFileNames()
    {
        var directory = TestDirectory("facade-scan-unsafe-legacy-" + Guid.NewGuid().ToString("N"));
        var safeId = Guid.Parse("10000000-0000-0000-0000-000000000003");
        var unsafeId = Guid.Parse("10000000-0000-0000-0000-000000000004");
        await WriteLegacyEntityAsync(directory, new WritableEntity
        {
            Id = safeId,
            Value = "safe legacy",
        });
        await WriteLegacyEntityFileAsync(
            directory,
            ".json",
            new WritableEntity
            {
                Id = unsafeId,
                Value = "unsafe legacy",
            });
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);

        var scanned = await context.Database.ScanJsonColdStoreAsync<WritableEntity>();

        Assert.Equal([safeId], scanned.Select(entity => entity.Id).ToArray());
        Assert.DoesNotContain(scanned, entity => entity.Value == "unsafe legacy");
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
    public async Task ReadJsonColdStoreIndexAsyncFiltersCurrentRecordInWrongBucket()
    {
        var directory = TestDirectory("facade-index-wrong-bucket-" + Guid.NewGuid().ToString("N"));
        var id = Guid.Parse("40000000-0000-0000-0000-000000000003");
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);
        context.Entities.Add(new WritableEntity
        {
            Id = id,
            Value = "actual-value",
        });
        context.SaveChanges();
        await MoveRecordIdToIndexBucketAsync(
            IndexPath(directory, "Value"),
            id.ToString(),
            "wrong-value");

        var matches = await context.Database.ReadJsonColdStoreIndexAsync<WritableEntity>(
            "Value",
            "wrong-value");

        Assert.Empty(matches);
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
    public async Task ReadJsonColdStoreIndexAsyncReturnsEmptyWhenDeclaredIndexHasNoCurrentRecords()
    {
        var directory = TestDirectory("facade-index-empty-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);
        context.Database.EnsureCreated();

        var matches = await context.Database.ReadJsonColdStoreIndexAsync<WritableEntity>("Value", "missing");

        Assert.Empty(matches);
    }

    [Fact]
    public async Task ReadJsonColdStoreIndexAsyncRejectsReparsePointIndexDocument()
    {
        var directory = TestDirectory("facade-index-linked-document-" + Guid.NewGuid().ToString("N"));
        var outside = TestDirectory("facade-index-linked-document-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "outside-index.json");
        await File.WriteAllTextAsync(outsideFile, "outside index");
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);
        context.Entities.Add(new WritableEntity
        {
            Id = Guid.Parse("50000000-0000-0000-0000-000000000010"),
            Value = "linked-index",
        });
        context.SaveChanges();
        var indexPath = IndexPath(directory, "Value");
        File.Delete(indexPath);
        JsonColdStoreReparsePointTestHelper.CreateRequiredFileLink(
            indexPath,
            outsideFile,
            nameof(ReadJsonColdStoreIndexAsyncRejectsReparsePointIndexDocument));

        var exception = await Assert.ThrowsAsync<JsonColdStoreUnsafePathException>(
            () => context.Database.ReadJsonColdStoreIndexAsync<WritableEntity>("Value", "linked-index"));

        Assert.Contains("index document", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(directory, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(indexPath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outsideFile, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outside index", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(indexPath));
        Assert.Equal("outside index", await File.ReadAllTextAsync(outsideFile));
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

        var unavailable = await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.Database.ReadJsonColdStoreIndexAsync<WritableEntity>("Value", "repair"));
        var rebuilt = await context.Database.RebuildJsonColdStoreIndexesAsync<WritableEntity>();
        var repaired = await context.Database.ReadJsonColdStoreIndexAsync<WritableEntity>("Value", "repair");

        Assert.Contains("index", unavailable.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, rebuilt);
        Assert.Single(repaired);
        Assert.Equal("repair", repaired[0].Value);
    }

    [Fact]
    public async Task RebuildJsonColdStoreIndexesAsyncRepairsAllDeclaredIndexes()
    {
        var directory = TestDirectory("facade-index-rebuild-all-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);
        context.Entities.Add(new WritableEntity
        {
            Id = Guid.Parse("50000000-0000-0000-0000-000000000002"),
            Value = "repair-all",
            Score = 42,
        });
        context.SaveChanges();
        File.Delete(IndexPath(directory, "Value"));
        File.Delete(IndexPath(directory, "Score"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.Database.ReadJsonColdStoreIndexAsync<WritableEntity>("Value", "repair-all"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.Database.ReadJsonColdStoreIndexAsync<WritableEntity>("Score", 42));
        var rebuilt = await context.Database.RebuildJsonColdStoreIndexesAsync();
        var repairedValue = await context.Database.ReadJsonColdStoreIndexAsync<WritableEntity>(
            "Value",
            "repair-all");
        var repairedScore = await context.Database.ReadJsonColdStoreIndexAsync<WritableEntity>(
            "Score",
            42);

        Assert.Equal(1, rebuilt);
        Assert.Single(repairedValue);
        Assert.Single(repairedScore);
        Assert.Equal("repair-all", repairedValue[0].Value);
        Assert.Equal(42, repairedScore[0].Score);
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
        Assert.Equal(2, result.VerifiedIndexes);
    }

    [Fact]
    public async Task VerifyJsonColdStoreAsyncRejectsReparsePointCurrentRecordsDirectory()
    {
        var directory = TestDirectory("verify-reparse-current-records-" + Guid.NewGuid().ToString("N"));
        var outside = TestDirectory("verify-reparse-current-records-target-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using (var setupContext = new WritableDbContext(builder.Options))
        {
            setupContext.Database.EnsureCreated();
        }

        var recordsDirectory = CurrentRecordsDirectory(directory, typeof(WritableEntity).FullName!);
        Directory.CreateDirectory(Path.GetDirectoryName(recordsDirectory)!);
        var outsideFile = Path.Combine(outside, "outside.jcs");
        await WriteTextFileAsync(outsideFile, "outside verify current record");
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
            recordsDirectory,
            outside,
            nameof(VerifyJsonColdStoreAsyncRejectsReparsePointCurrentRecordsDirectory));

        using var context = new WritableDbContext(builder.Options);
        var exception = await Assert.ThrowsAsync<JsonColdStoreUnsafePathException>(
            () => context.Database.VerifyJsonColdStoreAsync());

        Assert.Contains("records directory", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(recordsDirectory, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outside, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("outside verify current record", await File.ReadAllTextAsync(outsideFile));
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
        Assert.Equal(0, result.VerifiedIndexes);
        Assert.False(File.Exists(Path.Combine(directory, "_store.json")));
        Assert.False(File.Exists(Path.Combine(directory, "_model.json")));
    }

    [Fact]
    public async Task VerifyJsonColdStoreAsyncAcceptsIndexesRebuiltFromLegacyRecords()
    {
        var directory = TestDirectory("verify-rebuilt-legacy-index-" + Guid.NewGuid().ToString("N"));
        var id = Guid.Parse("51000000-0000-0000-0000-000000000008");
        await WriteLegacyEntityAsync(directory, new WritableEntity
        {
            Id = id,
            Value = "legacy-rebuilt",
            Score = 12,
        });
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        var rebuilt = await context.Database.RebuildJsonColdStoreIndexesAsync<WritableEntity>();
        var result = await context.Database.VerifyJsonColdStoreAsync();

        Assert.Equal(1, rebuilt);
        Assert.Equal(0, result.VerifiedRecords);
        Assert.Equal(1, result.VerifiedLegacyRecords);
        Assert.Equal(2, result.VerifiedIndexes);
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
        Assert.Equal(0, result.VerifiedIndexes);
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
    public async Task VerifyJsonColdStoreAsyncRejectsMissingDeclaredIndex()
    {
        var directory = TestDirectory("verify-missing-index-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);
        context.Entities.Add(new WritableEntity
        {
            Id = Guid.Parse("51000000-0000-0000-0000-000000000008"),
            Value = "missing-index",
        });
        context.SaveChanges();
        File.Delete(IndexPath(directory, "Value"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.Database.VerifyJsonColdStoreAsync());

        Assert.Contains("index", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyJsonColdStoreAsyncRejectsReparsePointIndexDocument()
    {
        var directory = TestDirectory("verify-linked-index-document-" + Guid.NewGuid().ToString("N"));
        var outside = TestDirectory("verify-linked-index-document-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "outside-index.json");
        await File.WriteAllTextAsync(outsideFile, "outside verify index");
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);
        context.Entities.Add(new WritableEntity
        {
            Id = Guid.Parse("51000000-0000-0000-0000-000000000020"),
            Value = "verify-linked-index",
        });
        context.SaveChanges();
        var indexPath = IndexPath(directory, "Value");
        File.Delete(indexPath);
        JsonColdStoreReparsePointTestHelper.CreateRequiredFileLink(
            indexPath,
            outsideFile,
            nameof(VerifyJsonColdStoreAsyncRejectsReparsePointIndexDocument));

        var exception = await Assert.ThrowsAsync<JsonColdStoreUnsafePathException>(
            () => context.Database.VerifyJsonColdStoreAsync());

        Assert.Contains("index document", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(directory, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(indexPath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outsideFile, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outside verify index", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(indexPath));
        Assert.Equal("outside verify index", await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public async Task VerifyJsonColdStoreAsyncRejectsIndexReferenceToMissingRecord()
    {
        var directory = TestDirectory("verify-stale-index-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        var missingRecordId = Guid.Parse("51000000-0000-0000-0000-000000000009").ToString();
        using var context = new WritableDbContext(builder.Options);
        context.Entities.Add(new WritableEntity
        {
            Id = Guid.Parse("51000000-0000-0000-0000-000000000010"),
            Value = "stale-index",
        });
        context.SaveChanges();
        await AppendRecordIdToFirstIndexBucketAsync(IndexPath(directory, "Value"), missingRecordId);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => context.Database.VerifyJsonColdStoreAsync());

        Assert.Contains("missing record", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(missingRecordId, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyJsonColdStoreAsyncRejectsIndexMissingCurrentRecord()
    {
        var directory = TestDirectory("verify-index-missing-current-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        var id = Guid.Parse("51000000-0000-0000-0000-000000000011");
        using var context = new WritableDbContext(builder.Options);
        context.Entities.Add(new WritableEntity
        {
            Id = id,
            Value = "missing-current-index",
        });
        context.SaveChanges();
        await RemoveRecordIdFromIndexAsync(IndexPath(directory, "Value"), id.ToString());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => context.Database.VerifyJsonColdStoreAsync());

        Assert.Contains("does not match current record values", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyJsonColdStoreAsyncRejectsIndexRecordInWrongBucket()
    {
        var directory = TestDirectory("verify-index-wrong-bucket-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        var id = Guid.Parse("51000000-0000-0000-0000-000000000012");
        using var context = new WritableDbContext(builder.Options);
        context.Entities.Add(new WritableEntity
        {
            Id = id,
            Value = "correct-index-bucket",
        });
        context.SaveChanges();
        await MoveRecordIdToIndexBucketAsync(
            IndexPath(directory, "Value"),
            id.ToString(),
            "wrong-index-bucket");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => context.Database.VerifyJsonColdStoreAsync());

        Assert.Contains("does not match current record values", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepairJsonColdStoreAsyncQuarantinesCorruptRecordAndKeepsValidRecords()
    {
        var directory = TestDirectory("repair-corrupt-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        var corruptId = Guid.Parse("51000000-0000-0000-0000-000000000006");
        var validId = Guid.Parse("51000000-0000-0000-0000-000000000007");
        using var context = new WritableDbContext(builder.Options);
        context.Entities.AddRange(
            new WritableEntity
            {
                Id = corruptId,
                Value = "corrupt me",
            },
            new WritableEntity
            {
                Id = validId,
                Value = "keep me",
            });
        context.SaveChanges();
        var corruptRecordPath = await CorruptStoredRecordAsync(directory, corruptId);

        var result = await context.Database.RepairJsonColdStoreAsync();
        var valid = await context.Database.ReadJsonColdStoreAsync<WritableEntity>(validId);
        var indexText = await File.ReadAllTextAsync(IndexPath(directory, "Value"));

        Assert.Equal(1, result.VerifiedRecords);
        Assert.Equal(1, result.QuarantinedRecords);
        Assert.Equal(1, result.RebuiltIndexRecordCount);
        Assert.False(File.Exists(corruptRecordPath));
        Assert.NotNull(valid);
        Assert.Equal("keep me", valid.Value);
        Assert.Contains(validId.ToString(), indexText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(corruptId.ToString(), indexText, StringComparison.OrdinalIgnoreCase);
        Assert.Single(Directory.GetFiles(Path.Combine(directory, "_quarantine", "records"), "*.jcs"));
    }

    [Fact]
    public async Task RepairJsonColdStoreAsyncRejectsReparsePointCurrentRecordsDirectory()
    {
        var directory = TestDirectory("repair-reparse-current-records-" + Guid.NewGuid().ToString("N"));
        var outside = TestDirectory("repair-reparse-current-records-target-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using (var setupContext = new WritableDbContext(builder.Options))
        {
            setupContext.Database.EnsureCreated();
        }

        var recordsDirectory = CurrentRecordsDirectory(directory, typeof(WritableEntity).FullName!);
        Directory.CreateDirectory(Path.GetDirectoryName(recordsDirectory)!);
        var outsideFile = Path.Combine(outside, "outside.jcs");
        await WriteTextFileAsync(outsideFile, "outside repair current record");
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
            recordsDirectory,
            outside,
            nameof(RepairJsonColdStoreAsyncRejectsReparsePointCurrentRecordsDirectory));

        using var context = new WritableDbContext(builder.Options);
        var exception = await Assert.ThrowsAsync<JsonColdStoreUnsafePathException>(
            () => context.Database.RepairJsonColdStoreAsync());

        Assert.Contains("current record directory", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(recordsDirectory, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outside, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("outside repair current record", await File.ReadAllTextAsync(outsideFile));
        Assert.False(Directory.Exists(Path.Combine(outside, "_quarantine")));
    }

    [Fact]
    public async Task CreateJsonColdStoreSnapshotAsyncCopiesStoreWithoutLocksOrNestedSnapshots()
    {
        var directory = TestDirectory("snapshot-copy-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        var id = Guid.Parse("52000000-0000-0000-0000-000000000001");
        using var context = new WritableDbContext(builder.Options);
        context.Entities.Add(new WritableEntity
        {
            Id = id,
            Value = "snapshot me",
            Score = 11,
        });
        context.SaveChanges();
        var preexistingSnapshot = Path.Combine(directory, "_snapshots", "20000101000000000000000-old");
        Directory.CreateDirectory(preexistingSnapshot);
        await File.WriteAllTextAsync(Path.Combine(preexistingSnapshot, "do-not-copy.txt"), "old snapshot");

        var result = await context.Database.CreateJsonColdStoreSnapshotAsync();

        Assert.True(Directory.Exists(result.SnapshotDirectory));
        Assert.True(result.CopiedFiles > 0);
        Assert.True(File.Exists(Path.Combine(result.SnapshotDirectory, "_store.json")));
        Assert.True(File.Exists(Path.Combine(result.SnapshotDirectory, "_model.json")));
        Assert.True(File.Exists(Path.Combine(
            result.SnapshotDirectory,
            "entities",
            JsonColdStoreNameEncoder.EncodePathSegment(typeof(WritableEntity).FullName!),
            "records",
            JsonColdStoreNameEncoder.EncodePathSegment(id.ToString()) + ".jcs")));
        Assert.False(Directory.Exists(Path.Combine(result.SnapshotDirectory, "_locks")));
        Assert.False(Directory.Exists(Path.Combine(result.SnapshotDirectory, "_snapshots")));
        Assert.False(File.Exists(Path.Combine(
            result.SnapshotDirectory,
            "_snapshots",
            "20000101000000000000000-old",
            "do-not-copy.txt")));
    }

    [Fact]
    public async Task CreateJsonColdStoreSnapshotAsyncAppliesRetentionCount()
    {
        var directory = TestDirectory("snapshot-retention-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(
            directory,
            store => store
                .UseFsyncOnWrite(false)
                .UseSnapshots(enabled: true, TimeSpan.FromHours(1), retentionCount: 1));
        using var context = new WritableDbContext(builder.Options);
        context.Entities.Add(new WritableEntity
        {
            Id = Guid.Parse("52000000-0000-0000-0000-000000000002"),
            Value = "retained",
            Score = 12,
        });
        context.SaveChanges();

        var first = await context.Database.CreateJsonColdStoreSnapshotAsync();
        await Task.Delay(20);
        var second = await context.Database.CreateJsonColdStoreSnapshotAsync();

        Assert.False(Directory.Exists(first.SnapshotDirectory));
        Assert.True(Directory.Exists(second.SnapshotDirectory));
        Assert.Equal(1, second.DeletedSnapshots);
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(directory, "_snapshots")));
    }

    [Fact]
    public async Task GetJsonColdStoreDiagnosticsAsyncReportsRedactedStoreCounts()
    {
        var directory = TestDirectory("diagnostics-counts-" + Guid.NewGuid().ToString("N"));
        using var key = JsonColdStoreEncryptionKey.FromBytes(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        var integrityKeyBytes = Enumerable.Range(0, 32).Select(value => (byte)(255 - value)).ToArray();
        using var integrityKey = JsonColdStoreIntegrityKey.FromBytes(integrityKeyBytes);
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(
            directory,
            store => store
                .UseFsyncOnWrite(false)
                .UseEncryption(new JsonColdStoreEncryptionOptions
                {
                    Key = key,
                    KeyId = "diagnostic-key-id",
                })
                .UseIntegrityKey(integrityKey)
                .UseEventLog(enabled: true, TimeSpan.FromDays(7))
                .UseSnapshots(enabled: true, TimeSpan.FromHours(1), retentionCount: 2));
        var id = Guid.Parse("53000000-0000-0000-0000-000000000001");
        using var context = new WritableDbContext(builder.Options);
        context.Entities.Add(new WritableEntity
        {
            Id = id,
            Value = "diagnostic payload",
            Score = 53,
        });
        context.SaveChanges();
        _ = await context.Database.CreateJsonColdStoreSnapshotAsync();

        var diagnostics = await context.Database.GetJsonColdStoreDiagnosticsAsync();
        var serialized = JsonSerializer.Serialize(diagnostics);

        Assert.True(diagnostics.HasStoreMetadata);
        Assert.Equal(1, diagnostics.RecordFileCount);
        Assert.Equal(2, diagnostics.IndexFileCount);
        Assert.Equal(0, diagnostics.LegacyIndexShardFileCount);
        Assert.Equal(0, diagnostics.LegacyChecksumSidecarFileCount);
        Assert.Equal(0, diagnostics.LegacySharedRowsFileCount);
        Assert.Equal(0, diagnostics.PlaintextProtectedDocumentCount);
        Assert.Equal(1, diagnostics.EventLogFileCount);
        Assert.Equal(1, diagnostics.SnapshotCount);
        Assert.Equal(1, diagnostics.MappedEntityCount);
        Assert.Equal(2, diagnostics.Entities[0].DeclaredIndexCount);
        Assert.Equal(1, diagnostics.Entities[0].RecordFileCount);
        Assert.Equal(2, diagnostics.Entities[0].IndexFileCount);
        Assert.Equal(0, diagnostics.Entities[0].LegacyIndexShardFileCount);
        Assert.Equal(0, diagnostics.Entities[0].LegacyChecksumSidecarFileCount);
        Assert.Equal(0, diagnostics.Entities[0].LegacySharedRowsFileCount);
        Assert.True(diagnostics.EncryptionEnabled);
        Assert.True(diagnostics.IntegrityChecksumsEnabled);
        Assert.True(diagnostics.KeyedIntegrityEnabled);
        Assert.DoesNotContain(directory, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("diagnostic-key-id", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(integrityKeyBytes), serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(id.ToString(), serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("diagnostic payload", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetJsonColdStoreDiagnosticsAsyncReportsProtectedMetadataWithoutKey()
    {
        var directory = TestDirectory("diagnostics-protected-metadata-no-key-" + Guid.NewGuid().ToString("N"));
        using (var key = JsonColdStoreEncryptionKey.FromBytes(new byte[32]))
        {
            var encryptedBuilder = new DbContextOptionsBuilder<WritableDbContext>();
            encryptedBuilder.UseJsonColdStoreDatabase(
                directory,
                store => store
                    .UseFsyncOnWrite(false)
                    .UseEncryptionKey(key));
            using var encryptedContext = new WritableDbContext(encryptedBuilder.Options);
            encryptedContext.Entities.Add(new WritableEntity
            {
                Id = Guid.Parse("53000000-0000-0000-0000-000000000003"),
                Value = "protected diagnostic payload",
                Score = 55,
            });
            encryptedContext.SaveChanges();
        }

        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);

        var diagnostics = await context.Database.GetJsonColdStoreDiagnosticsAsync();
        var serialized = JsonSerializer.Serialize(diagnostics);

        Assert.True(diagnostics.HasStoreMetadata);
        Assert.False(diagnostics.StoreMetadataReadable);
        Assert.True(diagnostics.StoreMetadataProtected);
        Assert.True(diagnostics.EncryptionEnabled);
        Assert.Null(diagnostics.StoreId);
        Assert.Equal(1, diagnostics.RecordFileCount);
        Assert.Equal(2, diagnostics.IndexFileCount);
        Assert.Equal(0, diagnostics.SkippedUnsafePathCount);
        Assert.DoesNotContain("protected diagnostic payload", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetJsonColdStoreDiagnosticsAsyncCountsLegacyRecordsWithoutCreatingMetadata()
    {
        var directory = TestDirectory("diagnostics-legacy-" + Guid.NewGuid().ToString("N"));
        await WriteLegacyEntityAsync(directory, new WritableEntity
        {
            Id = Guid.Parse("53000000-0000-0000-0000-000000000002"),
            Value = "legacy diagnostics",
            Score = 54,
        });
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        var diagnostics = await context.Database.GetJsonColdStoreDiagnosticsAsync();

        Assert.False(diagnostics.HasStoreMetadata);
        Assert.Equal(0, diagnostics.RecordFileCount);
        Assert.Equal(1, diagnostics.LegacyRecordFileCount);
        Assert.Equal(1, diagnostics.Entities[0].LegacyRecordFileCount);
        Assert.False(File.Exists(Path.Combine(directory, "_store.json")));
        Assert.False(File.Exists(Path.Combine(directory, "_model.json")));
    }

    [Fact]
    public async Task GetJsonColdStoreDiagnosticsAsyncIgnoresUnsafeLegacyRecordFileNames()
    {
        var directory = TestDirectory("diagnostics-unsafe-legacy-" + Guid.NewGuid().ToString("N"));
        await WriteLegacyEntityAsync(directory, new WritableEntity
        {
            Id = Guid.Parse("53000000-0000-0000-0000-000000000003"),
            Value = "safe legacy diagnostics",
            Score = 55,
        });
        await WriteLegacyEntityFileAsync(
            directory,
            ".json",
            new WritableEntity
            {
                Id = Guid.Parse("53000000-0000-0000-0000-000000000004"),
                Value = "unsafe legacy diagnostics",
                Score = 56,
            });
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        var diagnostics = await context.Database.GetJsonColdStoreDiagnosticsAsync();

        Assert.False(diagnostics.HasStoreMetadata);
        Assert.Equal(1, diagnostics.LegacyRecordFileCount);
        Assert.Equal(1, diagnostics.Entities[0].LegacyRecordFileCount);
        Assert.False(File.Exists(Path.Combine(directory, "_store.json")));
        Assert.False(File.Exists(Path.Combine(directory, "_model.json")));
    }

    [Fact]
    public async Task GetJsonColdStoreDiagnosticsAsyncCountsTemporaryFilesWithoutDeletingThem()
    {
        var directory = TestDirectory("diagnostics-temp-" + Guid.NewGuid().ToString("N"));
        var nestedDirectory = Path.Combine(directory, "entities", "Entity", "records");
        var snapshotDirectory = Path.Combine(directory, "_snapshots", "snapshot-1");
        var rootTemp = Path.Combine(directory, "_store.json.tmp-diagnostic");
        var nestedTemp = Path.Combine(nestedDirectory, "1.jcs.tmp-diagnostic");
        var snapshotTemp = Path.Combine(snapshotDirectory, "kept.jcs.tmp-diagnostic");
        Directory.CreateDirectory(nestedDirectory);
        Directory.CreateDirectory(snapshotDirectory);
        await File.WriteAllTextAsync(rootTemp, "root temp");
        await File.WriteAllTextAsync(nestedTemp, "nested temp");
        await File.WriteAllTextAsync(snapshotTemp, "snapshot temp");
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        var diagnostics = await context.Database.GetJsonColdStoreDiagnosticsAsync();

        Assert.Equal(2, diagnostics.TemporaryFileCount);
        Assert.Equal(0, diagnostics.SkippedUnsafePathCount);
        Assert.True(File.Exists(rootTemp));
        Assert.True(File.Exists(nestedTemp));
        Assert.True(File.Exists(snapshotTemp));
        Assert.False(File.Exists(Path.Combine(directory, "_store.json")));
        Assert.False(File.Exists(Path.Combine(directory, "_model.json")));
    }

    [Fact]
    public async Task GetJsonColdStoreDiagnosticsAsyncCountsNormalMaintenanceDirectories()
    {
        var directory = TestDirectory("diagnostics-normal-maintenance-" + Guid.NewGuid().ToString("N"));
        await WriteTextFileAsync(Path.Combine(directory, "_transactions", "pending", "pending.json"), "pending");
        await WriteTextFileAsync(Path.Combine(directory, "_transactions", "failed", "failed.json"), "failed");
        await WriteTextFileAsync(Path.Combine(directory, "_transactions", "staged", "staged.jcs"), "staged");
        await WriteTextFileAsync(Path.Combine(directory, "_quarantine", "records", "record.jcs"), "quarantine");
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        var diagnostics = await context.Database.GetJsonColdStoreDiagnosticsAsync();

        Assert.Equal(1, diagnostics.PendingManifestCount);
        Assert.Equal(1, diagnostics.FailedManifestCount);
        Assert.Equal(1, diagnostics.StagedWriteCount);
        Assert.Equal(1, diagnostics.QuarantineFileCount);
        Assert.Equal(0, diagnostics.LegacyIndexShardFileCount);
        Assert.Equal(0, diagnostics.LegacyChecksumSidecarFileCount);
        Assert.Equal(0, diagnostics.LegacySharedRowsFileCount);
        Assert.Equal(0, diagnostics.PlaintextProtectedDocumentCount);
        Assert.Equal(0, diagnostics.SkippedUnsafePathCount);
        Assert.Equal(0, diagnostics.Entities[0].SkippedUnsafePathCount);
        Assert.False(File.Exists(Path.Combine(directory, "_store.json")));
        Assert.False(File.Exists(Path.Combine(directory, "_model.json")));
    }

    [Fact]
    public async Task GetJsonColdStoreDiagnosticsAsyncCountsLegacyMigrationReadinessArtifacts()
    {
        var directory = TestDirectory("diagnostics-legacy-readiness-" + Guid.NewGuid().ToString("N"));
        var postId = Guid.Parse("53000000-0000-0000-0000-000000000005");
        var tagId = Guid.Parse("53000000-0000-0000-0000-000000000006");
        await WriteLegacyEntityAsync(
            directory,
            new ManyToManyPost { Id = postId },
            postId);
        await WriteTextFileAsync(
            Path.Combine(directory, nameof(ManyToManyPost), "_index_Id.json"),
            """{"legacy-index-secret":["53000000-0000-0000-0000-000000000005"]}""");
        await WriteTextFileAsync(
            Path.Combine(directory, nameof(ManyToManyPost), "_index_TagId", $"{tagId:D}.json"),
            """["53000000-0000-0000-0000-000000000005"]""");
        await WriteTextFileAsync(
            Path.Combine(directory, nameof(ManyToManyPost), "_checksums.json"),
            "legacy-checksum-secret");
        await WriteTextFileAsync(
            Path.Combine(directory, nameof(ManyToManyPost), "_checksums.sig"),
            "legacy-signature-secret");
        await WriteLegacySharedRowsAsync(
            directory,
            "ManyToManyPostTag",
            [
                new Dictionary<string, Guid>
                {
                    ["PostId"] = postId,
                    ["TagId"] = tagId,
                },
            ]);
        var builder = new DbContextOptionsBuilder<ManyToManyDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new ManyToManyDbContext(builder.Options);
        var diagnostics = await context.Database.GetJsonColdStoreDiagnosticsAsync();
        var serialized = JsonSerializer.Serialize(diagnostics);

        Assert.Equal(1, diagnostics.LegacyRecordFileCount);
        Assert.Equal(2, diagnostics.LegacyIndexShardFileCount);
        Assert.Equal(2, diagnostics.LegacyChecksumSidecarFileCount);
        Assert.Equal(1, diagnostics.LegacySharedRowsFileCount);
        Assert.Equal(0, diagnostics.PlaintextProtectedDocumentCount);
        Assert.Equal(0, diagnostics.SkippedUnsafePathCount);
        var postDiagnostics = Assert.Single(
            diagnostics.Entities,
            entity => entity.ClrTypeName == typeof(ManyToManyPost).FullName);
        Assert.Equal(1, postDiagnostics.LegacyRecordFileCount);
        Assert.Equal(2, postDiagnostics.LegacyIndexShardFileCount);
        Assert.Equal(2, postDiagnostics.LegacyChecksumSidecarFileCount);
        Assert.Equal(0, postDiagnostics.LegacySharedRowsFileCount);
        var sharedDiagnostics = Assert.Single(
            diagnostics.Entities,
            entity => entity.EntityName == "ManyToManyPostTag");
        Assert.Equal(0, sharedDiagnostics.LegacyRecordFileCount);
        Assert.Equal(0, sharedDiagnostics.LegacyIndexShardFileCount);
        Assert.Equal(0, sharedDiagnostics.LegacyChecksumSidecarFileCount);
        Assert.Equal(1, sharedDiagnostics.LegacySharedRowsFileCount);
        Assert.DoesNotContain(directory, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("legacy-index-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy-checksum-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy-signature-secret", serialized, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(directory, "_store.json")));
        Assert.False(File.Exists(Path.Combine(directory, "_model.json")));
    }

    [Fact]
    public async Task GetJsonColdStoreDiagnosticsAsyncSkipsReparsePointDatabaseRoot()
    {
        var parent = TestDirectory("diagnostics-root-reparse-parent-" + Guid.NewGuid().ToString("N"));
        var outside = TestDirectory("diagnostics-root-reparse-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(outside);
        await WriteTextFileAsync(
            Path.Combine(outside, JsonColdStoreCatalog.StoreFileName),
            """{"outsideRootSecret":true}""");
        await WriteLegacyEntityAsync(
            outside,
            new WritableEntity
            {
                Id = Guid.Parse("53000000-0000-0000-0000-000000000008"),
                Value = "outside-root-legacy",
                Score = 58,
            });
        await WriteTextFileAsync(
            Path.Combine(outside, "_transactions", "pending", "outside.json"),
            "outside pending");
        var link = Path.Combine(parent, "linked-store");
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
            link,
            outside,
            nameof(GetJsonColdStoreDiagnosticsAsyncSkipsReparsePointDatabaseRoot));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(link, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        var diagnostics = await context.Database.GetJsonColdStoreDiagnosticsAsync();
        var serialized = JsonSerializer.Serialize(diagnostics);

        Assert.False(diagnostics.HasStoreMetadata);
        Assert.False(diagnostics.StoreMetadataReadable);
        Assert.False(diagnostics.StoreMetadataProtected);
        Assert.Equal(1, diagnostics.MappedEntityCount);
        Assert.Equal(0, diagnostics.RecordFileCount);
        Assert.Equal(0, diagnostics.IndexFileCount);
        Assert.Equal(0, diagnostics.LegacyRecordFileCount);
        Assert.Equal(0, diagnostics.PendingManifestCount);
        Assert.Equal(0, diagnostics.TemporaryFileCount);
        Assert.Equal(1, diagnostics.SkippedUnsafePathCount);
        Assert.Equal(0, diagnostics.Entities[0].SkippedUnsafePathCount);
        Assert.DoesNotContain(link, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outside, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outsideRootSecret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("outside-root-legacy", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("outside pending", serialized, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(outside, JsonColdStoreCatalog.StoreFileName)));
        Assert.True(Directory.Exists(Path.Combine(outside, nameof(WritableEntity))));
        Assert.False(Directory.Exists(Path.Combine(outside, "_locks")));
    }

    [Fact]
    public async Task GetJsonColdStoreDiagnosticsAsyncCountsReparsePointStoreMetadataAsSkippedUnsafe()
    {
        var directory = TestDirectory("diagnostics-linked-store-metadata-" + Guid.NewGuid().ToString("N"));
        var outside = TestDirectory("diagnostics-linked-store-metadata-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, JsonColdStoreCatalog.StoreFileName);
        await File.WriteAllTextAsync(outsideFile, """{"outsideMetadataSecret":true}""");
        var storePath = Path.Combine(directory, JsonColdStoreCatalog.StoreFileName);
        JsonColdStoreReparsePointTestHelper.CreateRequiredFileLink(
            storePath,
            outsideFile,
            nameof(GetJsonColdStoreDiagnosticsAsyncCountsReparsePointStoreMetadataAsSkippedUnsafe));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        var diagnostics = await context.Database.GetJsonColdStoreDiagnosticsAsync();
        var serialized = JsonSerializer.Serialize(diagnostics);

        Assert.True(diagnostics.HasStoreMetadata);
        Assert.False(diagnostics.StoreMetadataReadable);
        Assert.False(diagnostics.StoreMetadataProtected);
        Assert.Equal(1, diagnostics.SkippedUnsafePathCount);
        Assert.Equal(0, diagnostics.Entities[0].SkippedUnsafePathCount);
        Assert.Equal(0, diagnostics.RecordFileCount);
        Assert.Equal(0, diagnostics.IndexFileCount);
        Assert.DoesNotContain(directory, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(storePath, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outside, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outsideMetadataSecret", serialized, StringComparison.Ordinal);
        Assert.True(File.Exists(storePath));
        Assert.Equal("""{"outsideMetadataSecret":true}""", await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public async Task GetJsonColdStoreDiagnosticsAsyncReportsCorruptMetadataWithoutSkippingUnsafePath()
    {
        var directory = TestDirectory("diagnostics-corrupt-store-metadata-" + Guid.NewGuid().ToString("N"));
        await WriteTextFileAsync(
            Path.Combine(directory, JsonColdStoreCatalog.StoreFileName),
            """{"corruptMetadataSecret":""");
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        var diagnostics = await context.Database.GetJsonColdStoreDiagnosticsAsync();
        var serialized = JsonSerializer.Serialize(diagnostics);

        Assert.True(diagnostics.HasStoreMetadata);
        Assert.False(diagnostics.StoreMetadataReadable);
        Assert.False(diagnostics.StoreMetadataProtected);
        Assert.Equal(0, diagnostics.SkippedUnsafePathCount);
        Assert.Equal(0, diagnostics.Entities[0].SkippedUnsafePathCount);
        Assert.DoesNotContain(directory, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("corruptMetadataSecret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetJsonColdStoreDiagnosticsAsyncReturnsEmptyForMissingDatabaseRoot()
    {
        var directory = TestDirectory("diagnostics-missing-root-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        var diagnostics = await context.Database.GetJsonColdStoreDiagnosticsAsync();

        Assert.False(Directory.Exists(directory));
        Assert.False(diagnostics.HasStoreMetadata);
        Assert.False(diagnostics.StoreMetadataReadable);
        Assert.False(diagnostics.StoreMetadataProtected);
        Assert.Equal(1, diagnostics.MappedEntityCount);
        Assert.Equal(0, diagnostics.RecordFileCount);
        Assert.Equal(0, diagnostics.IndexFileCount);
        Assert.Equal(0, diagnostics.LegacyRecordFileCount);
        Assert.Equal(0, diagnostics.PendingManifestCount);
        Assert.Equal(0, diagnostics.TemporaryFileCount);
        Assert.Equal(0, diagnostics.SkippedUnsafePathCount);
        var entity = Assert.Single(diagnostics.Entities);
        Assert.Equal(0, entity.SkippedUnsafePathCount);
    }

    [Fact]
    public async Task GetJsonColdStoreDiagnosticsAsyncCountsPlaintextProtectedDocumentsWithoutRemovingCompatibility()
    {
        var directory = TestDirectory("diagnostics-plaintext-protected-" + Guid.NewGuid().ToString("N"));
        using var key = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(
            directory,
            store => store
                .UseFsyncOnWrite(false)
                .UseEncryptionKey(key));
        var id = Guid.Parse("53000000-0000-0000-0000-000000000007");
        using var context = new WritableDbContext(builder.Options);
        context.Entities.Add(new WritableEntity
        {
            Id = id,
            Value = "plaintext-index-secret",
            Score = 57,
        });
        context.SaveChanges();
        var storeOptions = builder.Options.FindExtension<JsonColdStoreOptionsExtension>()!.Options;
        var metadata = JsonColdStoreStoreMetadata.CreateNew(
            storeOptions,
            JsonColdStoreProviderInfo.Version);
        await WriteStoreMetadataAsync(directory, metadata);
        await WriteModelCatalogAsync(directory, context.Model);
        await WriteTextFileAsync(
            IndexPath(directory, nameof(WritableEntity.Value)),
            $"{{\"buckets\":{{\"plaintext-index-secret\":[\"{id}\"]}}}}");

        var diagnostics = await context.Database.GetJsonColdStoreDiagnosticsAsync();
        var matches = await context.Database.ReadJsonColdStoreIndexAsync<WritableEntity>(
            nameof(WritableEntity.Value),
            "plaintext-index-secret");
        var serialized = JsonSerializer.Serialize(diagnostics);

        Assert.True(diagnostics.StoreMetadataReadable);
        Assert.False(diagnostics.StoreMetadataProtected);
        Assert.True(diagnostics.EncryptionEnabled);
        Assert.Equal(3, diagnostics.PlaintextProtectedDocumentCount);
        Assert.Single(matches);
        Assert.Equal(id, matches[0].Id);
        Assert.DoesNotContain(directory, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plaintext-index-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("modelHash", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetJsonColdStoreDiagnosticsAsyncSkipsReparsePointCounterDirectories()
    {
        var directory = TestDirectory("diagnostics-linked-counters-" + Guid.NewGuid().ToString("N"));
        var outside = TestDirectory("diagnostics-linked-counters-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(outside);
        var entitySegment = JsonColdStoreNameEncoder.EncodePathSegment(typeof(WritableEntity).FullName!);
        var outsideRecord = await CreateRequiredLinkedDirectoryWithFileAsync(
            Path.Combine(directory, "entities", entitySegment, "records"),
            Path.Combine(outside, "records"),
            "outside.jcs",
            "outside record",
            nameof(GetJsonColdStoreDiagnosticsAsyncSkipsReparsePointCounterDirectories));
        var outsideIndex = await CreateRequiredLinkedDirectoryWithFileAsync(
            Path.Combine(directory, "entities", entitySegment, "indexes"),
            Path.Combine(outside, "indexes"),
            "outside.json",
            "outside index",
            nameof(GetJsonColdStoreDiagnosticsAsyncSkipsReparsePointCounterDirectories));
        var outsideLegacy = await CreateRequiredLinkedDirectoryWithFileAsync(
            Path.Combine(directory, nameof(WritableEntity)),
            Path.Combine(outside, "legacy"),
            "53000000-0000-0000-0000-000000000004.json",
            "outside legacy",
            nameof(GetJsonColdStoreDiagnosticsAsyncSkipsReparsePointCounterDirectories));
        var outsidePending = await CreateRequiredLinkedDirectoryWithFileAsync(
            Path.Combine(directory, "_transactions", "pending"),
            Path.Combine(outside, "pending"),
            "pending.json",
            "outside pending",
            nameof(GetJsonColdStoreDiagnosticsAsyncSkipsReparsePointCounterDirectories));
        var outsideFailed = await CreateRequiredLinkedDirectoryWithFileAsync(
            Path.Combine(directory, "_transactions", "failed"),
            Path.Combine(outside, "failed"),
            "failed.json",
            "outside failed",
            nameof(GetJsonColdStoreDiagnosticsAsyncSkipsReparsePointCounterDirectories));
        var outsideStaged = await CreateRequiredLinkedDirectoryWithFileAsync(
            Path.Combine(directory, "_transactions", "staged"),
            Path.Combine(outside, "staged"),
            "staged.jcs",
            "outside staged",
            nameof(GetJsonColdStoreDiagnosticsAsyncSkipsReparsePointCounterDirectories));
        var outsideQuarantine = await CreateRequiredLinkedDirectoryWithFileAsync(
            Path.Combine(directory, "_quarantine", "records"),
            Path.Combine(outside, "quarantine"),
            "record.jcs",
            "outside quarantine",
            nameof(GetJsonColdStoreDiagnosticsAsyncSkipsReparsePointCounterDirectories));
        var outsideEvent = await CreateRequiredLinkedDirectoryWithFileAsync(
            Path.Combine(directory, "_events"),
            Path.Combine(outside, "events"),
            "20260702.jsonl",
            "outside event",
            nameof(GetJsonColdStoreDiagnosticsAsyncSkipsReparsePointCounterDirectories));
        var outsideSnapshots = Path.Combine(outside, "snapshots");
        Directory.CreateDirectory(Path.Combine(outsideSnapshots, "outside-snapshot"));
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
            Path.Combine(directory, "_snapshots"),
            outsideSnapshots,
            nameof(GetJsonColdStoreDiagnosticsAsyncSkipsReparsePointCounterDirectories));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        var diagnostics = await context.Database.GetJsonColdStoreDiagnosticsAsync();
        var serialized = JsonSerializer.Serialize(diagnostics);

        Assert.Equal(0, diagnostics.RecordFileCount);
        Assert.Equal(0, diagnostics.IndexFileCount);
        Assert.Equal(0, diagnostics.LegacyRecordFileCount);
        Assert.Equal(0, diagnostics.PendingManifestCount);
        Assert.Equal(0, diagnostics.FailedManifestCount);
        Assert.Equal(0, diagnostics.StagedWriteCount);
        Assert.Equal(0, diagnostics.QuarantineFileCount);
        Assert.Equal(0, diagnostics.EventLogFileCount);
        Assert.Equal(0, diagnostics.SnapshotCount);
        Assert.Equal(0, diagnostics.TemporaryFileCount);
        Assert.Equal(0, diagnostics.Entities[0].RecordFileCount);
        Assert.Equal(0, diagnostics.Entities[0].IndexFileCount);
        Assert.Equal(0, diagnostics.Entities[0].LegacyRecordFileCount);
        Assert.Equal(9, diagnostics.SkippedUnsafePathCount);
        Assert.Equal(3, diagnostics.Entities[0].SkippedUnsafePathCount);
        Assert.DoesNotContain(directory, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outside, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outside", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("outside record", await File.ReadAllTextAsync(outsideRecord));
        Assert.Equal("outside index", await File.ReadAllTextAsync(outsideIndex));
        Assert.Equal("outside legacy", await File.ReadAllTextAsync(outsideLegacy));
        Assert.Equal("outside pending", await File.ReadAllTextAsync(outsidePending));
        Assert.Equal("outside failed", await File.ReadAllTextAsync(outsideFailed));
        Assert.Equal("outside staged", await File.ReadAllTextAsync(outsideStaged));
        Assert.Equal("outside quarantine", await File.ReadAllTextAsync(outsideQuarantine));
        Assert.Equal("outside event", await File.ReadAllTextAsync(outsideEvent));
        Assert.True(Directory.Exists(Path.Combine(outsideSnapshots, "outside-snapshot")));
        Assert.False(File.Exists(Path.Combine(directory, "_store.json")));
        Assert.False(File.Exists(Path.Combine(directory, "_model.json")));
    }

    [Fact]
    public async Task GetJsonColdStoreDiagnosticsAsyncReportsConfiguredEncryptionWithoutMetadata()
    {
        var directory = TestDirectory("diagnostics-encryption-no-metadata-" + Guid.NewGuid().ToString("N"));
        using var key = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);
        using var integrityKey = JsonColdStoreIntegrityKey.FromBytes(Enumerable.Repeat((byte)6, 32).ToArray());
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(
            directory,
            store => store
                .UseFsyncOnWrite(false)
                .UseEncryption(new JsonColdStoreEncryptionOptions
                {
                    Key = key,
                    KeyId = "diagnostics-no-metadata-key",
                })
                .UseIntegrityKey(integrityKey));

        using var context = new WritableDbContext(builder.Options);
        var diagnostics = await context.Database.GetJsonColdStoreDiagnosticsAsync();
        var serialized = JsonSerializer.Serialize(diagnostics);

        Assert.False(diagnostics.HasStoreMetadata);
        Assert.True(diagnostics.EncryptionEnabled);
        Assert.True(diagnostics.IntegrityChecksumsEnabled);
        Assert.True(diagnostics.KeyedIntegrityEnabled);
        Assert.DoesNotContain("diagnostics-no-metadata-key", serialized, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(directory, "_store.json")));
        Assert.False(File.Exists(Path.Combine(directory, "_model.json")));
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
    public async Task ReadJsonColdStoreAsyncRejectsLegacyPlainEntityWhenEncryptionIsRequired()
    {
        var directory = TestDirectory("legacy-plain-require-encrypted-" + Guid.NewGuid().ToString("N"));
        using var key = JsonColdStoreEncryptionKey.FromBytes(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        var id = Guid.Parse("70000000-0000-0000-0000-000000000012");
        await WriteLegacyEntityAsync(directory, new WritableEntity
        {
            Id = id,
            Value = "plain legacy rejected",
        });
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(
            directory,
            store => store
                .UseFsyncOnWrite(false)
                .UseEncryption(new JsonColdStoreEncryptionOptions
                {
                    Key = key,
                    RequireEncryptedStore = true,
                }));

        using var context = new WritableDbContext(builder.Options);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.Database.ReadJsonColdStoreAsync<WritableEntity>(id));

        Assert.Contains("requires encrypted legacy", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadJsonColdStoreAsyncReadsLegacyEncryptedEntityWhenEncryptionIsRequired()
    {
        var directory = TestDirectory("legacy-encrypted-required-" + Guid.NewGuid().ToString("N"));
        using var key = JsonColdStoreEncryptionKey.FromBytes(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        var id = Guid.Parse("70000000-0000-0000-0000-000000000013");
        await WriteLegacyEntityAsync(
            directory,
            new WritableEntity
            {
                Id = id,
                Value = "encrypted legacy required",
            },
            key);
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(
            directory,
            store => store
                .UseFsyncOnWrite(false)
                .UseEncryption(new JsonColdStoreEncryptionOptions
                {
                    Key = key,
                    RequireEncryptedStore = true,
                }));

        using var context = new WritableDbContext(builder.Options);
        var read = await context.Database.ReadJsonColdStoreAsync<WritableEntity>(id);

        Assert.NotNull(read);
        Assert.Equal("encrypted legacy required", read.Value);
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
    public async Task ReadJsonColdStoreIndexAsyncScansLegacyRecordsWhenLegacyIndexShardIsMissing()
    {
        var directory = TestDirectory("legacy-missing-index-" + Guid.NewGuid().ToString("N"));
        var matchId = Guid.Parse("70000000-0000-0000-0000-000000000014");
        await WriteLegacyEntityAsync(directory, new WritableEntity
        {
            Id = matchId,
            Value = "legacy-missing-index-match",
        });
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        var matches = await context.Database.ReadJsonColdStoreIndexAsync<WritableEntity>(
            "Value",
            "legacy-missing-index-match");

        Assert.Single(matches);
        Assert.Equal(matchId, matches[0].Id);
    }

    [Fact]
    public async Task ReadJsonColdStoreIndexAsyncFiltersLegacyRecordInWrongBucket()
    {
        var directory = TestDirectory("legacy-index-wrong-bucket-" + Guid.NewGuid().ToString("N"));
        var id = Guid.Parse("70000000-0000-0000-0000-000000000012");
        await WriteLegacyEntityAsync(directory, new WritableEntity
        {
            Id = id,
            Value = "legacy-actual-value",
        });
        await WriteLegacyIndexAsync(directory, "Value", "legacy-wrong-value", [id.ToString()]);
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        var matches = await context.Database.ReadJsonColdStoreIndexAsync<WritableEntity>(
            "Value",
            "legacy-wrong-value");

        Assert.Empty(matches);
    }

    [Fact]
    public async Task ReadJsonColdStoreIndexAsyncUsesLegacyKeyScopedIndexShard()
    {
        var directory = TestDirectory("legacy-key-scoped-index-" + Guid.NewGuid().ToString("N"));
        var matchId = Guid.Parse("70000000-0000-0000-0000-000000000006");
        var skipId = Guid.Parse("70000000-0000-0000-0000-000000000007");
        var indexKey = Guid.Parse("71000000-0000-0000-0000-000000000001");
        await WriteLegacyEntityAsync(directory, new WritableEntity
        {
            Id = matchId,
            Value = indexKey.ToString(),
        });
        await WriteLegacyEntityAsync(directory, new WritableEntity
        {
            Id = skipId,
            Value = Guid.Parse("71000000-0000-0000-0000-000000000002").ToString(),
        });
        await WriteLegacyKeyScopedIndexAsync(directory, "Value", indexKey, [matchId.ToString()]);
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        var matches = await context.Database.ReadJsonColdStoreIndexAsync<WritableEntity>(
            "Value",
            indexKey.ToString());

        Assert.Single(matches);
        Assert.Equal(matchId, matches[0].Id);
        Assert.False(File.Exists(Path.Combine(directory, "_store.json")));
        Assert.False(File.Exists(Path.Combine(directory, "_model.json")));
    }

    [Fact]
    public async Task ReadJsonColdStoreIndexAsyncReturnsEmptyWhenLegacyKeyScopedShardIsMissing()
    {
        var directory = TestDirectory("legacy-missing-key-scoped-index-" + Guid.NewGuid().ToString("N"));
        var matchId = Guid.Parse("70000000-0000-0000-0000-000000000015");
        var indexKey = Guid.Parse("71000000-0000-0000-0000-000000000004");
        await WriteLegacyEntityAsync(directory, new WritableEntity
        {
            Id = matchId,
            Value = indexKey.ToString(),
        });
        Directory.CreateDirectory(Path.Combine(directory, nameof(WritableEntity), "_index_Value"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        var matches = await context.Database.ReadJsonColdStoreIndexAsync<WritableEntity>(
            "Value",
            indexKey.ToString());

        Assert.Empty(matches);
    }

    [Fact]
    public async Task ReadJsonColdStoreIndexAsyncReturnsEmptyWhenLegacyKeyScopedValueIsNotGuid()
    {
        var directory = TestDirectory("legacy-key-scoped-index-nonguid-" + Guid.NewGuid().ToString("N"));
        var matchId = Guid.Parse("70000000-0000-0000-0000-000000000016");
        await WriteLegacyEntityAsync(directory, new WritableEntity
        {
            Id = matchId,
            Value = "legacy-key-not-guid",
        });
        Directory.CreateDirectory(Path.Combine(directory, nameof(WritableEntity), "_index_Value"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        var matches = await context.Database.ReadJsonColdStoreIndexAsync<WritableEntity>(
            "Value",
            "legacy-key-not-guid");

        Assert.Empty(matches);
    }

    [Fact]
    public async Task ReadJsonColdStoreIndexAsyncRejectsReparsePointLegacyIndexShard()
    {
        var directory = TestDirectory("legacy-linked-index-" + Guid.NewGuid().ToString("N"));
        var outside = TestDirectory("legacy-linked-index-outside-" + Guid.NewGuid().ToString("N"));
        var matchId = Guid.Parse("70000000-0000-0000-0000-000000000017");
        var outsidePayload = "outside legacy flat shard payload";
        await WriteLegacyEntityAsync(directory, new WritableEntity
        {
            Id = matchId,
            Value = "legacy-linked-index-match",
        });
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "outside-index.json");
        await File.WriteAllTextAsync(outsideFile, outsidePayload);
        var shardPath = Path.Combine(directory, nameof(WritableEntity), "_index_Value.json");
        JsonColdStoreReparsePointTestHelper.CreateRequiredFileLink(
            shardPath,
            outsideFile,
            nameof(ReadJsonColdStoreIndexAsyncRejectsReparsePointLegacyIndexShard));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        var exception = await Assert.ThrowsAsync<JsonColdStoreUnsafePathException>(
            () => context.Database.ReadJsonColdStoreIndexAsync<WritableEntity>(
                "Value",
                "legacy-linked-index-match"));

        AssertRedactedLegacyIndexShardException(
            exception,
            directory,
            shardPath,
            outsideFile,
            outsidePayload);
        Assert.True(File.Exists(shardPath));
        Assert.Equal(outsidePayload, await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public async Task ReadJsonColdStoreIndexAsyncRejectsReparsePointLegacyKeyScopedIndexShard()
    {
        var directory = TestDirectory("legacy-linked-key-scoped-index-" + Guid.NewGuid().ToString("N"));
        var outside = TestDirectory("legacy-linked-key-scoped-index-outside-" + Guid.NewGuid().ToString("N"));
        var matchId = Guid.Parse("70000000-0000-0000-0000-000000000018");
        var indexKey = Guid.Parse("71000000-0000-0000-0000-000000000005");
        var outsidePayload = "outside legacy key scoped shard payload";
        await WriteLegacyEntityAsync(directory, new WritableEntity
        {
            Id = matchId,
            Value = indexKey.ToString(),
        });
        var indexDirectory = Path.Combine(directory, nameof(WritableEntity), "_index_Value");
        Directory.CreateDirectory(indexDirectory);
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "outside-key-index.json");
        await File.WriteAllTextAsync(outsideFile, outsidePayload);
        var shardPath = Path.Combine(indexDirectory, $"{indexKey:D}.json");
        JsonColdStoreReparsePointTestHelper.CreateRequiredFileLink(
            shardPath,
            outsideFile,
            nameof(ReadJsonColdStoreIndexAsyncRejectsReparsePointLegacyKeyScopedIndexShard));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        var exception = await Assert.ThrowsAsync<JsonColdStoreUnsafePathException>(
            () => context.Database.ReadJsonColdStoreIndexAsync<WritableEntity>(
                "Value",
                indexKey.ToString()));

        AssertRedactedLegacyIndexShardException(
            exception,
            directory,
            shardPath,
            outsideFile,
            outsidePayload);
        Assert.True(File.Exists(shardPath));
        Assert.Equal(outsidePayload, await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public async Task ReadJsonColdStoreIndexAsyncScansLegacyRecordsWhenLegacyIndexShardIsCorrupt()
    {
        var directory = TestDirectory("legacy-corrupt-index-" + Guid.NewGuid().ToString("N"));
        var matchId = Guid.Parse("70000000-0000-0000-0000-000000000008");
        await WriteLegacyEntityAsync(directory, new WritableEntity
        {
            Id = matchId,
            Value = "corrupt-index-match",
        });
        await File.WriteAllTextAsync(
            Path.Combine(directory, nameof(WritableEntity), "_index_Value.json"),
            "not valid json");
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        var matches = await context.Database.ReadJsonColdStoreIndexAsync<WritableEntity>(
            "Value",
            "corrupt-index-match");

        Assert.Single(matches);
        Assert.Equal(matchId, matches[0].Id);
    }

    [Fact]
    public async Task ReadJsonColdStoreIndexAsyncScansLegacyRecordsWhenKeyScopedShardIsCorrupt()
    {
        var directory = TestDirectory("legacy-corrupt-key-scoped-index-" + Guid.NewGuid().ToString("N"));
        var matchId = Guid.Parse("70000000-0000-0000-0000-000000000009");
        var indexKey = Guid.Parse("71000000-0000-0000-0000-000000000003");
        await WriteLegacyEntityAsync(directory, new WritableEntity
        {
            Id = matchId,
            Value = indexKey.ToString(),
        });
        var indexDirectory = Path.Combine(directory, nameof(WritableEntity), "_index_Value");
        Directory.CreateDirectory(indexDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(indexDirectory, $"{indexKey:D}.json"),
            "not valid json");
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        var matches = await context.Database.ReadJsonColdStoreIndexAsync<WritableEntity>(
            "Value",
            indexKey.ToString());

        Assert.Single(matches);
        Assert.Equal(matchId, matches[0].Id);
    }

    [Fact]
    public async Task ReadJsonColdStoreIndexAsyncRejectsUnsafeLegacyIndexRecordIds()
    {
        var directory = TestDirectory("legacy-unsafe-index-record-id-" + Guid.NewGuid().ToString("N"));
        var matchId = Guid.Parse("70000000-0000-0000-0000-000000000010");
        var escapedId = Guid.Parse("70000000-0000-0000-0000-000000000011");
        await WriteLegacyEntityAsync(directory, new WritableEntity
        {
            Id = matchId,
            Value = "unsafe-index-match",
        });
        var escapedDirectory = Path.Combine(directory, "OtherEntity");
        Directory.CreateDirectory(escapedDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(escapedDirectory, "secret.json"),
            JsonSerializer.Serialize(
                new WritableEntity
                {
                    Id = escapedId,
                    Value = "unsafe-index-match",
                },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        await WriteLegacyIndexAsync(
            directory,
            "Value",
            "unsafe-index-match",
            [Path.Combine("..", "OtherEntity", "secret")]);
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));

        using var context = new WritableDbContext(builder.Options);
        var matches = await context.Database.ReadJsonColdStoreIndexAsync<WritableEntity>(
            "Value",
            "unsafe-index-match");

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
    public async Task LinqToListAsyncScansWhenExplicitScanPolicyAndQueryIsMarked()
    {
        var directory = TestDirectory("query-async-explicit-scan-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(
            directory,
            store => store
                .UseFsyncOnWrite(false)
                .UseFullScanPolicy(JsonColdStoreScanPolicy.AllowExplicitScans));

        using var context = new WritableDbContext(builder.Options);
        context.Entities.AddRange(
            new WritableEntity
            {
                Id = Guid.Parse("81000000-0000-0000-0000-000000000010"),
                Value = "match-one",
            },
            new WritableEntity
            {
                Id = Guid.Parse("81000000-0000-0000-0000-000000000011"),
                Value = "skip",
            },
            new WritableEntity
            {
                Id = Guid.Parse("81000000-0000-0000-0000-000000000012"),
                Value = "match-two",
            });
        context.SaveChanges();

        var values = await context.Entities
            .AsJsonColdStoreExplicitScan()
            .Where(entity => entity.Value.Contains("match"))
            .Select(entity => entity.Value)
            .ToListAsync();

        Assert.Equal(["match-one", "match-two"], values.Order().ToArray());
    }

    [Fact]
    public void LinqToListThrowsWhenExplicitScanPolicyButQueryIsNotMarked()
    {
        var directory = TestDirectory("query-explicit-scan-unmarked-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(
            directory,
            store => store
                .UseFsyncOnWrite(false)
                .UseFullScanPolicy(JsonColdStoreScanPolicy.AllowExplicitScans));
        using var context = new WritableDbContext(builder.Options);

        var exception = Assert.Throws<NotSupportedException>(() => context.Entities.ToList());

        Assert.Contains("AsJsonColdStoreExplicitScan", exception.Message);
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
    public void LinqToListScansWhenExplicitScanPolicyAndQueryIsMarked()
    {
        var directory = TestDirectory("query-explicit-scan-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(
            directory,
            store => store
                .UseFsyncOnWrite(false)
                .UseFullScanPolicy(JsonColdStoreScanPolicy.AllowExplicitScans));

        using var context = new WritableDbContext(builder.Options);
        context.Entities.AddRange(
            new WritableEntity
            {
                Id = Guid.Parse("80000000-0000-0000-0000-000000000010"),
                Value = "match-one",
            },
            new WritableEntity
            {
                Id = Guid.Parse("80000000-0000-0000-0000-000000000011"),
                Value = "skip",
            },
            new WritableEntity
            {
                Id = Guid.Parse("80000000-0000-0000-0000-000000000012"),
                Value = "match-two",
            });
        context.SaveChanges();

        var values = context.Entities
            .Where(entity => entity.Value.Contains("match"))
            .AsJsonColdStoreExplicitScan()
            .Select(entity => entity.Value)
            .ToList();

        Assert.Equal(["match-one", "match-two"], values.Order().ToArray());
    }

    [Fact]
    public void LinqToListThrowsWhenQueryIsMarkedButExplicitScanPolicyIsNotConfigured()
    {
        var directory = TestDirectory("query-explicit-scan-default-policy-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);

        var exception = Assert.Throws<NotSupportedException>(
            () => context.Entities.AsJsonColdStoreExplicitScan().ToList());

        Assert.Contains("AllowExplicitScans", exception.Message);
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
    public void LinqRangeUsesDeclaredIndexWithoutSilentScan()
    {
        var directory = TestDirectory("query-range-index-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);
        context.Entities.AddRange(
            new WritableEntity
            {
                Id = Guid.Parse("83000000-0000-0000-0000-000000000001"),
                Value = "five",
                Score = 5,
            },
            new WritableEntity
            {
                Id = Guid.Parse("83000000-0000-0000-0000-000000000002"),
                Value = "ten",
                Score = 10,
            },
            new WritableEntity
            {
                Id = Guid.Parse("83000000-0000-0000-0000-000000000003"),
                Value = "twenty",
                Score = 20,
            });
        context.SaveChanges();
        var minimum = 10;
        var maximum = 20;

        var values = context.Entities
            .Where(entity => entity.Score >= minimum && entity.Score < maximum)
            .OrderBy(entity => entity.Score)
            .Select(entity => entity.Value)
            .ToList();

        Assert.Equal(["ten"], values);
    }

    [Fact]
    public async Task LinqRangeCountAsyncUsesDeclaredIndexWithoutSilentScan()
    {
        var directory = TestDirectory("query-async-range-index-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);
        context.Entities.AddRange(
            new WritableEntity
            {
                Id = Guid.Parse("83000000-0000-0000-0000-000000000004"),
                Value = "low",
                Score = 1,
            },
            new WritableEntity
            {
                Id = Guid.Parse("83000000-0000-0000-0000-000000000005"),
                Value = "middle",
                Score = 10,
            },
            new WritableEntity
            {
                Id = Guid.Parse("83000000-0000-0000-0000-000000000006"),
                Value = "high",
                Score = 20,
            });
        context.SaveChanges();
        var minimum = 10;

        var count = await context.Entities.CountAsync(entity => entity.Score >= minimum);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task LinqRangeSkipsOutOfRangeIndexBucketsBeforeMaterializingRecords()
    {
        var directory = TestDirectory("query-range-bucket-prune-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(
            directory,
            store => store
                .UseFsyncOnWrite(false)
                .UseChecksums(verifyOnStartup: true, verifyOnRead: true));
        using var context = new WritableDbContext(builder.Options);
        var corruptId = Guid.Parse("83000000-0000-0000-0000-000000000009");
        var expectedId = Guid.Parse("83000000-0000-0000-0000-000000000010");
        context.Entities.AddRange(
            new WritableEntity
            {
                Id = corruptId,
                Value = "corrupt-out-of-range",
                Score = 1,
            },
            new WritableEntity
            {
                Id = expectedId,
                Value = "range-hit",
                Score = 20,
            });
        context.SaveChanges();
        await CorruptStoredRecordAsync(directory, corruptId);

        var ids = context.Entities
            .Where(entity => entity.Score >= 10)
            .Select(entity => entity.Id)
            .ToList();

        Assert.Equal([expectedId], ids);
    }

    [Fact]
    public async Task LinqRangeCombinesBoundsBeforeMaterializingRecords()
    {
        var directory = TestDirectory("query-range-combined-prune-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(
            directory,
            store => store
                .UseFsyncOnWrite(false)
                .UseChecksums(verifyOnStartup: true, verifyOnRead: true));
        using var context = new WritableDbContext(builder.Options);
        var expectedId = Guid.Parse("83000000-0000-0000-0000-000000000011");
        var corruptHighId = Guid.Parse("83000000-0000-0000-0000-000000000012");
        context.Entities.AddRange(
            new WritableEntity
            {
                Id = expectedId,
                Value = "bounded-range-hit",
                Score = 15,
            },
            new WritableEntity
            {
                Id = corruptHighId,
                Value = "corrupt-above-range",
                Score = 30,
            });
        context.SaveChanges();
        await CorruptStoredRecordAsync(directory, corruptHighId);
        var minimum = 10;
        var maximum = 20;

        var ids = context.Entities
            .Where(entity => entity.Score >= minimum && entity.Score < maximum)
            .Select(entity => entity.Id)
            .ToList();

        Assert.Equal([expectedId], ids);
    }

    [Fact]
    public async Task LinqRangeThrowsWhenDeclaredIndexFileIsMissing()
    {
        var directory = TestDirectory("query-range-missing-index-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);
        context.Entities.Add(new WritableEntity
        {
            Id = Guid.Parse("83000000-0000-0000-0000-000000000008"),
            Value = "range-missing-index",
            Score = 50,
        });
        context.SaveChanges();
        File.Delete(IndexPath(directory, "Score"));

        var unavailable = Assert.Throws<InvalidOperationException>(
            () => context.Entities.Where(entity => entity.Score >= 10).ToList());
        var rebuilt = await context.Database.RebuildJsonColdStoreIndexesAsync();
        var values = context.Entities
            .Where(entity => entity.Score >= 10)
            .Select(entity => entity.Value)
            .ToList();

        Assert.Contains("index", unavailable.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, rebuilt);
        Assert.Equal(["range-missing-index"], values);
    }

    [Fact]
    public void LinqRangeThrowsWithoutDeclaredIndexByDefault()
    {
        var directory = TestDirectory("query-range-no-index-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContextWithoutIndex>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContextWithoutIndex(builder.Options);
        context.Entities.Add(new WritableEntity
        {
            Id = Guid.Parse("83000000-0000-0000-0000-000000000007"),
            Value = "indexed nowhere",
            Score = 50,
        });
        context.SaveChanges();

        var exception = Assert.Throws<NotSupportedException>(
            () => context.Entities.Where(entity => entity.Score >= 10).ToList());

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
    public async Task LinqIndexedTakeLimitsCandidateMaterialization()
    {
        var directory = TestDirectory("query-index-take-limit-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(
            directory,
            store => store
                .UseFsyncOnWrite(false)
                .UseChecksums(verifyOnStartup: true, verifyOnRead: true));
        using var context = new WritableDbContext(builder.Options);
        var expectedId = Guid.Parse("84000000-0000-0000-0000-000000000001");
        var corruptId = Guid.Parse("84000000-0000-0000-0000-000000000002");
        context.Entities.AddRange(
            new WritableEntity
            {
                Id = expectedId,
                Value = "limited-take",
            },
            new WritableEntity
            {
                Id = corruptId,
                Value = "limited-take",
            });
        context.SaveChanges();
        await CorruptStoredRecordAsync(directory, corruptId);

        var ids = context.Entities
            .Where(entity => entity.Value == "limited-take")
            .Take(1)
            .Select(entity => entity.Id)
            .ToList();

        Assert.Equal([expectedId], ids);
    }

    [Fact]
    public async Task LinqIndexedFirstOrDefaultLimitsCandidateMaterialization()
    {
        var directory = TestDirectory("query-index-first-limit-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(
            directory,
            store => store
                .UseFsyncOnWrite(false)
                .UseChecksums(verifyOnStartup: true, verifyOnRead: true));
        using var context = new WritableDbContext(builder.Options);
        var expectedId = Guid.Parse("84000000-0000-0000-0000-000000000003");
        var corruptId = Guid.Parse("84000000-0000-0000-0000-000000000004");
        context.Entities.AddRange(
            new WritableEntity
            {
                Id = expectedId,
                Value = "limited-first",
            },
            new WritableEntity
            {
                Id = corruptId,
                Value = "limited-first",
            });
        context.SaveChanges();
        await CorruptStoredRecordAsync(directory, corruptId);

        var entity = context.Entities.FirstOrDefault(entity => entity.Value == "limited-first");

        Assert.NotNull(entity);
        Assert.Equal(expectedId, entity.Id);
    }

    [Fact]
    public async Task LinqAnyWithoutPredicateUsesBoundedScanWithoutSilentScan()
    {
        var directory = TestDirectory("query-any-bounded-scan-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(
            directory,
            store => store
                .UseFsyncOnWrite(false)
                .UseChecksums(verifyOnStartup: true, verifyOnRead: true));
        using var context = new WritableDbContext(builder.Options);
        var expectedId = Guid.Parse("85000000-0000-0000-0000-000000000001");
        var corruptId = Guid.Parse("85000000-0000-0000-0000-000000000002");
        context.Entities.AddRange(
            new WritableEntity
            {
                Id = expectedId,
                Value = "bounded-any",
            },
            new WritableEntity
            {
                Id = corruptId,
                Value = "corrupt-bounded-any",
            });
        context.SaveChanges();
        await CorruptCurrentRecordFilesAfterFirstAsync(directory);

        Assert.True(context.Entities.Any());
    }

    [Fact]
    public async Task LinqTakeWithoutPredicateUsesBoundedScanWithoutSilentScan()
    {
        var directory = TestDirectory("query-take-bounded-scan-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(
            directory,
            store => store
                .UseFsyncOnWrite(false)
                .UseChecksums(verifyOnStartup: true, verifyOnRead: true));
        using var context = new WritableDbContext(builder.Options);
        var expectedId = Guid.Parse("85000000-0000-0000-0000-000000000003");
        var corruptId = Guid.Parse("85000000-0000-0000-0000-000000000004");
        context.Entities.AddRange(
            new WritableEntity
            {
                Id = expectedId,
                Value = "bounded-take",
            },
            new WritableEntity
            {
                Id = corruptId,
                Value = "corrupt-bounded-take",
            });
        context.SaveChanges();
        var expectedBoundedId = await CorruptCurrentRecordFilesAfterFirstAsync(directory);

        var ids = context.Entities
            .Take(1)
            .Select(entity => entity.Id)
            .ToList();

        Assert.Equal([expectedBoundedId], ids);
    }

    [Fact]
    public void LinqAnyWithoutPredicateReturnsFalseForEmptyStore()
    {
        var directory = TestDirectory("query-any-empty-bounded-scan-" + Guid.NewGuid().ToString("N"));
        var builder = new DbContextOptionsBuilder<WritableDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        using var context = new WritableDbContext(builder.Options);
        context.Database.EnsureCreated();

        Assert.False(context.Entities.Any());
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

    private static string IndexPath(string directory, string propertyName) =>
        Path.Combine(
            directory,
            "entities",
            JsonColdStoreNameEncoder.EncodePathSegment(typeof(WritableEntity).FullName!),
            "indexes",
            JsonColdStoreNameEncoder.EncodePathSegment(propertyName) + ".json");

    private static string CurrentRecordPath(string directory, Guid id) =>
        CurrentRecordPath(
            directory,
            typeof(WritableEntity).FullName!,
            id.ToString());

    private static string CurrentRecordPath(string directory, string entityName, string recordId) =>
        JsonColdStorePathValidator.GetSafeChildPath(
            directory,
            [.. JsonColdStoreRecordStore.GetRecordPathSegments(
                entityName,
                recordId)]);

    private static string CurrentRecordsDirectory(string directory, string entityName) =>
        Path.GetDirectoryName(CurrentRecordPath(directory, entityName, "probe"))!;

    private static async Task WriteTextFileAsync(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents);
    }

    private static async Task WriteStoreMetadataAsync(
        string directory,
        JsonColdStoreStoreMetadata metadata)
    {
        Directory.CreateDirectory(directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata, StoreJsonOptions);
        await File.WriteAllBytesAsync(Path.Combine(directory, JsonColdStoreCatalog.StoreFileName), bytes);
    }

    private static async Task WriteModelCatalogAsync(string directory, IModel model)
    {
        var descriptor = JsonColdStoreModelDescriptor.Create(model);
        var snapshot = JsonColdStoreModelSnapshot.FromDescriptor(descriptor);
        var hashBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, ModelHashJsonOptions);
        var document = new JsonColdStoreModelDocument
        {
            FormatVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ProviderVersion = JsonColdStoreProviderInfo.Version,
            ModelHash = Convert.ToHexString(SHA256.HashData(hashBytes)).ToLowerInvariant(),
            Model = snapshot,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, ModelCatalogJsonOptions);
        await File.WriteAllBytesAsync(Path.Combine(directory, JsonColdStoreModelCatalog.ModelFileName), bytes);
    }

    private static async Task<string> CreateRequiredLinkedDirectoryWithFileAsync(
        string linkPath,
        string targetPath,
        string fileName,
        string contents,
        string proofName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        Directory.CreateDirectory(targetPath);
        var filePath = Path.Combine(targetPath, fileName);
        await File.WriteAllTextAsync(filePath, contents);
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
            linkPath,
            targetPath,
            proofName);
        return filePath;
    }

    private static async Task WriteLegacyEntityAsync(
        string directory,
        WritableEntity entity,
        JsonColdStoreEncryptionKey? key = null)
    {
        await WriteLegacyEntityAsync(directory, entity, entity.Id, key);
    }

    private static async Task WriteLegacyEntityAsync<TEntity>(
        string directory,
        TEntity entity,
        Guid id,
        JsonColdStoreEncryptionKey? key = null)
        where TEntity : class
    {
        await WriteLegacyEntityFileAsync(directory, typeof(TEntity).Name, $"{id}.json", entity, key);
    }

    private static async Task WriteLegacyEntityFileAsync(
        string directory,
        string fileName,
        WritableEntity entity,
        JsonColdStoreEncryptionKey? key = null)
    {
        await WriteLegacyEntityFileAsync(directory, nameof(WritableEntity), fileName, entity, key);
    }

    private static async Task WriteLegacyEntityFileAsync<TEntity>(
        string directory,
        string legacyEntityDirectoryName,
        string fileName,
        TEntity entity,
        JsonColdStoreEncryptionKey? key = null)
        where TEntity : class
    {
        var entityDirectory = Path.Combine(directory, legacyEntityDirectoryName);
        Directory.CreateDirectory(entityDirectory);
        var json = JsonSerializer.Serialize(
            entity,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var bytes = Encoding.UTF8.GetBytes(json);
        if (key is not null)
            bytes = EncryptLegacyPayload(bytes, key);

        await File.WriteAllBytesAsync(Path.Combine(entityDirectory, fileName), bytes);
    }

    private static async Task<string> WriteLegacySharedRowsAsync(
        string directory,
        string sharedEntityName,
        IEnumerable<Dictionary<string, Guid>> rows,
        JsonColdStoreEncryptionKey? key = null)
    {
        var entityDirectory = Path.Combine(directory, sharedEntityName);
        Directory.CreateDirectory(entityDirectory);
        var json = JsonSerializer.Serialize(
            rows,
            new JsonSerializerOptions { WriteIndented = true });
        var bytes = Encoding.UTF8.GetBytes(json);
        if (key is not null)
            bytes = EncryptLegacyPayload(bytes, key);

        var rowsPath = Path.Combine(entityDirectory, "_rows.json");
        await File.WriteAllBytesAsync(rowsPath, bytes);
        return rowsPath;
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

    private static async Task WriteLegacyKeyScopedIndexAsync(
        string directory,
        string propertyName,
        Guid indexKey,
        IEnumerable<string> recordIds)
    {
        var indexDirectory = Path.Combine(directory, nameof(WritableEntity), $"_index_{propertyName}");
        Directory.CreateDirectory(indexDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(indexDirectory, $"{indexKey:D}.json"),
            JsonSerializer.Serialize(recordIds.ToList(), new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void AssertRedactedLegacyIndexShardException(
        JsonColdStoreUnsafePathException exception,
        string directory,
        string shardPath,
        string outsideFile,
        string outsidePayload)
    {
        Assert.Contains("legacy index shard", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(directory, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(shardPath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outsideFile, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outsidePayload, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> CorruptStoredRecordAsync(string directory, Guid id)
    {
        var recordPath = JsonColdStorePathValidator.GetSafeChildPath(
            directory,
            [.. JsonColdStoreRecordStore.GetRecordPathSegments(
                typeof(WritableEntity).FullName!,
                id.ToString())]);
        await CorruptStoredRecordFileAsync(recordPath);
        return recordPath;
    }

    private static async Task<Guid> CorruptCurrentRecordFilesAfterFirstAsync(string directory)
    {
        var recordsDirectory = JsonColdStorePathValidator.GetSafeChildPath(
            directory,
            "entities",
            JsonColdStoreNameEncoder.EncodePathSegment(typeof(WritableEntity).FullName!),
            "records");
        var recordFiles = Directory.EnumerateFiles(recordsDirectory, "*.jcs")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(recordFiles.Length >= 2);

        var storageOptions = new JsonColdStoreOptionsBuilder(directory)
            .UseFsyncOnWrite(false)
            .UseChecksums(verifyOnStartup: true, verifyOnRead: true)
            .Build();
        var firstPayload = JsonColdStorePayloadCodec.Decode(
            await File.ReadAllBytesAsync(recordFiles[0]),
            storageOptions);
        var firstEntity = JsonSerializer.Deserialize<WritableEntity>(firstPayload);
        Assert.NotNull(firstEntity);

        foreach (var recordFile in recordFiles.Skip(1))
            await CorruptStoredRecordFileAsync(recordFile);

        return firstEntity.Id;
    }

    private static async Task CorruptStoredRecordFileAsync(string recordPath)
    {
        var bytes = await File.ReadAllBytesAsync(recordPath);
        bytes[^1] ^= 0x7F;
        await File.WriteAllBytesAsync(recordPath, bytes);
    }

    private static async Task AppendRecordIdToFirstIndexBucketAsync(string indexPath, string recordId)
    {
        var root = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))?.AsObject()
            ?? throw new InvalidDataException("The index document could not be parsed.");
        var buckets = root["buckets"]?.AsObject()
            ?? throw new InvalidDataException("The index document does not contain buckets.");
        var firstBucket = buckets.FirstOrDefault().Value?.AsArray()
            ?? throw new InvalidDataException("The index document does not contain an index bucket.");

        firstBucket.Add(recordId);
        await File.WriteAllTextAsync(
            indexPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task RemoveRecordIdFromIndexAsync(string indexPath, string recordId)
    {
        var root = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))?.AsObject()
            ?? throw new InvalidDataException("The index document could not be parsed.");
        RemoveRecordIdFromBuckets(root, recordId);
        await File.WriteAllTextAsync(
            indexPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task MoveRecordIdToIndexBucketAsync(
        string indexPath,
        string recordId,
        string indexKey)
    {
        var root = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))?.AsObject()
            ?? throw new InvalidDataException("The index document could not be parsed.");
        var buckets = RemoveRecordIdFromBuckets(root, recordId);
        buckets[indexKey] = new JsonArray { recordId };
        await File.WriteAllTextAsync(
            indexPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonObject RemoveRecordIdFromBuckets(JsonObject root, string recordId)
    {
        var buckets = root["buckets"]?.AsObject()
            ?? throw new InvalidDataException("The index document does not contain buckets.");

        foreach (var bucket in buckets.ToArray())
        {
            var recordIds = bucket.Value?.AsArray()
                ?? throw new InvalidDataException("The index bucket is not an array.");
            for (var index = recordIds.Count - 1; index >= 0; index--)
            {
                if (string.Equals(recordIds[index]?.GetValue<string>(), recordId, StringComparison.Ordinal))
                    recordIds.RemoveAt(index);
            }

            if (recordIds.Count == 0)
                buckets.Remove(bucket.Key);
        }

        return buckets;
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
            entity.HasIndex(value => value.Score);
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
                entity.HasIndex(value => value.Score);
            });
        }
    }

    private sealed class UniqueWritableDbContext(DbContextOptions<UniqueWritableDbContext> options) : DbContext(options)
    {
        public DbSet<WritableEntity> Entities => Set<WritableEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<WritableEntity>(entity =>
            {
                entity.HasKey(value => value.Id);
                entity.HasIndex(value => value.Value).IsUnique();
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

    private sealed class ManyToManyDbContext(DbContextOptions<ManyToManyDbContext> options) : DbContext(options)
    {
        public DbSet<ManyToManyPost> Posts => Set<ManyToManyPost>();

        public DbSet<ManyToManyTag> Tags => Set<ManyToManyTag>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ManyToManyPost>().HasKey(value => value.Id);
            modelBuilder.Entity<ManyToManyTag>().HasKey(value => value.Id);
            modelBuilder.Entity<ManyToManyPost>()
                .HasMany(value => value.Tags)
                .WithMany(value => value.Posts)
                .UsingEntity<Dictionary<string, object>>(
                    "ManyToManyPostTag",
                    right => right
                        .HasOne<ManyToManyTag>()
                        .WithMany()
                        .HasForeignKey("TagId"),
                    left => left
                        .HasOne<ManyToManyPost>()
                        .WithMany()
                        .HasForeignKey("PostId"),
                    join =>
                    {
                        join.IndexerProperty<Guid>("PostId");
                        join.IndexerProperty<Guid>("TagId");
                        join.HasKey("PostId", "TagId");
                    });
        }
    }

    private sealed class ManyToManyPost
    {
        public Guid Id { get; set; }

        public List<ManyToManyTag> Tags { get; } = [];
    }

    private sealed class ManyToManyTag
    {
        public Guid Id { get; set; }

        public List<ManyToManyPost> Posts { get; } = [];
    }

    private sealed class WritableEntity
    {
        public Guid Id { get; set; }

        public string Value { get; set; } = string.Empty;

        public int Score { get; set; }
    }
}
