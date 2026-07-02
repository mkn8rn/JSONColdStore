using System.Text;
using System.Text.Json;
using EFC.JSONColdStore;
using EFC.JSONColdStore.Infrastructure;
using EFC.JSONColdStore.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace EFC.JSONColdStore.Tests;

public sealed class JsonColdStoreEntityRecordStoreTests
{
    [Fact]
    public async Task WriteEntityAsyncPersistsEncryptedEntityByModelKey()
    {
        var root = NewTempDirectory();
        using var key = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseEncryptionKey(key)
            .UseCompression(JsonColdStoreCompression.Brotli)
            .UseFsyncOnWrite(false)
            .Build();
        var model = CreateModel();

        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(options);
        var entityStore = new JsonColdStoreEntityRecordStore(
            session,
            JsonColdStoreModelDescriptor.Create(model));
        var entity = new ConsumerEvent
        {
            Id = Guid.Parse("6e521e07-bc75-4301-a3e1-5716d775845a"),
            ConsumerId = "consumer-1",
            Payload = "secret payload",
        };

        await entityStore.WriteEntityAsync(entity);

        var descriptor = JsonColdStoreModelDescriptor.Create(model).FindEntity(typeof(ConsumerEvent));
        Assert.True(session.Records.RecordExists(descriptor.EntityName, entity.Id.ToString()));
        var read = await entityStore.ReadEntityAsync<ConsumerEvent>(entity.Id);
        Assert.NotNull(read);
        Assert.Equal(entity.Id, read.Id);
        Assert.Equal(entity.ConsumerId, read.ConsumerId);
        Assert.Equal(entity.Payload, read.Payload);

        var indexPath = Path.Combine(
            root,
            "entities",
            JsonColdStoreNameEncoder.EncodePathSegment(descriptor.EntityName),
            "indexes",
            JsonColdStoreNameEncoder.EncodePathSegment("ConsumerId") + ".json");
        var indexBytes = await File.ReadAllBytesAsync(indexPath);
        var modelCatalogBytes = await File.ReadAllBytesAsync(Path.Combine(root, "_model.json"));
        Assert.False(ContainsBytes(indexBytes, entity.ConsumerId));
        Assert.False(ContainsBytes(modelCatalogBytes, nameof(ConsumerEvent.ConsumerId)));

        var indexed = await entityStore.ReadEntitiesByIndexAsync<ConsumerEvent>("ConsumerId", entity.ConsumerId);
        Assert.Single(indexed);
        Assert.Equal(entity.Id, indexed[0].Id);
    }

    [Fact]
    public async Task WriteEntityAsyncCreatesModelCatalog()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(options);
        var entityStore = new JsonColdStoreEntityRecordStore(
            session,
            JsonColdStoreModelDescriptor.Create(CreateModel()));

        await entityStore.WriteEntityAsync(new ConsumerEvent
        {
            Id = Guid.Parse("70000000-0000-0000-0000-000000000001"),
            ConsumerId = "catalog",
            Payload = "value",
        });

