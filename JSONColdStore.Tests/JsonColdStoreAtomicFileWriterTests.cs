using JSONColdStore.Storage;
using System.Diagnostics;

namespace JSONColdStore.Tests;

public sealed class JsonColdStoreAtomicFileWriterTests
{
    [Fact]
    public async Task WriteAsyncPublishesReadableBytes()
    {
        var root = NewTempDirectory();
        var payload = "stored bytes"u8.ToArray();

        await JsonColdStoreAtomicFileWriter.WriteAsync(
            root,
            ["Entity", "record.jcs"],
            payload,
            fsync: false);

        var read = await JsonColdStoreAtomicFileWriter.ReadAsync(
            root,
            ["Entity", "record.jcs"]);

        Assert.Equal(payload, read);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "Entity"), "*.tmp-*"));
    }

    [Fact]
    public async Task WriteAsyncWithOptionsPublishesReadableBytes()
    {
        var root = NewTempDirectory();
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .UseFlushRetry(maxRetries: 1, baseDelay: TimeSpan.Zero)
            .Build();
        var payload = "stored option bytes"u8.ToArray();

        await JsonColdStoreAtomicFileWriter.WriteAsync(
            options,
            ["Entity", "record.jcs"],
            payload);

        var read = await JsonColdStoreAtomicFileWriter.ReadAsync(
            options,
            ["Entity", "record.jcs"]);

        Assert.Equal(payload, read);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "Entity"), "*.tmp-*"));
    }

    [Fact]
    public async Task ReadAsyncRejectsReparsePointFile()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        var entityDirectory = Path.Combine(root, "Entity");
        Directory.CreateDirectory(entityDirectory);
        var outsideFile = Path.Combine(outside, "outside.jcs");
        await File.WriteAllTextAsync(outsideFile, "outside");
        JsonColdStoreReparsePointTestHelper.CreateRequiredFileLink(
            Path.Combine(entityDirectory, "record.jcs"),
            outsideFile,
            nameof(ReadAsyncRejectsReparsePointFile));

        var exception = await Assert.ThrowsAsync<JsonColdStoreUnsafePathException>(
            () => JsonColdStoreAtomicFileWriter.ReadAsync(root, ["Entity", "record.jcs"]));

        Assert.Contains("reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("outside", await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public async Task WriteAsyncRejectsTraversalOutsideDatabaseDirectory()
    {
        var root = NewTempDirectory();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => JsonColdStoreAtomicFileWriter.WriteAsync(
                root,
                ["..", "escaped.jcs"],
                "nope"u8.ToArray(),
                fsync: false));
    }

    [Fact]
    public async Task WriteAsyncRejectsEmptyPathSegments()
    {
        var root = NewTempDirectory();

        await Assert.ThrowsAsync<ArgumentException>(
            () => JsonColdStoreAtomicFileWriter.WriteAsync(
                root,
                [],
                "nope"u8.ToArray(),
                fsync: false));
    }

    [Fact]
    public async Task WriteAsyncWithOptionsRejectsReparsePointChildDirectory()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        JsonColdStoreReparsePointTestHelper.CreateRequiredDirectoryLink(
                Path.Combine(root, "Entity"),
                outside,
                nameof(WriteAsyncWithOptionsRejectsReparsePointChildDirectory));

        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .UseFlushRetry(maxRetries: 5, baseDelay: TimeSpan.FromSeconds(5))
            .Build();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var stopwatch = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(
            () => JsonColdStoreAtomicFileWriter.WriteAsync(
                options,
                ["Entity", "record.jcs"],
                "nope"u8.ToArray(),
                cancellation.Token));

        stopwatch.Stop();
        Assert.IsType<JsonColdStoreUnsafePathException>(exception);
        Assert.Contains("reparse-point", exception.Message);
        Assert.False(cancellation.IsCancellationRequested);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task WriteAsyncWithOptionsRejectsReparsePointTargetFileWithoutRetrying()
    {
        var root = NewTempDirectory();
        var outside = NewTempDirectory();
        var entityDirectory = Path.Combine(root, "Entity");
        Directory.CreateDirectory(entityDirectory);
        var outsideFile = Path.Combine(outside, "outside.jcs");
        await File.WriteAllTextAsync(outsideFile, "outside-write");
        JsonColdStoreReparsePointTestHelper.CreateRequiredFileLink(
            Path.Combine(entityDirectory, "record.jcs"),
            outsideFile,
            nameof(WriteAsyncWithOptionsRejectsReparsePointTargetFileWithoutRetrying));
        var options = new JsonColdStoreOptionsBuilder(root)
            .UseFsyncOnWrite(false)
            .UseFlushRetry(maxRetries: 5, baseDelay: TimeSpan.FromSeconds(5))
            .Build();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var stopwatch = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<JsonColdStoreUnsafePathException>(
            () => JsonColdStoreAtomicFileWriter.WriteAsync(
                options,
                ["Entity", "record.jcs"],
                "nope"u8.ToArray(),
                cancellation.Token));

        stopwatch.Stop();
        Assert.Contains("reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(cancellation.IsCancellationRequested);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal("outside-write", await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public void IsTransientWriteExceptionDoesNotRetryUnsafePathFailures()
    {
        Assert.False(JsonColdStoreAtomicFileWriter.IsTransientWriteException(
            new JsonColdStoreUnsafePathException("unsafe")));
        Assert.True(JsonColdStoreAtomicFileWriter.IsTransientWriteException(
            new IOException("transient")));
        Assert.True(JsonColdStoreAtomicFileWriter.IsTransientWriteException(
            new UnauthorizedAccessException("transient")));
    }

    private static string NewTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "jsoncoldstore-writer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
