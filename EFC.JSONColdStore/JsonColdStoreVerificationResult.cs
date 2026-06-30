namespace EFC.JSONColdStore;

/// <summary>
/// Result returned by an explicit JSONColdStore verification pass.
/// </summary>
public sealed record JsonColdStoreVerificationResult(
    int VerifiedRecords,
    int VerifiedLegacyRecords);
