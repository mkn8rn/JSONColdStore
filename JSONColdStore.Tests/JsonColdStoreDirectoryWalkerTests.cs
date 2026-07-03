using JSONColdStore.Storage;

namespace JSONColdStore.Tests;

public sealed class JsonColdStoreDirectoryWalkerTests
{
    [Fact]
    public async Task EnumerateFilesSkipsInjectedReparsePointDirectories()
    {
        var root = NewTempDirectory();
        var normalDirectory = Path.Combine(root, "normal");
        var reparseDirectory = Path.Combine(root, "linked-outside");
        Directory.CreateDirectory(normalDirectory);
        Directory.CreateDirectory(reparseDirectory);
        var normalFile = Path.Combine(normalDirectory, "inside.jcs");
        var escapedFile = Path.Combine(reparseDirectory, "outside.jcs");
        await File.WriteAllTextAsync(normalFile, "inside");
        await File.WriteAllTextAsync(escapedFile, "outside");

        var files = JsonColdStoreDirectoryWalker.EnumerateFiles(
                root,
                "*.jcs",
                shouldSkipDirectory: null,
                isReparsePoint: path => string.Equals(
                    path,
                    reparseDirectory,
                    StringComparison.Ordinal))
            .ToArray();

        Assert.Equal([normalFile], files);
    }

    private static string NewTempDirectory()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "jsoncoldstore-walker-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
