namespace JSONColdStore;

/// <summary>
/// Controls behavior when a query cannot use a declared index.
/// </summary>
public enum JsonColdStoreScanPolicy
{
    /// <summary>Reject LINQ query shapes that require a full scan.</summary>
    FailUnlessExplicit = 0,

    /// <summary>Allow full scans only when the LINQ query marks the scan explicitly.</summary>
    AllowExplicitScans = 1,

    /// <summary>Allow scans without a separate opt-in. Useful for small stores only.</summary>
    AllowSilentScans = 2,
}
