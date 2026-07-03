namespace JSONColdStore;

/// <summary>
/// Built-in compression policy for JSONColdStore payloads.
/// </summary>
public enum JsonColdStoreCompression
{
    /// <summary>No provider-managed compression is applied.</summary>
    None = 0,

    /// <summary>The provider decides when compression is worth the cost.</summary>
    Auto = 1,

    /// <summary>Use Brotli compression for eligible payloads.</summary>
    Brotli = 2,
}
