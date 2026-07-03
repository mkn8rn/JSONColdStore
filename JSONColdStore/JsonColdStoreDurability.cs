namespace JSONColdStore;

/// <summary>
/// Durability behavior for writes published by the storage engine.
/// </summary>
public enum JsonColdStoreDurability
{
    /// <summary>Writes use intent manifests and atomic publication.</summary>
    ManifestedAtomicWrites = 0,
}
