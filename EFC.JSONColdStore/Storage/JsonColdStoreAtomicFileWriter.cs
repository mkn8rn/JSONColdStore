namespace EFC.JSONColdStore.Storage;

internal static class JsonColdStoreAtomicFileWriter
{
    internal static async Task WriteAsync(
        string databaseDirectory,
        IEnumerable<string> pathSegments,
        ReadOnlyMemory<byte> data,
        bool fsync,
        CancellationToken cancellationToken = default)
    {
        var targetPath = ResolvePath(databaseDirectory, pathSegments);
        var directory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("The target path has no directory.");
        Directory.CreateDirectory(directory);

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