        var catalogPath = Path.Combine(root, "_model.json");
        Assert.True(File.Exists(catalogPath));
        var catalogJson = await File.ReadAllTextAsync(catalogPath);
        Assert.Contains("\"modelHash\"", catalogJson);
        Assert.Contains(nameof(ConsumerEvent.ConsumerId), catalogJson);
    }

    [Fact]
    public async Task ReadEntityAsyncRejectsChangedModelCatalog()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var entity = new ConsumerEvent
        {
            Id = Guid.Parse("71000000-0000-0000-0000-000000000001"),
            ConsumerId = "catalog-mismatch",
            Payload = "value",
        };

        await using (var session = await JsonColdStoreDatabaseSession.OpenAsync(options))
        {
            var entityStore = new JsonColdStoreEntityRecordStore(
                session,
                JsonColdStoreModelDescriptor.Create(CreateModel()));
            await entityStore.WriteEntityAsync(entity);
        }

        await using (var session = await JsonColdStoreDatabaseSession.OpenAsync(options))
        {
            var entityStore = new JsonColdStoreEntityRecordStore(
                session,
                JsonColdStoreModelDescriptor.Create(CreateModelWithoutIndexes()));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => entityStore.ReadEntityAsync<ConsumerEvent>(entity.Id));

            Assert.Contains("model catalog", exception.Message);
        }
    }

    [Fact]
    public async Task ReadEntityAsyncRejectsTamperedModelCatalog()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var entity = new ConsumerEvent
        {
            Id = Guid.Parse("72000000-0000-0000-0000-000000000001"),
            ConsumerId = "catalog-tamper",
            Payload = "value",
        };

        await using (var session = await JsonColdStoreDatabaseSession.OpenAsync(options))
        {
            var entityStore = new JsonColdStoreEntityRecordStore(
                session,
                JsonColdStoreModelDescriptor.Create(CreateModel()));
            await entityStore.WriteEntityAsync(entity);
        }

        var catalogPath = Path.Combine(root, "_model.json");
        var catalogJson = await File.ReadAllTextAsync(catalogPath);
        await File.WriteAllTextAsync(
            catalogPath,
            catalogJson.Replace(nameof(ConsumerEvent.ConsumerId), "TamperedConsumerId", StringComparison.Ordinal));

        await using (var session = await JsonColdStoreDatabaseSession.OpenAsync(options))
        {
            var entityStore = new JsonColdStoreEntityRecordStore(
                session,
                JsonColdStoreModelDescriptor.Create(CreateModel()));

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => entityStore.ReadEntityAsync<ConsumerEvent>(entity.Id));

            Assert.Contains("hash", exception.Message);
        }
    }

    [Fact]
    public async Task WriteEntityAsyncMaintainsDeclaredIndex()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(options);
        var entityStore = new JsonColdStoreEntityRecordStore(
            session,
            JsonColdStoreModelDescriptor.Create(CreateModel()));
        var first = new ConsumerEvent
        {
            Id = Guid.Parse("10000000-0000-0000-0000-000000000001"),
            ConsumerId = "consumer-a",
            Payload = "first",
        };
        var second = new ConsumerEvent
        {
            Id = Guid.Parse("10000000-0000-0000-0000-000000000002"),
            ConsumerId = "consumer-b",
            Payload = "second",
        };

        await entityStore.WriteEntityAsync(first);
        await entityStore.WriteEntityAsync(second);

        var indexed = await entityStore.ReadEntitiesByIndexAsync<ConsumerEvent>("ConsumerId", "consumer-a");
        Assert.Single(indexed);
        Assert.Equal(first.Id, indexed[0].Id);
    }

    [Fact]
    public async Task WriteEntityAsyncMaintainsBlankStringIndexValue()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(options);
        var entityStore = new JsonColdStoreEntityRecordStore(
            session,
            JsonColdStoreModelDescriptor.Create(CreateModel()));
        var entity = new ConsumerEvent
        {
            Id = Guid.Parse("10000000-0000-0000-0000-000000000003"),
            ConsumerId = string.Empty,
            Payload = "blank indexed value",
        };

        await entityStore.WriteEntityAsync(entity);

        var indexed = await entityStore.ReadEntitiesByIndexAsync<ConsumerEvent>("ConsumerId", string.Empty);
        var verification = await entityStore.VerifyEntitiesAsync();

        Assert.Single(indexed);
        Assert.Equal(entity.Id, indexed[0].Id);
        Assert.Equal(1, verification.VerifiedIndexes);
    }

    [Fact]
    public async Task WriteEntityAsyncMovesUpdatedRecordBetweenIndexBuckets()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(options);
        var entityStore = new JsonColdStoreEntityRecordStore(
            session,
            JsonColdStoreModelDescriptor.Create(CreateModel()));
        var entity = new ConsumerEvent
        {
            Id = Guid.Parse("20000000-0000-0000-0000-000000000001"),
            ConsumerId = "old",
            Payload = "value",
        };

        await entityStore.WriteEntityAsync(entity);
        entity.ConsumerId = "new";
        await entityStore.WriteEntityAsync(entity);

        Assert.Empty(await entityStore.ReadEntitiesByIndexAsync<ConsumerEvent>("ConsumerId", "old"));
        var indexed = await entityStore.ReadEntitiesByIndexAsync<ConsumerEvent>("ConsumerId", "new");
        Assert.Single(indexed);
        Assert.Equal(entity.Id, indexed[0].Id);
    }

    [Fact]
    public async Task DeleteEntityAsyncRemovesDeclaredIndexEntries()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(options);
        var entityStore = new JsonColdStoreEntityRecordStore(
            session,
            JsonColdStoreModelDescriptor.Create(CreateModel()));
        var entity = new ConsumerEvent
        {
            Id = Guid.Parse("30000000-0000-0000-0000-000000000001"),
            ConsumerId = "delete-index",
            Payload = "value",
        };

        await entityStore.WriteEntityAsync(entity);
        await entityStore.DeleteEntityAsync(entity, typeof(ConsumerEvent));

        Assert.Empty(await entityStore.ReadEntitiesByIndexAsync<ConsumerEvent>("ConsumerId", "delete-index"));
    }

    [Fact]
    public async Task DeleteRecordIfExistsRejectsReparsePointLegacyRecordFile()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var descriptor = JsonColdStoreModelDescriptor.Create(CreateModel())
            .FindEntity(typeof(ConsumerEvent));
        var legacyDirectory = Path.Combine(root, nameof(ConsumerEvent));
        Directory.CreateDirectory(legacyDirectory);
        var outsideFile = Path.Combine(outside, "outside.json");
        await File.WriteAllTextAsync(outsideFile, "outside-legacy-record");
        JsonColdStoreReparsePointTestHelper.CreateRequiredFileLink(
            Path.Combine(legacyDirectory, "legacy-delete.json"),
            outsideFile,
            nameof(DeleteRecordIfExistsRejectsReparsePointLegacyRecordFile));
        var legacyStore = new JsonColdStoreLegacyRecordStore(options);

        Assert.Throws<JsonColdStoreUnsafePathException>(
            () => legacyStore.DeleteRecordIfExists(descriptor, "legacy-delete"));

        Assert.Equal("outside-legacy-record", await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public async Task DeleteSharedRowsIfExistsRejectsReparsePointLegacyRowsDocument()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var descriptor = JsonColdStoreModelDescriptor.Create(CreateSharedRowsModel())
            .Entities
            .Single(entity => entity.IsSharedType);
        var legacyDirectory = Path.Combine(root, descriptor.EntityName);
        Directory.CreateDirectory(legacyDirectory);
        var outsideFile = Path.Combine(outside, "_rows.json");
        await File.WriteAllTextAsync(outsideFile, "outside-legacy-rows");
        JsonColdStoreReparsePointTestHelper.CreateRequiredFileLink(
            Path.Combine(legacyDirectory, "_rows.json"),
            outsideFile,
            nameof(DeleteSharedRowsIfExistsRejectsReparsePointLegacyRowsDocument));
        var legacyStore = new JsonColdStoreLegacyRecordStore(options);

        Assert.Throws<JsonColdStoreUnsafePathException>(
            () => legacyStore.DeleteSharedRowsIfExists(descriptor));

        Assert.Equal("outside-legacy-rows", await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public async Task ReadEntityAsyncRejectsReparsePointLegacyRecordExistenceTarget()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var id = Guid.Parse("41000000-0000-0000-0000-000000000011");
        var legacyDirectory = Path.Combine(root, nameof(ConsumerEvent));
        Directory.CreateDirectory(legacyDirectory);
        var outsideFile = await WriteLegacyConsumerEventAsync(
            outside,
            new ConsumerEvent
            {
                Id = id,
                ConsumerId = "outside-linked-existence",
                Payload = "outside linked existence payload",
            });
        var linkPath = Path.Combine(legacyDirectory, id + ".json");
        JsonColdStoreReparsePointTestHelper.CreateRequiredFileLink(
            linkPath,
            outsideFile,
            nameof(ReadEntityAsyncRejectsReparsePointLegacyRecordExistenceTarget));
        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(options);
        var entityStore = new JsonColdStoreEntityRecordStore(
            session,
            JsonColdStoreModelDescriptor.Create(CreateModel()));

        var exception = await Assert.ThrowsAsync<JsonColdStoreUnsafePathException>(
            () => entityStore.ReadEntityAsync<ConsumerEvent>(id));

        Assert.Contains("legacy record existence", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(root, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(linkPath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outsideFile, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outside linked existence payload", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("outside linked existence payload", await ReadLegacyConsumerEventPayloadAsync(outsideFile));
    }

    [Fact]
    public void RecordExistsReturnsFalseWhenLegacyRecordFileIsMissing()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        Directory.CreateDirectory(Path.Combine(root, nameof(ConsumerEvent)));
        var descriptor = JsonColdStoreModelDescriptor.Create(CreateModel())
            .FindEntity(typeof(ConsumerEvent));
        var legacyStore = new JsonColdStoreLegacyRecordStore(options);

        Assert.False(legacyStore.RecordExists(
            descriptor,
            "41000000-0000-0000-0000-000000000012"));
    }

    [Fact]
    public async Task SharedRowsDocumentExistsRejectsReparsePointLegacyRowsExistenceTarget()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var descriptor = JsonColdStoreModelDescriptor.Create(CreateSharedRowsModel())
            .Entities
            .Single(entity => entity.IsSharedType);
        var legacyDirectory = Path.Combine(root, descriptor.EntityName);
        Directory.CreateDirectory(legacyDirectory);
        var outsideFile = Path.Combine(outside, "_rows.json");
        await File.WriteAllTextAsync(outsideFile, "outside linked shared rows");
        var linkPath = Path.Combine(legacyDirectory, "_rows.json");
        JsonColdStoreReparsePointTestHelper.CreateRequiredFileLink(
            linkPath,
            outsideFile,
            nameof(SharedRowsDocumentExistsRejectsReparsePointLegacyRowsExistenceTarget));
        var legacyStore = new JsonColdStoreLegacyRecordStore(options);

        var exception = Assert.Throws<JsonColdStoreUnsafePathException>(
            () => legacyStore.SharedRowsDocumentExists(descriptor));

        Assert.Contains("legacy shared rows existence", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(root, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(linkPath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outsideFile, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outside linked shared rows", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("outside linked shared rows", await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public void SharedRowsDocumentExistsReturnsFalseWhenLegacyRowsFileIsMissing()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var descriptor = JsonColdStoreModelDescriptor.Create(CreateSharedRowsModel())
            .Entities
            .Single(entity => entity.IsSharedType);
        Directory.CreateDirectory(Path.Combine(root, descriptor.EntityName));
        var legacyStore = new JsonColdStoreLegacyRecordStore(options);

        Assert.False(legacyStore.SharedRowsDocumentExists(descriptor));
    }

    [Fact]
    public async Task ReadEntityAsyncReadsNormalLegacyEntityDirectory()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var id = Guid.Parse("41000000-0000-0000-0000-000000000001");
        await WriteLegacyConsumerEventAsync(
            root,
            new ConsumerEvent
            {
                Id = id,
                ConsumerId = "normal-legacy-read",
                Payload = "normal legacy payload",
            });
        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(options);
        var entityStore = new JsonColdStoreEntityRecordStore(
            session,
            JsonColdStoreModelDescriptor.Create(CreateModel()));

        var read = await entityStore.ReadEntityAsync<ConsumerEvent>(id);

        Assert.NotNull(read);
        Assert.Equal(id, read.Id);
        Assert.Equal("normal-legacy-read", read.ConsumerId);
        Assert.Equal("normal legacy payload", read.Payload);
        Assert.True(File.Exists(GetLegacyConsumerEventPath(root, id)));
    }

    [Fact]
    public async Task ImportLegacyRecordsAsyncImportsNormalLegacyEntityDirectory()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var id = Guid.Parse("41000000-0000-0000-0000-000000000002");
        await WriteLegacyConsumerEventAsync(
            root,
            new ConsumerEvent
            {
                Id = id,
                ConsumerId = "normal-legacy-import",
                Payload = "normal import payload",
            });
        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(options);
        var model = CreateModel();
        var descriptor = JsonColdStoreModelDescriptor.Create(model)
            .FindEntity(typeof(ConsumerEvent));
        var entityStore = new JsonColdStoreEntityRecordStore(
            session,
            JsonColdStoreModelDescriptor.Create(model));

        var imported = await entityStore.ImportLegacyRecordsAsync();

        Assert.Equal(1, imported);
        Assert.True(session.Records.RecordExists(descriptor.EntityName, id.ToString()));
        Assert.False(File.Exists(GetLegacyConsumerEventPath(root, id)));
        var read = await entityStore.ReadEntityAsync<ConsumerEvent>(id);
        Assert.NotNull(read);
        Assert.Equal("normal-legacy-import", read.ConsumerId);
    }

    [Fact]
    public async Task ImportLegacyRecordsAsyncImportsNormalLegacySharedRowsDocument()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var descriptor = JsonColdStoreModelDescriptor.Create(CreateSharedRowsModel())
            .Entities
            .Single(entity => entity.IsSharedType);
        var legacyDirectory = Path.Combine(root, descriptor.EntityName);
        Directory.CreateDirectory(legacyDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(legacyDirectory, "_rows.json"),
            """
            [
              {
                "ConsumerEventId": "41000000-0000-0000-0000-000000000013",
                "TagId": "41000000-0000-0000-0000-000000000014"
              }
            ]
            """);
        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(options);
        var entityStore = new JsonColdStoreEntityRecordStore(
            session,
            JsonColdStoreModelDescriptor.Create(CreateSharedRowsModel()));

        var imported = await entityStore.ImportLegacyRecordsAsync();
        var storedRows = 0;
        await foreach (var _ in session.Records.ReadAllNamedRecordsAsync(
            descriptor.EntityName,
            CancellationToken.None))
        {
            storedRows++;
        }

        Assert.Equal(1, imported);
        Assert.Equal(1, storedRows);
        Assert.False(File.Exists(Path.Combine(legacyDirectory, "_rows.json")));
    }

    [Fact]
    public async Task ReadEntityAsyncIgnoresReparsePointLegacyEntityDirectory()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var id = Guid.Parse("41000000-0000-0000-0000-000000000003");
        var outsideFile = await WriteLegacyConsumerEventAsync(
            outside,
            new ConsumerEvent
            {
                Id = id,
                ConsumerId = "outside-legacy-read",
                Payload = "outside legacy payload",
            });
        var outsideJson = await File.ReadAllTextAsync(outsideFile);
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
            Path.Combine(root, nameof(ConsumerEvent)),
            outside,
            nameof(ReadEntityAsyncIgnoresReparsePointLegacyEntityDirectory));
        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(options);
        var entityStore = new JsonColdStoreEntityRecordStore(
            session,
            JsonColdStoreModelDescriptor.Create(CreateModel()));

        var read = await entityStore.ReadEntityAsync<ConsumerEvent>(id);

        Assert.Null(read);
        Assert.Equal(outsideJson, await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public async Task ImportLegacyRecordsAsyncSkipsReparsePointLegacyEntityDirectory()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var id = Guid.Parse("41000000-0000-0000-0000-000000000004");
        var outsideFile = await WriteLegacyConsumerEventAsync(
            outside,
            new ConsumerEvent
            {
                Id = id,
                ConsumerId = "outside-legacy-import",
                Payload = "outside import payload",
            });
        var outsideJson = await File.ReadAllTextAsync(outsideFile);
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
            Path.Combine(root, nameof(ConsumerEvent)),
            outside,
            nameof(ImportLegacyRecordsAsyncSkipsReparsePointLegacyEntityDirectory));
        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(options);
        var model = CreateModel();
        var descriptor = JsonColdStoreModelDescriptor.Create(model)
            .FindEntity(typeof(ConsumerEvent));
        var entityStore = new JsonColdStoreEntityRecordStore(
            session,
            JsonColdStoreModelDescriptor.Create(model));

        var imported = await entityStore.ImportLegacyRecordsAsync();

        Assert.Equal(0, imported);
        Assert.False(session.Records.RecordExists(descriptor.EntityName, id.ToString()));
        Assert.Equal(outsideJson, await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public async Task ImportLegacyRecordsAsyncSkipsReparsePointLegacySharedRowsDirectory()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var descriptor = JsonColdStoreModelDescriptor.Create(CreateSharedRowsModel())
            .Entities
            .Single(entity => entity.IsSharedType);
        var outsideRowsPath = Path.Combine(outside, "_rows.json");
        await File.WriteAllTextAsync(
            outsideRowsPath,
            """
            [
              {
                "ConsumerEventId": "41000000-0000-0000-0000-000000000005",
                "TagId": "41000000-0000-0000-0000-000000000006"
              }
            ]
            """);
        var outsideJson = await File.ReadAllTextAsync(outsideRowsPath);
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
            Path.Combine(root, descriptor.EntityName),
            outside,
            nameof(ImportLegacyRecordsAsyncSkipsReparsePointLegacySharedRowsDirectory));
        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(options);
        var entityStore = new JsonColdStoreEntityRecordStore(
            session,
            JsonColdStoreModelDescriptor.Create(CreateSharedRowsModel()));

        var imported = await entityStore.ImportLegacyRecordsAsync();
        var storedRows = 0;
        await foreach (var _ in session.Records.ReadAllNamedRecordsAsync(
            descriptor.EntityName,
            CancellationToken.None))
        {
            storedRows++;
        }

        Assert.Equal(0, imported);
        Assert.Equal(0, storedRows);
        Assert.Equal(outsideJson, await File.ReadAllTextAsync(outsideRowsPath));
    }

    [Fact]
    public async Task DeleteRecordIfExistsRejectsReparsePointLegacyEntityDirectory()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var descriptor = JsonColdStoreModelDescriptor.Create(CreateModel())
            .FindEntity(typeof(ConsumerEvent));
        var id = Guid.Parse("41000000-0000-0000-0000-000000000007");
        var outsideFile = await WriteLegacyConsumerEventAsync(
            outside,
            new ConsumerEvent
            {
                Id = id,
                ConsumerId = "outside-delete-directory",
                Payload = "outside delete directory payload",
            });
        var outsideJson = await File.ReadAllTextAsync(outsideFile);
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
            Path.Combine(root, nameof(ConsumerEvent)),
            outside,
            nameof(DeleteRecordIfExistsRejectsReparsePointLegacyEntityDirectory));
        var legacyStore = new JsonColdStoreLegacyRecordStore(options);

        Assert.Throws<JsonColdStoreUnsafePathException>(
            () => legacyStore.DeleteRecordIfExists(descriptor, id.ToString()));

        Assert.Equal(outsideJson, await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public async Task DeleteSharedRowsIfExistsRejectsReparsePointLegacyRowsDirectory()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var descriptor = JsonColdStoreModelDescriptor.Create(CreateSharedRowsModel())
            .Entities
            .Single(entity => entity.IsSharedType);
        var outsideRowsPath = Path.Combine(outside, "_rows.json");
        await File.WriteAllTextAsync(outsideRowsPath, "outside-linked-rows-directory");
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
            Path.Combine(root, descriptor.EntityName),
            outside,
            nameof(DeleteSharedRowsIfExistsRejectsReparsePointLegacyRowsDirectory));
        var legacyStore = new JsonColdStoreLegacyRecordStore(options);

        Assert.Throws<JsonColdStoreUnsafePathException>(
            () => legacyStore.DeleteSharedRowsIfExists(descriptor));

        Assert.Equal("outside-linked-rows-directory", await File.ReadAllTextAsync(outsideRowsPath));
    }

    [Fact]
    public async Task RebuildIndexesAsyncRecreatesMissingIndexEntries()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(options);
        var entityStore = new JsonColdStoreEntityRecordStore(
            session,
            JsonColdStoreModelDescriptor.Create(CreateModel()));
        var entity = new ConsumerEvent
        {
            Id = Guid.Parse("35000000-0000-0000-0000-000000000001"),
            ConsumerId = "rebuild",
            Payload = "value",
        };
        await entityStore.WriteEntityAsync(entity);
        Directory.Delete(Path.Combine(
            root,
            "entities",
            JsonColdStoreNameEncoder.EncodePathSegment(typeof(ConsumerEvent).FullName!),
            "indexes"), recursive: true);

        var unavailable = await Assert.ThrowsAsync<InvalidOperationException>(
            () => entityStore.ReadEntitiesByIndexAsync<ConsumerEvent>("ConsumerId", "rebuild"));
        var rebuilt = await entityStore.RebuildIndexesAsync<ConsumerEvent>();

        Assert.Contains("index", unavailable.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, rebuilt);
        var indexed = await entityStore.ReadEntitiesByIndexAsync<ConsumerEvent>("ConsumerId", "rebuild");
        Assert.Single(indexed);
        Assert.Equal(entity.Id, indexed[0].Id);
    }

    [Fact]
    public async Task ReadEntityAsyncReturnsNullWhenRecordDoesNotExist()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(options);
        var entityStore = new JsonColdStoreEntityRecordStore(
            session,
            JsonColdStoreModelDescriptor.Create(CreateModel()));

        var read = await entityStore.ReadEntityAsync<ConsumerEvent>(Guid.NewGuid());

        Assert.Null(read);
    }

    [Fact]
    public async Task WriteEntityAsyncRejectsEntityOutsideModel()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        await using var session = await JsonColdStoreDatabaseSession.OpenAsync(options);
        var entityStore = new JsonColdStoreEntityRecordStore(
            session,
            JsonColdStoreModelDescriptor.Create(CreateModel()));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => entityStore.WriteEntityAsync(new UnmodeledEntity { Id = 1 }));
    }

    [Fact]
    public void CreateRecordIdFromEntityReadsPrimaryKeyProperty()
    {
        var descriptor = JsonColdStoreModelDescriptor.Create(CreateModel())
            .FindEntity(typeof(ConsumerEvent));
        var entity = new ConsumerEvent
        {
            Id = Guid.Parse("5a77242b-b4ed-42a5-8aae-fb0c8ad2cf55"),
        };

        var recordId = descriptor.CreateRecordIdFromEntity(entity);

        Assert.Equal("5a77242b-b4ed-42a5-8aae-fb0c8ad2cf55", recordId);
    }

    private static IModel CreateModel()
    {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        modelBuilder.Entity<ConsumerEvent>(entity =>
        {
            entity.HasKey(value => value.Id);
            entity.HasIndex(value => value.ConsumerId);
            entity.Property(value => value.Payload);
        });

        return modelBuilder.FinalizeModel();
    }

    private static IModel CreateModelWithoutIndexes()
    {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        modelBuilder.Entity<ConsumerEvent>(entity =>
        {
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Payload);
        });

        return modelBuilder.FinalizeModel();
    }

    private static IModel CreateSharedRowsModel()
    {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        modelBuilder.SharedTypeEntity<Dictionary<string, object>>(
            "ConsumerEventTag",
            entity =>
            {
                entity.IndexerProperty<Guid>("ConsumerEventId");
                entity.IndexerProperty<Guid>("TagId");
                entity.HasKey("ConsumerEventId", "TagId");
            });

        return modelBuilder.FinalizeModel();
    }

    private static string NewTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "jsoncoldstore-entity-record-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<string> WriteLegacyConsumerEventAsync(
        string root,
        ConsumerEvent entity)
    {
        var path = GetLegacyConsumerEventPath(root, entity.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(entity));
        return path;
    }

    private static string GetLegacyConsumerEventPath(string root, Guid id) =>
        Path.Combine(root, nameof(ConsumerEvent), id + ".json");

    private static async Task<string> ReadLegacyConsumerEventPayloadAsync(string path)
    {
        var entity = JsonSerializer.Deserialize<ConsumerEvent>(await File.ReadAllTextAsync(path));
        return entity?.Payload ?? string.Empty;
    }

    private static bool ContainsBytes(byte[] haystack, string needle) =>
        haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0;

    private sealed class ConsumerEvent
    {
        public Guid Id { get; set; }

        public string ConsumerId { get; set; } = string.Empty;

        public string Payload { get; set; } = string.Empty;
    }

    private sealed class UnmodeledEntity
    {
        public int Id { get; set; }
    }
}
