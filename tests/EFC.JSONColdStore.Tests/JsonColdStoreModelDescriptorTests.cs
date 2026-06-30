using EFC.JSONColdStore.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace EFC.JSONColdStore.Tests;

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
        Assert.Equal("Id", entity.Key.PropertyName);
        Assert.Equal(typeof(Guid), entity.Key.ClrType);
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
    public void CreateRejectsCompositePrimaryKeysForFirstProviderVersion()
    {
        var model = CreateModel(modelBuilder =>
        {
            modelBuilder.Entity<CompositeKeyEvent>()
                .HasKey(value => new { value.PartitionId, value.Id });
        });

        var exception = Assert.Throws<NotSupportedException>(
            () => JsonColdStoreModelDescriptor.Create(model));

        Assert.Contains("must use one primary key property", exception.Message);
    }

    [Fact]
    public void CreateRecordIdRejectsNullOrEmptyKeys()
    {
        var key = new JsonColdStoreKeyDescriptor("Id", typeof(string));

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
}
