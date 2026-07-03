namespace EFC.JSONColdStore.Storage;

internal static class JsonColdStoreTemporaryFileCleaner
{
    internal static int DeleteOrphanedAtomicTempFiles(string databaseDirectory)
    {
        if (!Directory.Exists(databaseDirectory))
            return 0;
        if (JsonColdStoreDirectoryWalker.IsReparsePoint(databaseDirectory))
        {
            throw new JsonColdStoreUnsafePathException(
                "The JSONColdStore temporary-file cleanup root cannot be a reparse point.");
        }

        var deleted = 0;
        foreach (var file in JsonColdStoreDirectoryWalker.EnumerateFiles(
                     databaseDirectory,
                     shouldSkipDirectory: IsSnapshotDirectory))
        {
            if (!Path.GetFileName(file).Contains(".tmp-", StringComparison.Ordinal))
                continue;

            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return deleted;
    }

    private static bool IsSnapshotDirectory(string directory) =>
        string.Equals(Path.GetFileName(directory), "_snapshots", StringComparison.Ordinal);
}
