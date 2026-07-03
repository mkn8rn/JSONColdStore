namespace JSONColdStore.Storage;

internal static class JsonColdStoreAtomicFileWriter
{
    internal static Task WriteAsync(
        JsonColdStoreOptions options,
        IEnumerable<string> pathSegments,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var segments = MaterializePathSegments(pathSegments);
        var targetPath = ResolvePath(options.DatabaseDirectory, segments);
        return JsonColdStoreRetryPolicy.ExecuteAsync(
            options.FlushRetry,
            token => WriteResolvedPathAsync(
                options.DatabaseDirectory,
                segments,
                targetPath,
                data,
                options.FsyncOnWrite,
                token),
            IsTransientWriteException,
            cancellationToken);
    }

    internal static async Task WriteAsync(
        string databaseDirectory,
        IEnumerable<string> pathSegments,
        ReadOnlyMemory<byte> data,
        bool fsync,
        CancellationToken cancellationToken = default)
    {
        var segments = MaterializePathSegments(pathSegments);
        var targetPath = ResolvePath(databaseDirectory, segments);
        await WriteResolvedPathAsync(
            databaseDirectory,
            segments,
            targetPath,
            data,
            fsync,
            cancellationToken);
    }

    private static async Task WriteResolvedPathAsync(
        string databaseDirectory,
        IReadOnlyList<string> pathSegments,
        string targetPath,
        ReadOnlyMemory<byte> data,
        bool fsync,
        CancellationToken cancellationToken)
    {
        CreateSafeTargetDirectory(databaseDirectory, pathSegments);
        JsonColdStoreFileGuard.ThrowIfReparsePoint(
            targetPath,
            "The target file cannot be a reparse point.");

        var tempPath = targetPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous))
            {
                await stream.WriteAsync(data, cancellationToken);
                if (fsync)
                    stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
    }

    internal static async Task<byte[]> ReadAsync(
        string databaseDirectory,
        IEnumerable<string> pathSegments,
        CancellationToken cancellationToken = default)
    {
        var targetPath = ResolvePath(databaseDirectory, pathSegments);
        JsonColdStoreFileGuard.ThrowIfReparsePoint(
            targetPath,
            "The JSONColdStore file read target cannot be a reparse point.");
        return await File.ReadAllBytesAsync(targetPath, cancellationToken);
    }

    internal static async Task<byte[]> ReadAsync(
        JsonColdStoreOptions options,
        IEnumerable<string> pathSegments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var targetPath = ResolvePath(options.DatabaseDirectory, pathSegments);
        return await JsonColdStoreFileReader.ReadAllBytesAsync(options, targetPath, cancellationToken);
    }

    private static string ResolvePath(string databaseDirectory, IEnumerable<string> pathSegments)
    {
        ArgumentNullException.ThrowIfNull(pathSegments);
        return JsonColdStorePathValidator.GetSafeChildPath(databaseDirectory, [.. pathSegments]);
    }

    private static string[] MaterializePathSegments(IEnumerable<string> pathSegments)
    {
        ArgumentNullException.ThrowIfNull(pathSegments);

        var segments = pathSegments.ToArray();
        if (segments.Length == 0)
            throw new ArgumentException("At least one target path segment is required.", nameof(pathSegments));

        return segments;
    }

    private static void CreateSafeTargetDirectory(
        string databaseDirectory,
        IReadOnlyList<string> pathSegments)
    {
        if (pathSegments.Count == 1)
        {
            JsonColdStoreDirectoryGuard.CreateDirectory(databaseDirectory);
            return;
        }

        JsonColdStoreDirectoryGuard.CreateDirectory(
            databaseDirectory,
            [.. pathSegments.Take(pathSegments.Count - 1)]);
    }

    internal static bool IsTransientWriteException(Exception exception) =>
        exception is IOException
        || (exception is UnauthorizedAccessException and not JsonColdStoreUnsafePathException);

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
