namespace JSONColdStore;

/// <summary>
/// Result returned by a manual JSONColdStore snapshot operation.
/// </summary>
public sealed record JsonColdStoreSnapshotResult(
    string SnapshotDirectory,
    int CopiedFiles,
    int DeletedSnapshots);
