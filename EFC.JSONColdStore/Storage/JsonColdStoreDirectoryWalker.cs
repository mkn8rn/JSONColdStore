namespace EFC.JSONColdStore.Storage;

internal static class JsonColdStoreDirectoryWalker
{
    internal static IEnumerable<string> EnumerateFiles(
        string directory,
        string searchPattern = "*",
        Func<string, bool>? shouldSkipDirectory = null) =>
        EnumerateFiles(
            directory,
            searchPattern,
            shouldSkipDirectory,
            IsReparsePoint);

    internal static IEnumerable<string> EnumerateFiles(
        string directory,
        string searchPattern,
        Func<string, bool>? shouldSkipDirectory,
        Func<string, bool> isReparsePoint)
    {
        ArgumentNullException.ThrowIfNull(isReparsePoint);

        if (!Directory.Exists(directory))
            yield break;

        foreach (var file in Directory.EnumerateFiles(directory, searchPattern).Order(StringComparer.Ordinal))
        {
            if (!isReparsePoint(file))
                yield return file;
        }

        foreach (var childDirectory in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
        {
            if (shouldSkipDirectory?.Invoke(childDirectory) == true)
                continue;

            if (isReparsePoint(childDirectory))
                continue;

            foreach (var file in EnumerateFiles(
                         childDirectory,
                         searchPattern,
                         shouldSkipDirectory,
                         isReparsePoint))
            {
                yield return file;
            }
        }
    }

    internal static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
