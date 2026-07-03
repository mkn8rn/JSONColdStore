using JSONColdStore.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace JSONColdStore.Tests;

public sealed class JsonColdStoreModelDescriptorTests
{
    [Fact]
    public void CreateCapturesEntityNamesKeysAndIndexes()
    {
        var model = CreateModel(modelBuilder =>
        {
            modelBuilder.Entity<ConsumerEvent>(entity =>
            {
                entity.HasKey(value => value.Id);
                entity.HasIndex(value => value.ConsumerId);
                entity.HasIndex(value => new { value.ConsumerId, value.CreatedAt });
            });
        });

        var descriptor = JsonColdStoreModelDescriptor.Create(model);
        var entity = descriptor.FindEntity(typeof(ConsumerEvent));

        Assert.Equal(typeof(ConsumerEvent).FullName, entity.EntityName);
        Assert.Equal(typeof(ConsumerEvent), entity.ClrType);
        Assert.Equal(["Id"], entity.Key.PropertyNames);
        Assert.Equal([typeof(Guid)], entity.Key.ClrTypes);
        Assert.Equal("a4b40f00-6a1b-48dd-b544-9b6924e0f4f2", entity.CreateRecordId(
            Guid.Parse("a4b40f00-6a1b-48dd-b544-9b6924e0f4f2")));
        Assert.Contains(entity.Indexes, index => index.PropertyNames.SequenceEqual(["ConsumerId"]));
        Assert.Contains(entity.Indexes, index => index.PropertyNames.SequenceEqual(["ConsumerId", "CreatedAt"]));
        Assert.Equal("ConsumerId", entity.FindSinglePropertyIndex("ConsumerId").StorageName);
    }

    [Fact]
    public void CreateRejectsKeylessEntityTypes()
    {
        var model = CreateModel(modelBuilder =>
        {
            modelBuilder.Entity<KeylessEvent>().HasNoKey();
        });

        var exception = Assert.Throws<NotSupportedException>(
            () => JsonColdStoreModelDescriptor.Create(model));

        Assert.Contains("must define a primary key", exception.Message);
    }

    [Fact]
    public void CreateCapturesCompositePrimaryKeys()
    {
        var model = CreateModel(modelBuilder =>
        {
            modelBuilder.Entity<CompositeKeyEvent>()
                .HasKey(value => new { value.PartitionId, value.Id });
        });

        var descriptor = JsonColdStoreModelDescriptor.Create(model);
        var entity = descriptor.FindEntity(typeof(CompositeKeyEvent));

        Assert.Equal(["PartitionId", "Id"], entity.Key.PropertyNames);
        Assert.Equal([typeof(string), typeof(int)], entity.Key.ClrTypes);
        Assert.Equal(
            "partition-a\u001F7",
            entity.CreateRecordIdFromEntity(new CompositeKeyEvent
            {
                PartitionId = "partition-a",
                Id = 7,
            }));
    }

    [Fact]
    public void CreateCapturesSharedTypeJoinEntities()
    {
        var model = CreateModel(modelBuilder =>
        {
            modelBuilder.Entity<SharedPost>().HasKey(value => value.Id);
            modelBuilder.Entity<SharedTag>().HasKey(value => value.Id);
            modelBuilder.SharedTypeEntity<Dictionary<string, object>>(
                "SharedPostSharedTag",
                entity =>
                {
                    entity.IndexerProperty<Guid>("PostsId");
                    entity.IndexerProperty<Guid>("TagsId");
                    entity.HasKey("PostsId", "TagsId");
                });
        });

        var descriptor = JsonColdStoreModelDescriptor.Create(model);

        Assert.Contains(descriptor.Entities, entity => entity.ClrType == typeof(SharedPost));
        Assert.Contains(descriptor.Entities, entity => entity.ClrType == typeof(SharedTag));
        var sharedEntity = Assert.Single(descriptor.Entities, entity => entity.IsSharedType);
        Assert.Equal(typeof(Dictionary<string, object>), sharedEntity.ClrType);
        Assert.Equal(2, sharedEntity.Key.PropertyNames.Count);
        Assert.All(sharedEntity.Key.PropertyNames, propertyName => Assert.False(string.IsNullOrWhiteSpace(propertyName)));
    }

    [Fact]
    public void CreateRecordIdRejectsNullOrEmptyKeys()
    {
        var key = new JsonColdStoreKeyDescriptor(
            [new JsonColdStorePropertyDescriptor("Id", typeof(string), null)]);

        Assert.Throws<InvalidOperationException>(() => key.CreateRecordId(null));
        Assert.Throws<InvalidOperationException>(() => key.CreateRecordId(" "));
    }

    [Fact]
    public void FindSinglePropertyIndexRejectsUndeclaredIndex()
    {
        var descriptor = JsonColdStoreModelDescriptor.Create(CreateModel(modelBuilder =>
        {
            modelBuilder.Entity<ConsumerEvent>(entity =>
            {
                entity.HasKey(value => value.Id);
                entity.HasIndex(value => value.ConsumerId);
            });
        })).FindEntity(typeof(ConsumerEvent));

        Assert.Throws<InvalidOperationException>(() => descriptor.FindSinglePropertyIndex("Missing"));
    }

    private static IModel CreateModel(Action<ModelBuilder> configure)
    {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        configure(modelBuilder);
        return modelBuilder.FinalizeModel();
    }

    private sealed class ConsumerEvent
    {
        public Guid Id { get; set; }

        public string ConsumerId { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; }
    }

    private sealed class KeylessEvent
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class CompositeKeyEvent
    {
        public string PartitionId { get; set; } = string.Empty;

        public int Id { get; set; }
    }

    private sealed class SharedPost
    {
        public Guid Id { get; set; }

        public List<SharedTag> Tags { get; } = [];
    }

    private sealed class SharedTag
    {
        public Guid Id { get; set; }

        public List<SharedPost> Posts { get; } = [];
    }
}
