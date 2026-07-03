namespace JSONColdStore.Storage;

internal static class JsonColdStoreDirectoryGuard
{
    internal static string CreateDirectory(
        string databaseDirectory,
        params ReadOnlySpan<string> pathSegments)
    {
        var root = CreateDatabaseRoot(databaseDirectory);

        if (pathSegments.Length == 0)
            return root;

        var directorySegments = new List<string>(pathSegments.Length);
        foreach (var segment in pathSegments)
        {
            directorySegments.Add(segment);
            var directory = JsonColdStorePathValidator.GetSafeChildPath(
                root,
                [.. directorySegments]);

            if (Directory.Exists(directory))
            {
                ThrowIfReparsePoint(directory);
                continue;
            }

            Directory.CreateDirectory(directory);
            ThrowIfReparsePoint(directory);
        }

        return JsonColdStorePathValidator.GetSafeChildPath(root, pathSegments);
    }

    internal static string CreateDatabaseRoot(string databaseDirectory)
    {
        var root = JsonColdStorePathValidator.NormalizeDatabaseDirectory(databaseDirectory);
        Directory.CreateDirectory(root);
        ThrowIfDatabaseRootIsReparsePoint(root);
        return root;
    }

    internal static bool ExistingDatabaseRootIsSafe(string databaseDirectory)
    {
        var root = JsonColdStorePathValidator.NormalizeDatabaseDirectory(databaseDirectory);
        return Directory.Exists(root) && !JsonColdStoreDirectoryWalker.IsReparsePoint(root);
    }

    internal static void ThrowIfExistingDatabaseRootIsReparsePoint(string databaseDirectory)
    {
        var root = JsonColdStorePathValidator.NormalizeDatabaseDirectory(databaseDirectory);
        if (!Directory.Exists(root))
            return;

        ThrowIfDatabaseRootIsReparsePoint(root);
    }

    internal static void ThrowIfReparsePoint(string path)
    {
        if (JsonColdStoreDirectoryWalker.IsReparsePoint(path))
        {
            throw new JsonColdStoreUnsafePathException(
                "The JSONColdStore path cannot contain reparse-point child directories.");
        }
    }

    internal static void ThrowIfContainsReparsePoint(
        string directory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ThrowIfContainsReparsePointCore(directory, cancellationToken);
        }
        catch (JsonColdStoreUnsafePathException)
        {
            throw;
        }
        catch (IOException)
        {
            throw new JsonColdStoreUnsafePathException(
                "The JSONColdStore directory cannot be safely inspected before deletion.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new JsonColdStoreUnsafePathException(
                "The JSONColdStore directory cannot be safely inspected before deletion.");
        }
    }

    private static void ThrowIfDatabaseRootIsReparsePoint(string root)
    {
        if (JsonColdStoreDirectoryWalker.IsReparsePoint(root))
        {
            throw new JsonColdStoreUnsafePathException(
                "The configured JSONColdStore database directory cannot be a reparse point.");
        }
    }

    private static void ThrowIfContainsReparsePointCore(
        string directory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (JsonColdStoreDirectoryWalker.IsReparsePoint(directory))
        {
            throw new JsonColdStoreUnsafePathException(
                "The JSONColdStore directory cannot be deleted because it contains a reparse point.");
        }

        foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (JsonColdStoreDirectoryWalker.IsReparsePoint(file))
            {
                throw new JsonColdStoreUnsafePathException(
                    "The JSONColdStore directory cannot be deleted because it contains a reparse point.");
            }
        }

        foreach (var childDirectory in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfContainsReparsePointCore(childDirectory, cancellationToken);
        }
    }
}
