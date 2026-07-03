namespace JSONColdStore;

/// <summary>
/// Result returned by an explicit JSONColdStore verification pass.
/// </summary>
public sealed record JsonColdStoreVerificationResult(
    int VerifiedRecords,
    int VerifiedLegacyRecords)
{
    /// <summary>
    /// Number of declared current-format index documents verified.
    /// </summary>
    public int VerifiedIndexes { get; init; }
}
