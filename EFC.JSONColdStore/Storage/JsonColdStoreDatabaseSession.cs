namespace EFC.JSONColdStore.Storage;

internal sealed class JsonColdStoreDatabaseSession : IAsyncDisposable, IDisposable
{
    private readonly JsonColdStoreDatabaseLock? _writerLock;
    private bool _disposed;

    private JsonColdStoreDatabaseSession(
        JsonColdStoreOptions options,
        JsonColdStoreStoreMetadata metadata,
        JsonColdStoreRecoveryResult recoveryResult,
        JsonColdStoreStartupValidationResult startupValidationResult,
        JsonColdStoreRecordStore records,
        JsonColdStoreLegacyRecordStore legacyRecords,
        JsonColdStoreDatabaseLock? writerLock)
    {
        Options = options;
        Metadata = metadata;
        RecoveryResult = recoveryResult;
        StartupValidationResult = startupValidationResult;
        Records = records;
        LegacyRecords = legacyRecords;
        _writerLock = writerLock;
    }

    internal JsonColdStoreOptions Options { get; }

    internal JsonColdStoreStoreMetadata Metadata { get; }

    internal JsonColdStoreRecoveryResult RecoveryResult { get; }

    internal JsonColdStoreStartupValidationResult StartupValidationResult { get; }

    internal JsonColdStoreRecordStore Records { get; }

    internal JsonColdStoreLegacyRecordStore LegacyRecords { get; }

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

            var deletedTemporaryFiles = acquireWriterLock
                ? JsonColdStoreTemporaryFileCleaner.DeleteOrphanedAtomicTempFiles(options.DatabaseDirectory)
                : 0;
            var catalog = new JsonColdStoreCatalog(options);
            var metadata = acquireWriterLock
                ? await catalog.EnsureInitializedAsync(cancellationToken)
                : await catalog.LoadIfExistsOrCreateTransientAsync(cancellationToken);
            var records = new JsonColdStoreRecordStore(
                options,
                metadata.Policy.EncryptionEnabled,
                allowStorageMutations: acquireWriterLock,
                protectRecordPayloads: metadata.Policy.EncryptionEnabled);
            var legacyRecords = new JsonColdStoreLegacyRecordStore(options);
            var recoveryResult = acquireWriterLock
                ? await records.RecoverPendingManifestsAsync(cancellationToken)
                : new JsonColdStoreRecoveryResult(0, 0);
            recoveryResult = recoveryResult with
            {
                DeletedTemporaryFiles = deletedTemporaryFiles,
            };
            var startupValidationResult = options.StartupMode == JsonColdStoreStartupMode.FullHydration
                ? await records.VerifyAllRecordsAsync(cancellationToken)
                : new JsonColdStoreStartupValidationResult(0);

            return new JsonColdStoreDatabaseSession(
                options,
                metadata,
                recoveryResult,
                startupValidationResult,
                records,
                legacyRecords,
                writerLock);
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
