namespace EFC.JSONColdStore.Storage;

internal sealed class JsonColdStoreDatabaseSession : IAsyncDisposable, IDisposable
{
    private readonly JsonColdStoreDatabaseLock? _writerLock;
    private bool _disposed;

    private JsonColdStoreDatabaseSession(
        JsonColdStoreOptions options,
        JsonColdStoreStoreMetadata metadata,
        JsonColdStoreRecoveryResult recoveryResult,
        JsonColdStoreRecordStore records,
        JsonColdStoreDatabaseLock? writerLock)
    {
        Options = options;
        Metadata = metadata;
        RecoveryResult = recoveryResult;
        Records = records;
        _writerLock = writerLock;
    }

    internal JsonColdStoreOptions Options { get; }

    internal JsonColdStoreStoreMetadata Metadata { get; }

    internal JsonColdStoreRecoveryResult RecoveryResult { get; }

    internal JsonColdStoreRecordStore Records { get; }

    internal static async Task<JsonColdStoreDatabaseSession> OpenAsync(
        JsonColdStoreOptions options,
        bool acquireWriterLock = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        JsonColdStoreDatabaseLock? writerLock = null;
        try
        {
            if (acquireWriterLock)
                writerLock = await JsonColdStoreDatabaseLock.AcquireAsync(options, cancellationToken);

            var catalog = new JsonColdStoreCatalog(options);
            var metadata = await catalog.EnsureInitializedAsync(cancellationToken);
            var records = new JsonColdStoreRecordStore(
                options,
                metadata.Policy.EncryptionEnabled);
            var recoveryResult = acquireWriterLock
                ? await records.RecoverPendingManifestsAsync(cancellationToken)
                : new JsonColdStoreRecoveryResult(0, 0);

            return new JsonColdStoreDatabaseSession(options, metadata, recoveryResult, records, writerLock);
        }
        catch
        {
            writerLock?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _writerLock?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
