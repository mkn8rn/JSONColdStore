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

        Assert.Empty(await entityStore.ReadEntitiesByIndexAsync<ConsumerEvent>("ConsumerId", "rebuild"));
        var rebuilt = await entityStore.RebuildIndexesAsync<ConsumerEvent>();

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
        });

        return modelBuilder.FinalizeModel();
    }

    private static string NewTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "jsoncoldstore-entity-record-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

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
