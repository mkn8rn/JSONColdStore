namespace JSONColdStore.Storage;

internal static class JsonColdStoreFileReader
{
    internal static Task<byte[]> ReadAllBytesAsync(
        JsonColdStoreOptions options,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        JsonColdStoreFileGuard.ThrowIfReparsePoint(
            path,
            "The JSONColdStore file read target cannot be a reparse point.");

        return JsonColdStoreRetryPolicy.ExecuteAsync(
            options.ReadRetry,
            token => File.ReadAllBytesAsync(path, token),
            IsTransientReadException,
            cancellationToken);
    }

    private static bool IsTransientReadException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;
}
