namespace JSONColdStore.Storage;

internal static class JsonColdStoreLegacyRecordNames
{
    internal static bool TryGetRecordIdFromFileName(string? fileName, out string recordId)
    {
        recordId = string.Empty;
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(safeFileName, fileName, StringComparison.Ordinal))
            return false;
        if (safeFileName.StartsWith('_'))
            return false;

        var candidate = Path.GetFileNameWithoutExtension(safeFileName);
        if (!IsSafeRecordId(candidate))
            return false;

        recordId = candidate;
        return true;
    }

    internal static bool IsSafeRecordFile(string path) =>
        TryGetRecordIdFromFileName(Path.GetFileName(path), out _);

    internal static bool IsSafeRecordId(string? recordId)
    {
        if (string.IsNullOrWhiteSpace(recordId))
            return false;
        if (recordId is "." or "..")
            return false;
        if (recordId.StartsWith('_'))
            return false;
        if (recordId.Contains('\\') || recordId.Contains('/'))
            return false;
        if (recordId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;

        return string.Equals(Path.GetFileName(recordId), recordId, StringComparison.Ordinal);
    }
}
