using System.Globalization;

namespace EFC.JSONColdStore.Storage;

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
        var snapshotRoot = JsonColdStorePathValidator.GetSafeChildPath(
            _options.DatabaseDirectory,
            SnapshotDirectoryName);
        Directory.CreateDirectory(snapshotRoot);
        if (JsonColdStoreDirectoryWalker.IsReparsePoint(snapshotRoot))
            throw new UnauthorizedAccessException("The JSONColdStore snapshot directory cannot be a reparse point.");

        var snapshotName = DateTimeOffset.UtcNow.ToString(
            "yyyyMMddHHmmssfffffff",
            CultureInfo.InvariantCulture)
            + "-"
            + Guid.NewGuid().ToString("N");
        var snapshotDirectory = JsonColdStorePathValidator.GetSafeChildPath(
            snapshotRoot,
            snapshotName);
        Directory.CreateDirectory(snapshotDirectory);

        try
        {
            var copiedFiles = 0;
            foreach (var sourceFile in EnumerateSnapshotSourceFiles(_options.DatabaseDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(_options.DatabaseDirectory, sourceFile);
                var targetPath = JsonColdStorePathValidator.GetSafeChildPath(
                    snapshotDirectory,
                    relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var targetDirectory = Path.GetDirectoryName(targetPath)
                    ?? throw new InvalidOperationException("The snapshot target path has no directory.");

                Directory.CreateDirectory(targetDirectory);
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

            Directory.Delete(directory, recursive: true);
            deleted++;
        }

        return deleted;
    }

    private static void DeleteIncompleteSnapshot(string snapshotDirectory)
    {
        try
        {
            if (Directory.Exists(snapshotDirectory))
                Directory.Delete(snapshotDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
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

    private static bool IsTemporaryFile(string path) =>
        Path.GetFileName(path).Contains(".tmp-", StringComparison.Ordinal);
}
