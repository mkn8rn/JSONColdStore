using JSONColdStore;
using Microsoft.EntityFrameworkCore;

namespace JSONColdStore.Tests;

public sealed class JsonColdStoreShadowPropertyTests
{
    [Fact]
    public async Task SharpClawExecutionMetadataShadowPropertiesRoundTripThroughTrackedEntities()
    {
        var directory = TestDirectory("shadow-execution-metadata-" + Guid.NewGuid().ToString("N"));
        var options = CreateOptions(directory);
        var artifactId = Guid.NewGuid();

        await using (var context = new ShadowMetadataDbContext(options))
        {
            Assert.True(context.Database.EnsureCreated());

            var entity = new ShadowMetadataEntity
            {
                Id = Guid.NewGuid(),
                Value = "queued",
            };
            context.Entities.Add(entity);
            SetShadowValues(
                context,
                entity,
                artifactId,
                "application/json",
                128,
                "sha256",
                "preview",
                "none",
                null,
                ShadowDiagnosticCompleteness.Complete,
                7,
                3);

            Assert.Equal(1, await context.SaveChangesAsync());
        }

        await using (var context = new ShadowMetadataDbContext(options))
        {
            var loaded = await context.Entities.SingleAsync(entity => entity.Value == "queued");
            var entry = context.Entry(loaded);

            Assert.Equal(EntityState.Unchanged, entry.State);
            Assert.Equal(artifactId, entry.Property<Guid?>("ResultArtifactId").CurrentValue);
            Assert.Equal("application/json", entry.Property<string?>("ResultMediaType").CurrentValue);
            Assert.Equal(128, entry.Property<long?>("ResultLength").CurrentValue);
            Assert.Equal("sha256", entry.Property<string?>("ResultSha256").CurrentValue);
            Assert.Equal("preview", entry.Property<string?>("ResultPreview").CurrentValue);
            Assert.Equal("none", entry.Property<string?>("ErrorCode").CurrentValue);
            Assert.Null(entry.Property<string?>("ErrorMessage").CurrentValue);
            Assert.Equal(
                ShadowDiagnosticCompleteness.Complete,
                entry.Property<ShadowDiagnosticCompleteness>("DiagnosticCompleteness").CurrentValue);
            Assert.Equal(7, entry.Property<long?>("FinalLogSequence").CurrentValue);
            Assert.Equal(3, entry.Property<long>("LogRecordCount").CurrentValue);

            entry.Property<string?>("ErrorMessage").CurrentValue = "retryable";
            entry.Property<long>("LogRecordCount").CurrentValue = 4;

            Assert.Equal(1, await context.SaveChangesAsync());
        }

        await using (var context = new ShadowMetadataDbContext(options))
        {
            var loaded = await context.Entities.SingleAsync(entity => entity.Value == "queued");
            var entry = context.Entry(loaded);

            Assert.Equal("retryable", entry.Property<string?>("ErrorMessage").CurrentValue);
            Assert.Equal(4, entry.Property<long>("LogRecordCount").CurrentValue);
            Assert.Equal(EntityState.Unchanged, entry.State);
        }
    }

    private static DbContextOptions<ShadowMetadataDbContext> CreateOptions(string directory)
    {
        var builder = new DbContextOptionsBuilder<ShadowMetadataDbContext>();
        builder.UseJsonColdStoreDatabase(directory, store => store.UseFsyncOnWrite(false));
        return builder.Options;
    }

    private static void SetShadowValues(
        DbContext context,
        ShadowMetadataEntity entity,
        Guid artifactId,
        string mediaType,
        long length,
        string sha256,
        string preview,
        string errorCode,
        string? errorMessage,
        ShadowDiagnosticCompleteness completeness,
        long finalLogSequence,
        long logRecordCount)
    {
        var entry = context.Entry(entity);
        entry.Property<Guid?>("ResultArtifactId").CurrentValue = artifactId;
        entry.Property<string?>("ResultMediaType").CurrentValue = mediaType;
        entry.Property<long?>("ResultLength").CurrentValue = length;
        entry.Property<string?>("ResultSha256").CurrentValue = sha256;
        entry.Property<string?>("ResultPreview").CurrentValue = preview;
        entry.Property<string?>("ErrorCode").CurrentValue = errorCode;
        entry.Property<string?>("ErrorMessage").CurrentValue = errorMessage;
        entry.Property<ShadowDiagnosticCompleteness>("DiagnosticCompleteness").CurrentValue = completeness;
        entry.Property<long?>("FinalLogSequence").CurrentValue = finalLogSequence;
        entry.Property<long>("LogRecordCount").CurrentValue = logRecordCount;
    }

    private static string TestDirectory(string name) =>
        Path.Combine(Path.GetTempPath(), "jsoncoldstore-tests", name);

    private enum ShadowDiagnosticCompleteness
    {
        Unknown,
        Partial,
        Complete,
    }

    private sealed class ShadowMetadataDbContext(DbContextOptions<ShadowMetadataDbContext> options)
        : DbContext(options)
    {
        public DbSet<ShadowMetadataEntity> Entities => Set<ShadowMetadataEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ShadowMetadataEntity>(entity =>
            {
                entity.HasKey(value => value.Id);
                entity.HasIndex(value => value.Value);
                entity.Property<Guid?>("ResultArtifactId");
                entity.Property<string?>("ResultMediaType");
                entity.Property<long?>("ResultLength");
                entity.Property<string?>("ResultSha256");
                entity.Property<string?>("ResultPreview");
                entity.Property<string?>("ErrorCode");
                entity.Property<string?>("ErrorMessage");
                entity.Property<ShadowDiagnosticCompleteness>("DiagnosticCompleteness")
                    .HasConversion<string>();
                entity.Property<long?>("FinalLogSequence");
                entity.Property<long>("LogRecordCount");
            });
        }
    }

    private sealed class ShadowMetadataEntity
    {
        public Guid Id { get; set; }

        public string Value { get; set; } = string.Empty;
    }
}
