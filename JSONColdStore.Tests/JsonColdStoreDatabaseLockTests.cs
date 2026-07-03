using System.Text.Json;
using JSONColdStore;
using JSONColdStore.Storage;

namespace JSONColdStore.Tests;

public sealed class JsonColdStoreDatabaseLockTests
{
    [Fact]
    public async Task AcquireAsyncWritesLockOwnerFile()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();

        using var databaseLock = await JsonColdStoreDatabaseLock.AcquireAsync(options);

        var lockPath = LockPath(root);
        Assert.True(File.Exists(lockPath));

        var lockInfo = await ReadLockInfoAsync(lockPath);

        Assert.NotNull(lockInfo);
        Assert.Equal(Environment.ProcessId, lockInfo.ProcessId);
        Assert.Equal(Environment.MachineName, lockInfo.MachineName);
        Assert.False(string.IsNullOrWhiteSpace(lockInfo.ProviderVersion));
    }

    [Fact]
    public async Task AcquireAsyncRejectsSecondWriter()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();

        using var firstLock = await JsonColdStoreDatabaseLock.AcquireAsync(options);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => JsonColdStoreDatabaseLock.AcquireAsync(options));

        Assert.Contains("writer lock", exception.Message);
        Assert.Contains(Environment.ProcessId.ToString(), exception.Message);
    }

    [Fact]
    public async Task AcquireAsyncRejectsReparsePointLockDirectory()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
                Path.Combine(root, JsonColdStoreDatabaseLock.LockDirectoryName),
                outside,
                nameof(AcquireAsyncRejectsReparsePointLockDirectory));

        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();

        await Assert.ThrowsAsync<JsonColdStoreUnsafePathException>(
            () => JsonColdStoreDatabaseLock.AcquireAsync(options));

        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task AcquireAsyncRejectsReparsePointDatabaseRoot()
    {
        var parent = NewTempDirectory();
        var outside = NewTempDirectory();
        var link = Path.Combine(parent, "linked-store");
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
            link,
            outside,
            nameof(AcquireAsyncRejectsReparsePointDatabaseRoot));
        var options = new JsonColdStoreOptionsBuilder(link)
            .UseFsyncOnWrite(false)
            .Build();

        var exception = await Assert.ThrowsAsync<JsonColdStoreUnsafePathException>(
            () => JsonColdStoreDatabaseLock.AcquireAsync(options));

        Assert.Contains("database directory", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(link, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task AcquireAsyncRejectsReparsePointLockFile()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, JsonColdStoreDatabaseLock.LockDirectoryName));
        var outsideFile = Path.Combine(outside, JsonColdStoreDatabaseLock.WriterLockFileName);
        await File.WriteAllTextAsync(outsideFile, "outside-lock");
        JsonColdStoreReparsePointTestHelper.CreateRequiredFileLink(
            LockPath(root),
            outsideFile,
            nameof(AcquireAsyncRejectsReparsePointLockFile));

        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();

        await Assert.ThrowsAsync<JsonColdStoreUnsafePathException>(
            () => JsonColdStoreDatabaseLock.AcquireAsync(options));

        Assert.Equal("outside-lock", await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public async Task DisposeReleasesLockFile()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();

        var databaseLock = await JsonColdStoreDatabaseLock.AcquireAsync(options);
        databaseLock.Dispose();

        Assert.False(File.Exists(LockPath(root)));

        using var reacquired = await JsonColdStoreDatabaseLock.AcquireAsync(options);
        Assert.True(File.Exists(LockPath(root)));
    }

    [Fact]
    public async Task HeldLockAllowsRecursiveStoreDeletion()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .Build();

        using var databaseLock = await JsonColdStoreDatabaseLock.AcquireAsync(options);
        await File.WriteAllTextAsync(Path.Combine(root, "payload.txt"), "payload");

        Directory.Delete(root, recursive: true);

        Assert.False(Directory.Exists(root));
    }

    private static string LockPath(string root) =>
        Path.Combine(
            root,
            JsonColdStoreDatabaseLock.LockDirectoryName,
            JsonColdStoreDatabaseLock.WriterLockFileName);

    private static async Task<JsonColdStoreLockInfo?> ReadLockInfoAsync(string lockPath)
    {
        await using var stream = new FileStream(
            lockPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            FileOptions.Asynchronous);

        return await JsonSerializer.DeserializeAsync<JsonColdStoreLockInfo>(
            stream,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private static string NewTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "jsoncoldstore-lock-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
