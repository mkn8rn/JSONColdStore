namespace EFC.JSONColdStore;

/// <summary>
/// Fluent builder for JSONColdStore provider settings.
/// </summary>
public sealed class JsonColdStoreOptionsBuilder
{
    private readonly string _databaseDirectory;
    private JsonColdStoreCompression _compression = JsonColdStoreCompression.Auto;
    private JsonColdStoreEncryptionOptions? _encryption;
    private JsonColdStoreStartupMode _startupMode = JsonColdStoreStartupMode.MetadataOnly;
    private JsonColdStoreDurability _durability = JsonColdStoreDurability.ManifestedAtomicWrites;
    private bool _fsyncOnWrite = true;
    private JsonColdStoreAsyncFlushOptions _asyncFlush = new();
    private JsonColdStoreRetryOptions _flushRetry = JsonColdStoreRetryOptions.DefaultFlushRetry;
    private JsonColdStoreRetryOptions _transactionReplay = JsonColdStoreRetryOptions.DefaultTransactionReplay;
    private JsonColdStoreRetryOptions _readRetry = JsonColdStoreRetryOptions.DefaultReadRetry;
    private JsonColdStoreIntegrityOptions _integrity = new();
    private JsonColdStoreQuarantineOptions _quarantine = new();
    private JsonColdStoreIndexMaintenanceOptions _indexMaintenance = new();
    private JsonColdStoreEventLogOptions _eventLog = new();
    private JsonColdStoreSnapshotOptions _snapshots = new();
    private JsonColdStoreScanPolicy _fullScanPolicy = JsonColdStoreScanPolicy.FailUnlessExplicit;

    /// <summary>Creates a builder for a database rooted at <paramref name="databaseDirectory"/>.</summary>
    public JsonColdStoreOptionsBuilder(string databaseDirectory)
    {
        _databaseDirectory = JsonColdStorePathValidator.NormalizeDatabaseDirectory(databaseDirectory);
    }

    /// <summary>Sets the default compression policy.</summary>
    public JsonColdStoreOptionsBuilder UseCompression(JsonColdStoreCompression compression)
    {
        ThrowIfUndefined(compression);
        _compression = compression;
        return this;
    }

    /// <summary>Enables encrypted-at-rest payloads with a host-forwarded key.</summary>
    public JsonColdStoreOptionsBuilder UseEncryptionKey(JsonColdStoreEncryptionKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _encryption = new JsonColdStoreEncryptionOptions { Key = key };
        return this;
    }

