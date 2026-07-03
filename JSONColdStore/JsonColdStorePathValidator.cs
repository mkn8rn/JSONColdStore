namespace JSONColdStore;

/// <summary>
/// Normalizes and constrains file-system paths used by the storage engine.
/// </summary>
public static class JsonColdStorePathValidator
{
    /// <summary>
    /// Converts a configured database directory to a full path and rejects unsafe roots.
    /// </summary>
    public static string NormalizeDatabaseDirectory(string databaseDirectory)
    {
        if (string.IsNullOrWhiteSpace(databaseDirectory))
            throw new ArgumentException("A database directory is required.", nameof(databaseDirectory));

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(databaseDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("The database directory path is invalid.", nameof(databaseDirectory), ex);
        }

        fullPath = TrimDirectorySeparators(fullPath);
        var root = Path.GetPathRoot(fullPath);
        if (string.Equals(fullPath, TrimDirectorySeparators(root ?? string.Empty), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The database directory cannot be a filesystem root.", nameof(databaseDirectory));

        return fullPath;
    }

    /// <summary>
    /// Combines child path segments under a database directory and rejects traversal escapes.
    /// </summary>
    public static string GetSafeChildPath(string databaseDirectory, params ReadOnlySpan<string> pathSegments)
    {
        var root = NormalizeDatabaseDirectory(databaseDirectory);
        if (pathSegments.Length == 0)
            return root;

        var candidate = root;
        foreach (var segment in pathSegments)
        {
            ValidateChildPathSegment(segment, nameof(pathSegments));

            candidate = Path.Combine(candidate, segment);
        }

        var fullCandidate = Path.GetFullPath(candidate);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        if (!fullCandidate.StartsWith(rootWithSeparator, comparison)
            && !string.Equals(fullCandidate, root, comparison))
        {
            throw new UnauthorizedAccessException("The resolved path escapes the database directory.");
        }

        return fullCandidate;
    }

    private static void ValidateChildPathSegment(string segment, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(segment))
            throw new ArgumentException("Path segments cannot be empty.", parameterName);

        if (segment is "." or ".."
            || Path.IsPathRooted(segment)
            || segment.Contains(Path.DirectorySeparatorChar)
            || segment.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new UnauthorizedAccessException(
                "Path segments must be relative child names without traversal or directory separators.");
        }
    }

    private static string TrimDirectorySeparators(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        var root = Path.GetPathRoot(path);
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrEmpty(trimmed) && !string.IsNullOrEmpty(root)
            ? root
            : trimmed;
    }
}
