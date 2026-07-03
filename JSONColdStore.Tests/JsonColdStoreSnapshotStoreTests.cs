using JSONColdStore;
using JSONColdStore.Storage;

namespace JSONColdStore.Tests;

public sealed class JsonColdStoreSnapshotStoreTests
{
    [Fact]
    public async Task CreateSnapshotAsyncRejectsReparsePointSnapshotRoot()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        await File.WriteAllTextAsync(Path.Combine(root, "_store.json"), "{}");

        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
                Path.Combine(root, "_snapshots"),
                outside,
                nameof(CreateSnapshotAsyncRejectsReparsePointSnapshotRoot));

        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();
        var snapshots = new JsonColdStoreSnapshotStore(options);

        await Assert.ThrowsAsync<JsonColdStoreUnsafePathException>(
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

        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
                Path.Combine(root, "linked-outside"),
                outside,
                nameof(CreateSnapshotAsyncSkipsReparsePointDirectories));

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

    [Fact]
    public async Task CreateSnapshotAsyncPrunesOnlyNonReparseSnapshotDirectories()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        await File.WriteAllTextAsync(Path.Combine(root, "_store.json"), "{}");
        var snapshotRoot = Path.Combine(root, "_snapshots");
        Directory.CreateDirectory(snapshotRoot);
        var oldSnapshot = Path.Combine(snapshotRoot, "000000000000000000000-old");
        Directory.CreateDirectory(oldSnapshot);
        await File.WriteAllTextAsync(Path.Combine(oldSnapshot, "old.txt"), "old");
        var linkedSnapshot = Path.Combine(snapshotRoot, "000000000000000000001-linked");
        var outsideFile = Path.Combine(outside, "outside.txt");
        await File.WriteAllTextAsync(outsideFile, "outside");
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
                linkedSnapshot,
                outside,
                nameof(CreateSnapshotAsyncPrunesOnlyNonReparseSnapshotDirectories));

        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .UseSnapshots(enabled: true, TimeSpan.FromHours(1), retentionCount: 1)
            .Build();
        var snapshots = new JsonColdStoreSnapshotStore(options);

        var result = await snapshots.CreateSnapshotAsync();

        Assert.Equal(1, result.DeletedSnapshots);
        Assert.False(Directory.Exists(oldSnapshot));
        Assert.True(Directory.Exists(linkedSnapshot));
        Assert.True(File.Exists(outsideFile));
        Assert.Single(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task CreateSnapshotAsyncDoesNotPruneSnapshotContainingReparsePointChild()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        await File.WriteAllTextAsync(Path.Combine(root, "_store.json"), "{}");
        var snapshotRoot = Path.Combine(root, "_snapshots");
        Directory.CreateDirectory(snapshotRoot);
        var oldSnapshot = Path.Combine(snapshotRoot, "000000000000000000000-old");
        Directory.CreateDirectory(oldSnapshot);
        await File.WriteAllTextAsync(Path.Combine(oldSnapshot, "old.txt"), "old");
        var outsideFile = Path.Combine(outside, "outside.txt");
        await File.WriteAllTextAsync(outsideFile, "outside");
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
            Path.Combine(oldSnapshot, "linked-child"),
            outside,
            nameof(CreateSnapshotAsyncDoesNotPruneSnapshotContainingReparsePointChild));

        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .UseSnapshots(enabled: true, TimeSpan.FromHours(1), retentionCount: 1)
            .Build();
        var snapshots = new JsonColdStoreSnapshotStore(options);

        var result = await snapshots.CreateSnapshotAsync();

        Assert.Equal(0, result.DeletedSnapshots);
        Assert.True(Directory.Exists(oldSnapshot));
        Assert.True(Directory.Exists(Path.Combine(oldSnapshot, "linked-child")));
        Assert.True(File.Exists(outsideFile));
        Assert.Single(Directory.EnumerateFileSystemEntries(outside));
    }

    private static string NewTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "jsoncoldstore-snapshot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
