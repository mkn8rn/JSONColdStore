namespace JSONColdStore.Storage;

internal sealed class JsonColdStoreUnsafePathException(string message)
    : UnauthorizedAccessException(message);
