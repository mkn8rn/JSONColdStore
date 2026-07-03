namespace JSONColdStore;

/// <summary>
/// Controls how much record data is loaded when a store is opened.
/// </summary>
public enum JsonColdStoreStartupMode
{
    /// <summary>Open catalogs and indexes without hydrating all records.</summary>
    MetadataOnly = 0,

    /// <summary>Hydrate records during startup when a small hot store wants it.</summary>
    FullHydration = 1,
}
