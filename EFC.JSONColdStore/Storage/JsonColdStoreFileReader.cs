namespace EFC.JSONColdStore.Storage;

internal static class JsonColdStoreFileReader
{
    internal static Task<byte[]> ReadAllBytesAsync(
        JsonColdStoreOptions options,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return JsonColdStoreRetryPolicy.ExecuteAsync(
            options.ReadRetry,
            token => File.ReadAllBytesAsync(path, token),
            IsTransientReadException,
            cancellationToken);
    }

    private static bool IsTransientReadException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;
}
