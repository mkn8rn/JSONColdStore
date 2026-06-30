namespace EFC.JSONColdStore.Storage;

internal static class JsonColdStoreTemporaryFileCleaner
{
    internal static int DeleteOrphanedAtomicTempFiles(string databaseDirectory)
    {
        if (!Directory.Exists(databaseDirectory))
            return 0;

        var deleted = 0;
        foreach (var file in EnumerateTempFiles(databaseDirectory))
        {
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

    private static IEnumerable<string> EnumerateTempFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(file).Contains(".tmp-", StringComparison.Ordinal))
                yield return file;
        }

        foreach (var childDirectory in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
        {
            if (string.Equals(Path.GetFileName(childDirectory), "_snapshots", StringComparison.Ordinal))
                continue;

            foreach (var file in EnumerateTempFiles(childDirectory))
                yield return file;
        }
    }
}
