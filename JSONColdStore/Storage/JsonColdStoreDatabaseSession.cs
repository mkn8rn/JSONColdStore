namespace JSONColdStore.Storage;

internal sealed class JsonColdStoreDatabaseSession : IAsyncDisposable, IDisposable
{
    private readonly JsonColdStoreDatabaseLock? _writerLock;
    private readonly bool _ownsWriterLock;
    private bool _disposed;

    private JsonColdStoreDatabaseSession(
        JsonColdStoreOptions options,
        JsonColdStoreStoreMetadata metadata,
        JsonColdStoreRecoveryResult recoveryResult,
        JsonColdStoreStartupValidationResult startupValidationResult,
        JsonColdStoreRecordStore records,
        JsonColdStoreLegacyRecordStore legacyRecords,
        JsonColdStoreDatabaseLock? writerLock,
        bool ownsWriterLock)
    {
        Options = options;
        Metadata = metadata;
        RecoveryResult = recoveryResult;
        StartupValidationResult = startupValidationResult;
        Records = records;
        LegacyRecords = legacyRecords;
        _writerLock = writerLock;
        _ownsWriterLock = ownsWriterLock;
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
        return await OpenCoreAsync(
            options,
            acquireWriterLock,
            existingWriterLock: null,
            ownsWriterLock: acquireWriterLock,
            cancellationToken);
    }

    internal static async Task<JsonColdStoreDatabaseSession> OpenWithExistingWriterLockAsync(
        JsonColdStoreOptions options,
        JsonColdStoreDatabaseLock writerLock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writerLock);

        return await OpenCoreAsync(
            options,
            acquireWriterLock: true,
            existingWriterLock: writerLock,
            ownsWriterLock: false,
            cancellationToken);
    }

    private static async Task<JsonColdStoreDatabaseSession> OpenCoreAsync(
        JsonColdStoreOptions options,
        bool acquireWriterLock,
        JsonColdStoreDatabaseLock? existingWriterLock,
        bool ownsWriterLock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        JsonColdStoreDatabaseLock? writerLock = existingWriterLock;
        try
        {
            if (acquireWriterLock && writerLock is null)
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
                writerLock,
                ownsWriterLock);
        }
        catch
        {
            if (ownsWriterLock)
                writerLock?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_ownsWriterLock)
            _writerLock?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
