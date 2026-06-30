namespace EFC.JSONColdStore;

/// <summary>
/// Redacted operational diagnostics for a JSONColdStore database directory.
/// </summary>
public sealed record JsonColdStoreDiagnosticsResult
{
    /// <summary>Whether the root metadata document exists.</summary>
    public bool HasStoreMetadata { get; init; }

    /// <summary>Store identifier from root metadata, or <see langword="null"/> when metadata is absent.</summary>
    public Guid? StoreId { get; init; }

    /// <summary>On-disk store format version, or <see langword="null"/> when metadata is absent.</summary>
    public int? FormatVersion { get; init; }

    /// <summary>Provider version recorded by root metadata, or <see langword="null"/> when metadata is absent.</summary>
    public string? ProviderVersion { get; init; }

    /// <summary>Compression policy recorded by root metadata or current options.</summary>
    public JsonColdStoreCompression Compression { get; init; }

    /// <summary>Whether payload encryption is enabled by metadata or current options.</summary>
    public bool EncryptionEnabled { get; init; }

    /// <summary>Whether payload integrity tags are enabled by current options.</summary>
    public bool IntegrityChecksumsEnabled { get; init; }

    /// <summary>Whether payload integrity tags use a host-forwarded HMAC key.</summary>
    public bool KeyedIntegrityEnabled { get; init; }

    /// <summary>Startup mode recorded by root metadata or current options.</summary>
    public JsonColdStoreStartupMode StartupMode { get; init; }

    /// <summary>Full-scan policy recorded by root metadata or current options.</summary>
    public JsonColdStoreScanPolicy FullScanPolicy { get; init; }

    /// <summary>Number of mapped entity types in the current EF model.</summary>
    public int MappedEntityCount { get; init; }

    /// <summary>Number of JSONColdStore record files visible under mapped entity directories.</summary>
    public int RecordFileCount { get; init; }

    /// <summary>Number of JSONColdStore index files visible under mapped entity directories.</summary>
    public int IndexFileCount { get; init; }

    /// <summary>Number of compatible legacy record files visible under mapped entity directories.</summary>
    public int LegacyRecordFileCount { get; init; }

    /// <summary>Number of pending transaction manifests.</summary>
    public int PendingManifestCount { get; init; }

    /// <summary>Number of failed transaction manifests.</summary>
    public int FailedManifestCount { get; init; }

    /// <summary>Number of staged write payload files.</summary>
    public int StagedWriteCount { get; init; }

    /// <summary>Number of quarantined record files.</summary>
    public int QuarantineFileCount { get; init; }

    /// <summary>Number of snapshot directories.</summary>
    public int SnapshotCount { get; init; }

    /// <summary>Number of provider event log files.</summary>
    public int EventLogFileCount { get; init; }

    /// <summary>Number of orphaned atomic writer temp files outside snapshots.</summary>
    public int TemporaryFileCount { get; init; }

    /// <summary>Per-entity redacted diagnostics for the current EF model.</summary>
    public required IReadOnlyList<JsonColdStoreEntityDiagnostics> Entities { get; init; }
}

/// <summary>
/// Redacted operational diagnostics for one mapped JSONColdStore entity.
/// </summary>
public sealed record JsonColdStoreEntityDiagnostics
{
    /// <summary>Provider storage entity name.</summary>
    public required string EntityName { get; init; }

    /// <summary>Mapped CLR type name.</summary>
    public required string ClrTypeName { get; init; }

    /// <summary>Number of declared EF indexes for this entity.</summary>
    public int DeclaredIndexCount { get; init; }

    /// <summary>Number of JSONColdStore record files for this entity.</summary>
    public int RecordFileCount { get; init; }

    /// <summary>Number of JSONColdStore index files for this entity.</summary>
    public int IndexFileCount { get; init; }

    /// <summary>Number of compatible legacy record files for this entity.</summary>
    public int LegacyRecordFileCount { get; init; }
}
