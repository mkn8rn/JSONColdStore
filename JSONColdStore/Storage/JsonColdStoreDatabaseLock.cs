using System.Diagnostics;
using System.Text.Json;

namespace JSONColdStore.Storage;

internal sealed class JsonColdStoreDatabaseLock : IDisposable
{
    internal const string LockDirectoryName = "_locks";
    internal const string WriterLockFileName = "write.lock";

    private static readonly JsonSerializerOptions LockJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly FileStream _stream;
    private readonly string _lockPath;
    private bool _disposed;

    private JsonColdStoreDatabaseLock(FileStream stream, string lockPath)
    {
        _stream = stream;
        _lockPath = lockPath;
    }

    internal static async Task<JsonColdStoreDatabaseLock> AcquireAsync(
        JsonColdStoreOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        JsonColdStoreDirectoryGuard.CreateDirectory(
            options.DatabaseDirectory,
            LockDirectoryName);

        var lockPath = JsonColdStorePathValidator.GetSafeChildPath(
            options.DatabaseDirectory,
            LockDirectoryName,
            WriterLockFileName);
        JsonColdStoreFileGuard.ThrowIfReparsePoint(
            lockPath,
            "The JSONColdStore writer lock file cannot be a reparse point.");

        FileStream stream;
        try
        {
            stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous);
        }
        catch (IOException ex)
        {
            throw CreateLockFailure(lockPath, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw CreateLockFailure(lockPath, ex);
        }

        var lockInfo = JsonColdStoreLockInfo.Create();
        var lockBytes = JsonSerializer.SerializeToUtf8Bytes(lockInfo, LockJsonOptions);
        try
        {
            stream.SetLength(0);
            await stream.WriteAsync(lockBytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }

        return new JsonColdStoreDatabaseLock(stream, lockPath);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _stream.Dispose();
        TryDeleteLockFile(_lockPath);
    }

    private static InvalidOperationException CreateLockFailure(string lockPath, Exception innerException)
    {
        var owner = TryReadLockOwner(lockPath);
        var message = owner is null
            ? "The JSONColdStore database writer lock is already held."
            : $"The JSONColdStore database writer lock is already held by process {owner.ProcessId}.";

        return new InvalidOperationException(message, innerException);
    }

    private static JsonColdStoreLockInfo? TryReadLockOwner(string lockPath)
    {
        try
        {
            if (!File.Exists(lockPath))
                return null;
            if (JsonColdStoreDirectoryWalker.IsReparsePoint(lockPath))
                return null;

            using var stream = new FileStream(
                lockPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var json = memory.ToArray();
            return JsonSerializer.Deserialize<JsonColdStoreLockInfo>(json, LockJsonOptions);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void TryDeleteLockFile(string lockPath)
    {
        try
        {
            if (File.Exists(lockPath))
                File.Delete(lockPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed record JsonColdStoreLockInfo(
    int ProcessId,
    string MachineName,
    DateTimeOffset AcquiredAt,
    string ProviderVersion)
{
    internal static JsonColdStoreLockInfo Create() => new(
        Environment.ProcessId,
        Environment.MachineName,
        DateTimeOffset.UtcNow,
        JsonColdStoreProviderInfo.Version);
}
