using System.Text.Json;
using System.Text.Json.Nodes;
using JSONColdStore.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace JSONColdStore.Tests;

public sealed class JsonColdStoreModelCatalogTests
{
    [Fact]
    public async Task EnsureCompatibleAsyncRejectsMissingCreationTimestamp()
    {
        var (catalog, descriptor, modelFile) = await CreateWrittenCatalogAsync("missing-created-at");
        var document = await ReadDocumentAsync(modelFile);
        document["createdAt"] = JsonValue.Create(DateTimeOffset.MinValue);
        await WriteDocumentAsync(modelFile, document);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => catalog.EnsureCompatibleAsync(descriptor, createIfMissing: true));

        Assert.Contains("creation timestamp", exception.Message);
    }

    [Fact]
    public async Task EnsureCompatibleAsyncRejectsMissingProviderVersion()
    {
        var (catalog, descriptor, modelFile) = await CreateWrittenCatalogAsync("missing-provider-version");
        var document = await ReadDocumentAsync(modelFile);
        document["providerVersion"] = " ";
        await WriteDocumentAsync(modelFile, document);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => catalog.EnsureCompatibleAsync(descriptor, createIfMissing: true));

        Assert.Contains("provider version", exception.Message);
    }

    [Fact]
    public async Task EnsureCompatibleAsyncRejectsBlankEntityNameBeforeHashComparison()
    {
        var (catalog, descriptor, modelFile) = await CreateWrittenCatalogAsync("blank-entity-name");
        var document = await ReadDocumentAsync(modelFile);
        GetFirstEntity(document)["entityName"] = " ";
        await WriteDocumentAsync(modelFile, document);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => catalog.EnsureCompatibleAsync(descriptor, createIfMissing: true));

        Assert.Contains("entity name", exception.Message);
    }

    [Fact]
    public async Task EnsureCompatibleAsyncRejectsIndexPropertyTypeCountMismatch()
    {
        var (catalog, descriptor, modelFile) = await CreateWrittenCatalogAsync("index-type-count");
        var document = await ReadDocumentAsync(modelFile);
        var propertyTypeNames = GetFirstIndex(document)["propertyTypeNames"]?.AsArray()
            ?? throw new InvalidDataException("The test model catalog does not contain index property types.");
        propertyTypeNames.RemoveAt(0);
        await WriteDocumentAsync(modelFile, document);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => catalog.EnsureCompatibleAsync(descriptor, createIfMissing: true));

        Assert.Contains("property type count", exception.Message);
    }

    [Fact]
    public async Task EnsureCompatibleAsyncReadsPlaintextCatalogWhenProtectedDocumentsAreExpected()
    {
        var directory = TestDirectory("protected-read-plaintext-" + Guid.NewGuid().ToString("N"));
        using var key = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);
        var options = new JsonColdStoreOptionsBuilder(directory)
            .UseEncryptionKey(key)
            .UseFsyncOnWrite(false)
            .Build();
        var descriptor = CreateDescriptor();
        var plaintextCatalog = new JsonColdStoreModelCatalog(options, protectDocument: false);
        await plaintextCatalog.EnsureCompatibleAsync(descriptor, createIfMissing: true);

        var protectedCatalog = new JsonColdStoreModelCatalog(options, protectDocument: true);
        var compatible = await protectedCatalog.EnsureCompatibleAsync(
            descriptor,
            createIfMissing: false);

        Assert.True(compatible);
        Assert.Contains(
            "ModelCatalogEntity",
            await File.ReadAllTextAsync(Path.Combine(directory, JsonColdStoreModelCatalog.ModelFileName)));
    }

    private static async Task<(
        JsonColdStoreModelCatalog Catalog,
        JsonColdStoreModelDescriptor Descriptor,
        string ModelFile)> CreateWrittenCatalogAsync(string name)
    {
        var directory = TestDirectory(name + "-" + Guid.NewGuid().ToString("N"));
        var options = new JsonColdStoreOptionsBuilder(directory)
            .UseFsyncOnWrite(false)
            .Build();
        var catalog = new JsonColdStoreModelCatalog(options, protectDocument: false);
        var descriptor = CreateDescriptor();

        Assert.True(await catalog.EnsureCompatibleAsync(descriptor, createIfMissing: true));

        return (
            catalog,
            descriptor,
            Path.Combine(directory, JsonColdStoreModelCatalog.ModelFileName));
    }

    private static JsonColdStoreModelDescriptor CreateDescriptor()
    {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        modelBuilder.Entity<ModelCatalogEntity>(entity =>
        {
            entity.HasKey(value => value.Id);
            entity.HasIndex(value => value.Value);
        });

        return JsonColdStoreModelDescriptor.Create(modelBuilder.FinalizeModel());
    }

    private static string TestDirectory(string name) =>
        Path.Combine(Path.GetTempPath(), "jsoncoldstore-tests", name);

    private static async Task<JsonObject> ReadDocumentAsync(string modelFile)
    {
        return JsonNode.Parse(await File.ReadAllTextAsync(modelFile))?.AsObject()
            ?? throw new InvalidDataException("The test model catalog could not be parsed.");
    }

    private static async Task WriteDocumentAsync(string modelFile, JsonObject document)
    {
        await File.WriteAllTextAsync(
            modelFile,
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonObject GetFirstEntity(JsonObject document)
    {
        return document["model"]?["entities"]?.AsArray()[0]?.AsObject()
            ?? throw new InvalidDataException("The test model catalog does not contain an entity.");
    }

    private static JsonObject GetFirstIndex(JsonObject document)
    {
        return GetFirstEntity(document)["indexes"]?.AsArray()[0]?.AsObject()
            ?? throw new InvalidDataException("The test model catalog does not contain an index.");
    }

    private sealed class ModelCatalogEntity
    {
        public Guid Id { get; set; }

        public string Value { get; set; } = string.Empty;
    }
}
