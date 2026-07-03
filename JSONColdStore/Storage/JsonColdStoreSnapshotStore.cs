using System.Globalization;

namespace JSONColdStore.Storage;

internal sealed class JsonColdStoreSnapshotStore
{
    private const string SnapshotDirectoryName = "_snapshots";
    private const string LockDirectoryName = "_locks";

    private readonly JsonColdStoreOptions _options;

    internal JsonColdStoreSnapshotStore(JsonColdStoreOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    internal async Task<JsonColdStoreSnapshotResult> CreateSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var snapshotRoot = JsonColdStoreDirectoryGuard.CreateDirectory(
            _options.DatabaseDirectory,
            SnapshotDirectoryName);

        var snapshotName = DateTimeOffset.UtcNow.ToString(
            "yyyyMMddHHmmssfffffff",
            CultureInfo.InvariantCulture)
            + "-"
            + Guid.NewGuid().ToString("N");
        var snapshotDirectory = JsonColdStoreDirectoryGuard.CreateDirectory(
            _options.DatabaseDirectory,
            SnapshotDirectoryName,
            snapshotName);

        try
        {
            var copiedFiles = 0;
            foreach (var sourceFile in EnumerateSnapshotSourceFiles(_options.DatabaseDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(_options.DatabaseDirectory, sourceFile);
                var targetSegments = relativePath.Split(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                var targetPath = JsonColdStorePathValidator.GetSafeChildPath(
                    snapshotDirectory,
                    targetSegments);

                CreateSafeSnapshotTargetDirectory(snapshotDirectory, targetSegments);
                await CopyFileAsync(sourceFile, targetPath, cancellationToken).ConfigureAwait(false);
                copiedFiles++;
            }

            var deletedSnapshots = PruneSnapshots(snapshotRoot, snapshotDirectory);
            return new JsonColdStoreSnapshotResult(
                snapshotDirectory,
                copiedFiles,
                deletedSnapshots);
        }
        catch
        {
            DeleteIncompleteSnapshot(snapshotDirectory);
            throw;
        }
    }

    private async Task CopyFileAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        if (JsonColdStoreDirectoryWalker.IsReparsePoint(sourcePath))
            throw new JsonColdStoreUnsafePathException("The snapshot source file cannot be a reparse point.");

        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var target = new FileStream(
            targetPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        if (_options.FsyncOnWrite)
            target.Flush(flushToDisk: true);
    }

    private int PruneSnapshots(string snapshotRoot, string currentSnapshotDirectory)
    {
        var retentionCount = _options.Snapshots.RetentionCount;
        var snapshotDirectories = Directory.EnumerateDirectories(snapshotRoot)
            .Where(directory => !JsonColdStoreDirectoryWalker.IsReparsePoint(directory))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();
        var deleted = 0;

        foreach (var directory in snapshotDirectories.Skip(retentionCount))
        {
            if (string.Equals(directory, currentSnapshotDirectory, StringComparison.OrdinalIgnoreCase))
                continue;

            if (DeleteSnapshotDirectoryIfSafe(directory))
                deleted++;
        }

        return deleted;
    }

    private static void DeleteIncompleteSnapshot(string snapshotDirectory)
    {
        DeleteSnapshotDirectoryIfSafe(snapshotDirectory);
    }

    private static bool DeleteSnapshotDirectoryIfSafe(string snapshotDirectory)
    {
        try
        {
            if (Directory.Exists(snapshotDirectory)
                && !JsonColdStoreDirectoryWalker.IsReparsePoint(snapshotDirectory))
            {
                JsonColdStoreDirectoryGuard.ThrowIfContainsReparsePoint(snapshotDirectory);
                Directory.Delete(snapshotDirectory, recursive: true);
                return true;
            }
        }
        catch (JsonColdStoreUnsafePathException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }

    private static IEnumerable<string> EnumerateSnapshotSourceFiles(string directory)
    {
        foreach (var file in JsonColdStoreDirectoryWalker.EnumerateFiles(
                     directory,
                     shouldSkipDirectory: IsSkippedSnapshotDirectory))
        {
            if (!IsTemporaryFile(file))
                yield return file;
        }
    }

    private static bool IsSkippedSnapshotDirectory(string directory)
    {
        var name = Path.GetFileName(directory);
        return string.Equals(name, SnapshotDirectoryName, StringComparison.Ordinal)
            || string.Equals(name, LockDirectoryName, StringComparison.Ordinal);
    }

    private static void CreateSafeSnapshotTargetDirectory(
        string snapshotDirectory,
        IReadOnlyList<string> targetSegments)
    {
        if (targetSegments.Count <= 1)
        {
            JsonColdStoreDirectoryGuard.CreateDirectory(snapshotDirectory);
            return;
        }

        JsonColdStoreDirectoryGuard.CreateDirectory(
            snapshotDirectory,
            [.. targetSegments.Take(targetSegments.Count - 1)]);
    }

    private static bool IsTemporaryFile(string path) =>
        Path.GetFileName(path).Contains(".tmp-", StringComparison.Ordinal);
}