    /// <summary>Enables encrypted-at-rest payloads with explicit encryption options.</summary>
    public JsonColdStoreOptionsBuilder UseEncryption(JsonColdStoreEncryptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Key);
        _encryption = options;
        return this;
    }

    /// <summary>Sets the default startup hydration mode.</summary>
    public JsonColdStoreOptionsBuilder UseStartupMode(JsonColdStoreStartupMode startupMode)
    {
        ThrowIfUndefined(startupMode);
        _startupMode = startupMode;
        return this;
    }

    /// <summary>Sets the write durability policy.</summary>
    public JsonColdStoreOptionsBuilder UseDurability(JsonColdStoreDurability durability)
    {
        ThrowIfUndefined(durability);
        _durability = durability;
        return this;
    }

    /// <summary>Controls whether staged files are flushed before publication.</summary>
    public JsonColdStoreOptionsBuilder UseFsyncOnWrite(bool enabled)
    {
        _fsyncOnWrite = enabled;
        return this;
    }

    /// <summary>Rejects background flush because saves publish synchronously in this provider version.</summary>
    public JsonColdStoreOptionsBuilder UseAsyncFlush(int queueCapacity = 256)
    {
        throw new NotSupportedException(
            "Background async flush is not implemented in this JSONColdStore provider version.");
    }

    /// <summary>Disables background flush so saves publish synchronously.</summary>
    public JsonColdStoreOptionsBuilder DisableAsyncFlush()
    {
        _asyncFlush = _asyncFlush with { Enabled = false };
        return this;
    }

    /// <summary>Sets retry behavior for transient atomic write failures.</summary>
    public JsonColdStoreOptionsBuilder UseFlushRetry(int maxRetries, TimeSpan baseDelay)
    {
        _flushRetry = CreateRetryOptions(maxRetries, baseDelay, nameof(maxRetries));
        return this;
    }

    /// <summary>Sets the maximum retry count for transaction replay.</summary>
    public JsonColdStoreOptionsBuilder UseTransactionReplay(int maxRetries)
    {
        _transactionReplay = CreateRetryOptions(maxRetries, TimeSpan.Zero, nameof(maxRetries));
        return this;
    }

    /// <summary>Sets retry behavior for transient storage read failures.</summary>
    public JsonColdStoreOptionsBuilder UseReadRetry(int maxRetries, TimeSpan baseDelay)
    {
        _readRetry = CreateRetryOptions(maxRetries, baseDelay, nameof(maxRetries));
        return this;
    }

    /// <summary>Enables checksum manifests and verification settings.</summary>
    public JsonColdStoreOptionsBuilder UseChecksums(bool verifyOnStartup = true, bool verifyOnRead = false)
    {
        _integrity = new JsonColdStoreIntegrityOptions
        {
            EnableChecksums = true,
            Key = _integrity.Key,
            VerifyOnStartup = verifyOnStartup,
            VerifyOnRead = verifyOnRead,
        };
        return this;
    }

    /// <summary>Enables keyed HMAC-SHA256 payload integrity with a host-forwarded key.</summary>
    public JsonColdStoreOptionsBuilder UseIntegrityKey(JsonColdStoreIntegrityKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _integrity = _integrity with
        {
            EnableChecksums = true,
            Key = key,
        };
        return this;
    }

    /// <summary>Disables checksum manifest maintenance and verification.</summary>
    public JsonColdStoreOptionsBuilder DisableChecksums()
    {
        _integrity = _integrity with
        {
            EnableChecksums = false,
            Key = null,
            VerifyOnStartup = false,
            VerifyOnRead = false,
        };
        return this;
    }

    /// <summary>Sets how long quarantined corrupt files are retained.</summary>
    public JsonColdStoreOptionsBuilder UseQuarantine(TimeSpan retention)
    {
        if (retention < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retention), "Retention cannot be negative.");

        _quarantine = new JsonColdStoreQuarantineOptions { Retention = retention };
        return this;
    }

    /// <summary>Sets periodic cold-index maintenance behavior.</summary>
    public JsonColdStoreOptionsBuilder UseIndexMaintenance(
        TimeSpan rescanInterval,
        bool rebuildOnCatalogMismatch = true)
    {
        if (rescanInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(rescanInterval), "Rescan interval cannot be negative.");

        _indexMaintenance = new JsonColdStoreIndexMaintenanceOptions
        {
            RescanInterval = rescanInterval,
            RebuildOnCatalogMismatch = rebuildOnCatalogMismatch,
        };
        return this;
    }

    /// <summary>Sets provider event-log behavior.</summary>
    public JsonColdStoreOptionsBuilder UseEventLog(bool enabled, TimeSpan retention)
    {
        if (retention < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retention), "Retention cannot be negative.");

        _eventLog = new JsonColdStoreEventLogOptions
        {
            Enabled = enabled,
            Retention = retention,
        };
        return this;
    }

    /// <summary>Sets disaster-recovery snapshot behavior.</summary>
    public JsonColdStoreOptionsBuilder UseSnapshots(bool enabled, TimeSpan interval, int retentionCount)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "Snapshot interval must be positive.");
        if (retentionCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(retentionCount), "Snapshot retention count must be positive.");

        _snapshots = new JsonColdStoreSnapshotOptions
        {
            Enabled = enabled,
            Interval = interval,
            RetentionCount = retentionCount,
        };
        return this;
    }

    /// <summary>Sets behavior for queries that require full scans.</summary>
    public JsonColdStoreOptionsBuilder UseFullScanPolicy(JsonColdStoreScanPolicy scanPolicy)
    {
        ThrowIfUndefined(scanPolicy);
        _fullScanPolicy = scanPolicy;
        return this;
    }

    /// <summary>Builds an immutable options instance.</summary>
    public JsonColdStoreOptions Build() => new()
    {
        DatabaseDirectory = _databaseDirectory,
        Compression = _compression,
        Encryption = _encryption,
        StartupMode = _startupMode,
        Durability = _durability,
        FsyncOnWrite = _fsyncOnWrite,
        AsyncFlush = _asyncFlush,
        FlushRetry = _flushRetry,
        TransactionReplay = _transactionReplay,
        ReadRetry = _readRetry,
        Integrity = _integrity,
        Quarantine = _quarantine,
        IndexMaintenance = _indexMaintenance,
        EventLog = _eventLog,
        Snapshots = _snapshots,
        FullScanPolicy = _fullScanPolicy,
    };

    private static JsonColdStoreRetryOptions CreateRetryOptions(
        int maxRetries,
        TimeSpan baseDelay,
        string parameterName)
    {
        if (maxRetries < 0)
            throw new ArgumentOutOfRangeException(parameterName, "Retry count cannot be negative.");
        if (baseDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(baseDelay), "Retry delay cannot be negative.");

        return new JsonColdStoreRetryOptions
        {
            MaxRetries = maxRetries,
            BaseDelay = baseDelay,
        };
    }

    private static void ThrowIfUndefined<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(nameof(value), value, "The enum value is not defined.");
    }
}
