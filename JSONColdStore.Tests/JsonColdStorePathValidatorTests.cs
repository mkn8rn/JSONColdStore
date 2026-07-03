using JSONColdStore;

namespace JSONColdStore.Tests;

public sealed class JsonColdStorePathValidatorTests
{
    [Fact]
    public void NormalizeDatabaseDirectoryRejectsEmptyPath()
    {
        Assert.Throws<ArgumentException>(
            () => JsonColdStorePathValidator.NormalizeDatabaseDirectory(" "));
    }

    [Fact]
    public void NormalizeDatabaseDirectoryRejectsFilesystemRoot()
    {
        var root = Path.GetPathRoot(Environment.CurrentDirectory)!;

        Assert.Throws<ArgumentException>(
            () => JsonColdStorePathValidator.NormalizeDatabaseDirectory(root));
    }

    [Fact]
    public void NormalizeDatabaseDirectoryReturnsFullPath()
    {
        var relative = Path.Combine(".", "data", "store");

        var normalized = JsonColdStorePathValidator.NormalizeDatabaseDirectory(relative);

        Assert.True(Path.IsPathFullyQualified(normalized));
        Assert.EndsWith(Path.Combine("data", "store"), normalized);
    }

    [Fact]
    public void GetSafeChildPathRejectsTraversalOutsideDatabaseDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "jsoncoldstore-tests", "root");

        Assert.Throws<UnauthorizedAccessException>(
            () => JsonColdStorePathValidator.GetSafeChildPath(root, "..", "escaped.json"));
    }

    [Fact]
    public void GetSafeChildPathRejectsTraversalInsideDatabaseDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "jsoncoldstore-tests", "root");

        Assert.Throws<UnauthorizedAccessException>(
            () => JsonColdStorePathValidator.GetSafeChildPath(root, "entities", "..", "_store.json"));
    }

    [Fact]
    public void GetSafeChildPathRejectsRootedChildSegment()
    {
        var root = Path.Combine(Path.GetTempPath(), "jsoncoldstore-tests", "root");
        var rootedSegment = Path.Combine(root, "other");

        Assert.Throws<UnauthorizedAccessException>(
            () => JsonColdStorePathValidator.GetSafeChildPath(root, rootedSegment));
    }

    [Fact]
    public void GetSafeChildPathRejectsMultiPartChildSegment()
    {
        var root = Path.Combine(Path.GetTempPath(), "jsoncoldstore-tests", "root");
        var childSegment = Path.Combine("Entity", "record.json");

        Assert.Throws<UnauthorizedAccessException>(
            () => JsonColdStorePathValidator.GetSafeChildPath(root, childSegment));
    }

    [Fact]
    public void GetSafeChildPathAllowsNestedFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "jsoncoldstore-tests", "root");

        var path = JsonColdStorePathValidator.GetSafeChildPath(root, "Entity", "record.json");

        Assert.StartsWith(JsonColdStorePathValidator.NormalizeDatabaseDirectory(root), path);
        Assert.EndsWith(Path.Combine("Entity", "record.json"), path);
    }
}
