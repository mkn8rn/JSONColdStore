namespace EFC.JSONColdStore;

/// <summary>
/// Immutable provider settings produced by <see cref="JsonColdStoreOptionsBuilder"/>.
/// </summary>
public sealed record JsonColdStoreOptions
{
    /// <summary>Normalized root directory for the JSONColdStore database.</summary>
    public required string DatabaseDirectory { get; init; }

    /// <summary>Default compression policy for eligible payloads.</summary>
    public JsonColdStoreCompression Compression { get; init; } = JsonColdStoreCompression.Auto;

    /// <summary>Encrypted-at-rest configuration, or <see langword="null"/> for no key.</summary>
    public JsonColdStoreEncryptionOptions? Encryption { get; init; }

    /// <summary>Default startup behavior for entity payload hydration.</summary>
    public JsonColdStoreStartupMode StartupMode { get; init; } = JsonColdStoreStartupMode.MetadataOnly;

    /// <summary>Write durability publication mode.</summary>
    public JsonColdStoreDurability Durability { get; init; } = JsonColdStoreDurability.ManifestedAtomicWrites;

    /// <summary>Whether writes should flush file data before publication.</summary>
    public bool FsyncOnWrite { get; init; } = true;

    /// <summary>Background flush and queue back-pressure settings.</summary>
    public JsonColdStoreAsyncFlushOptions AsyncFlush { get; init; } = new();

    /// <summary>Retry behavior for background flush attempts.</summary>
    public JsonColdStoreRetryOptions FlushRetry { get; init; } = JsonColdStoreRetryOptions.DefaultFlushRetry;

    /// <summary>Retry behavior for replaying pending transaction manifests.</summary>
    public JsonColdStoreRetryOptions TransactionReplay { get; init; } = JsonColdStoreRetryOptions.DefaultTransactionReplay;

    /// <summary>Retry behavior for transient storage read failures.</summary>
    public JsonColdStoreRetryOptions ReadRetry { get; init; } = JsonColdStoreRetryOptions.DefaultReadRetry;

    /// <summary>Checksum and verification settings.</summary>
    public JsonColdStoreIntegrityOptions Integrity { get; init; } = new();

    /// <summary>Corrupt-file quarantine behavior.</summary>
    public JsonColdStoreQuarantineOptions Quarantine { get; init; } = new();

    /// <summary>Cold index rebuild and maintenance settings.</summary>
    public JsonColdStoreIndexMaintenanceOptions IndexMaintenance { get; init; } = new();

    /// <summary>Append-only provider event log settings.</summary>
    public JsonColdStoreEventLogOptions EventLog { get; init; } = new();

    /// <summary>Full-store snapshot settings.</summary>
    public JsonColdStoreSnapshotOptions Snapshots { get; init; } = new();

    /// <summary>Behavior for query shapes that require full scans.</summary>
    public JsonColdStoreScanPolicy FullScanPolicy { get; init; } = JsonColdStoreScanPolicy.FailUnlessExplicit;
}

/// <summary>Encrypted-at-rest provider configuration.</summary>
public sealed record JsonColdStoreEncryptionOptions
{
    /// <summary>Host-forwarded encryption key used for protected payloads.</summary>
    public required JsonColdStoreEncryptionKey Key { get; init; }

    /// <summary>Optional key identifier recorded in encrypted envelopes.</summary>
    public string? KeyId { get; init; }

    /// <summary>Whether an existing plaintext store should be rejected.</summary>
    public bool RequireEncryptedStore { get; init; }
}

/// <summary>Background flush queue settings.</summary>
public sealed record JsonColdStoreAsyncFlushOptions
{
    /// <summary>Whether saves may publish through a background flush queue.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Maximum queued flush intents before callers apply back-pressure.</summary>
    public int QueueCapacity { get; init; } = 256;
}

/// <summary>Bounded retry policy for storage operations.</summary>
public sealed record JsonColdStoreRetryOptions
{
    /// <summary>Default retry policy for background flush attempts.</summary>
    public static JsonColdStoreRetryOptions DefaultFlushRetry { get; } = new()
    {
        MaxRetries = 3,
        BaseDelay = TimeSpan.FromMilliseconds(200),
    };

    /// <summary>Default retry policy for transaction replay attempts.</summary>
    public static JsonColdStoreRetryOptions DefaultTransactionReplay { get; } = new()
    {
        MaxRetries = 3,
        BaseDelay = TimeSpan.Zero,
    };

    /// <summary>Default retry policy for transient storage read failures.</summary>
    public static JsonColdStoreRetryOptions DefaultReadRetry { get; } = new()
    {
        MaxRetries = 3,
        BaseDelay = TimeSpan.FromMilliseconds(25),
    };

    /// <summary>Maximum number of retry attempts after the first failure.</summary>
    public int MaxRetries { get; init; }

    /// <summary>Base delay used by retry loops.</summary>
    public TimeSpan BaseDelay { get; init; }
}

/// <summary>Checksum and verification behavior.</summary>
public sealed record JsonColdStoreIntegrityOptions
{
    /// <summary>Whether checksum manifests are maintained.</summary>
    public bool EnableChecksums { get; init; } = true;

    /// <summary>Whether checksums are verified during startup hydration.</summary>
    public bool VerifyOnStartup { get; init; } = true;

    /// <summary>Whether checksums are verified on individual reads.</summary>
    public bool VerifyOnRead { get; init; }
}

/// <summary>Retention behavior for quarantined corrupt files.</summary>
public sealed record JsonColdStoreQuarantineOptions
{
    /// <summary>How long quarantined files should be retained.</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(30);
}

/// <summary>Cold index maintenance behavior.</summary>
public sealed record JsonColdStoreIndexMaintenanceOptions
{
    /// <summary>Periodic full-rescan interval. <see cref="TimeSpan.Zero"/> disables it.</summary>
    public TimeSpan RescanInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Whether catalog mismatches should trigger index rebuilds.</summary>
    public bool RebuildOnCatalogMismatch { get; init; } = true;
}

/// <summary>Provider-level append-only event log behavior.</summary>
public sealed record JsonColdStoreEventLogOptions
{
    /// <summary>Whether provider event logging is enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>How long provider event log files should be retained.</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(7);
}

/// <summary>Disaster-recovery snapshot behavior.</summary>
public sealed record JsonColdStoreSnapshotOptions
{
    /// <summary>Whether periodic snapshots are enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>Time between automatic snapshots.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Maximum number of snapshots to keep.</summary>
    public int RetentionCount { get; init; } = 3;
}
