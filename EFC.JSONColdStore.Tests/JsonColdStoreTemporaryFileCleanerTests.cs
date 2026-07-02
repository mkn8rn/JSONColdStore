using EFC.JSONColdStore.Storage;

namespace EFC.JSONColdStore.Tests;

public sealed class JsonColdStoreTemporaryFileCleanerTests
{
    [Fact]
    public async Task DeleteOrphanedAtomicTempFilesSkipsReparsePointDirectories()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        var outsideTemp = Path.Combine(outside, "outside.tmp-escape");
        await File.WriteAllTextAsync(outsideTemp, "outside");

        if (!JsonColdStoreReparsePointTestHelper.TryCreateDirectoryLink(
                Path.Combine(root, "linked-outside"),
                outside))
        {
            return;
        }

        var deleted = JsonColdStoreTemporaryFileCleaner.DeleteOrphanedAtomicTempFiles(root);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(outsideTemp));
    }

    private static string NewTempDirectory()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "jsoncoldstore-cleaner-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
