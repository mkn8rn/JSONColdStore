using EFC.JSONColdStore;
using EFC.JSONColdStore.Storage;

namespace EFC.JSONColdStore.Tests;

public sealed class JsonColdStoreSnapshotStoreTests
{
    [Fact]
    public async Task CreateSnapshotAsyncRejectsReparsePointSnapshotRoot()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        await File.WriteAllTextAsync(Path.Combine(root, "_store.json"), "{}");

        if (!JsonColdStoreReparsePointTestHelper.TryCreateDirectoryLink(
                Path.Combine(root, "_snapshots"),
                outside))
        {
            return;
        }

        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var snapshots = new JsonColdStoreSnapshotStore(options);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => snapshots.CreateSnapshotAsync());

        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task CreateSnapshotAsyncSkipsReparsePointDirectories()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        await File.WriteAllTextAsync(Path.Combine(root, "_store.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(outside, "outside.txt"), "outside");

        if (!JsonColdStoreReparsePointTestHelper.TryCreateDirectoryLink(
                Path.Combine(root, "linked-outside"),
                outside))
        {
            return;
        }

        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var snapshots = new JsonColdStoreSnapshotStore(options);

        var result = await snapshots.CreateSnapshotAsync();

        Assert.False(Directory.Exists(Path.Combine(result.SnapshotDirectory, "linked-outside")));
        Assert.False(File.Exists(Path.Combine(
            result.SnapshotDirectory,
            "linked-outside",
            "outside.txt")));
    }

    [Fact]
    public async Task CreateSnapshotAsyncDeletesIncompleteSnapshotWhenCanceled()
    {
        var root = NewTempDirectory();
        await File.WriteAllTextAsync(Path.Combine(root, "_store.json"), "{}");
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var snapshots = new JsonColdStoreSnapshotStore(options);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => snapshots.CreateSnapshotAsync(cancellation.Token));

        var snapshotRoot = Path.Combine(root, "_snapshots");
        Assert.True(Directory.Exists(snapshotRoot));
        Assert.Empty(Directory.EnumerateDirectories(snapshotRoot));
    }

    private static string NewTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "jsoncoldstore-snapshot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
