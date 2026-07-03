using JSONColdStore.Storage;

namespace JSONColdStore.Tests;

public sealed class JsonColdStoreTemporaryFileCleanerTests
{
    [Fact]
    public async Task DeleteOrphanedAtomicTempFilesRejectsReparsePointRoot()
    {
        var parent = NewTempDirectory();
        var root = Path.Combine(parent, "linked-root");
        var outside = NewTempDirectory();
        var outsideTemp = Path.Combine(outside, "outside.tmp-escape");
        await File.WriteAllTextAsync(outsideTemp, "outside");

        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
            root,
            outside,
            nameof(DeleteOrphanedAtomicTempFilesRejectsReparsePointRoot));

        var exception = Assert.Throws<JsonColdStoreUnsafePathException>(
            () => JsonColdStoreTemporaryFileCleaner.DeleteOrphanedAtomicTempFiles(root));

        Assert.Contains("temporary-file cleanup root", exception.Message);
        Assert.DoesNotContain(root, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outside, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("outside", await File.ReadAllTextAsync(outsideTemp));
    }

    [Fact]
    public async Task DeleteOrphanedAtomicTempFilesSkipsReparsePointDirectories()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        var outsideTemp = Path.Combine(outside, "outside.tmp-escape");
        await File.WriteAllTextAsync(outsideTemp, "outside");

        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
                Path.Combine(root, "linked-outside"),
                outside,
                nameof(DeleteOrphanedAtomicTempFilesSkipsReparsePointDirectories));

        var deleted = JsonColdStoreTemporaryFileCleaner.DeleteOrphanedAtomicTempFiles(root);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(outsideTemp));
    }

    [Fact]
    public async Task DeleteOrphanedAtomicTempFilesDeletesNormalTemporaryFiles()
    {
        var root = NewTempDirectory();
        var nested = Path.Combine(root, "records");
        Directory.CreateDirectory(nested);
        var rootTemp = Path.Combine(root, "root.tmp-abc");
        var nestedTemp = Path.Combine(nested, "nested.tmp-def");
        var normalFile = Path.Combine(nested, "record.jcs");
        await File.WriteAllTextAsync(rootTemp, "root temp");
        await File.WriteAllTextAsync(nestedTemp, "nested temp");
        await File.WriteAllTextAsync(normalFile, "normal");

        var deleted = JsonColdStoreTemporaryFileCleaner.DeleteOrphanedAtomicTempFiles(root);

        Assert.Equal(2, deleted);
        Assert.False(File.Exists(rootTemp));
        Assert.False(File.Exists(nestedTemp));
        Assert.True(File.Exists(normalFile));
    }

    [Fact]
    public void DeleteOrphanedAtomicTempFilesReturnsZeroForMissingRoot()
    {
        var root = NewTempPath();

        var deleted = JsonColdStoreTemporaryFileCleaner.DeleteOrphanedAtomicTempFiles(root);

        Assert.Equal(0, deleted);
    }

    private static string NewTempPath() =>
        Path.Combine(
            Path.GetTempPath(),
            "jsoncoldstore-cleaner-tests",
            Guid.NewGuid().ToString("N"));

    private static string NewTempDirectory()
    {
        var root = NewTempPath();
        Directory.CreateDirectory(root);
        return root;
    }
}
