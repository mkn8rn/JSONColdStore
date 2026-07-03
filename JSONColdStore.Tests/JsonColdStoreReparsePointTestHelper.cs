using System.Diagnostics;
using JSONColdStore.Storage;

namespace JSONColdStore.Tests;

internal static class JsonColdStoreReparsePointTestHelper
{
    internal static void CreateRequiredDirectoryLink(
        string linkPath,
        string targetPath,
        string proofName)
    {
        var created = TryCreateDirectoryLink(linkPath, targetPath);
        Assert.True(
            created,
            "Unable to create the linked directory required for "
            + proofName
            + ": "
            + linkPath
            + " -> "
            + targetPath);
    }

    internal static void CreateRequiredFileLink(
        string linkPath,
        string targetPath,
        string proofName)
    {
        var created = TryCreateFileLink(linkPath, targetPath);
        Assert.True(
            created,
            "Unable to create the linked file required for "
            + proofName
            + ": "
            + linkPath
            + " -> "
            + targetPath);
    }

    internal static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return TryCreateWindowsJunction(linkPath, targetPath);

            Directory.CreateSymbolicLink(linkPath, targetPath);
            return Directory.Exists(linkPath)
                && JsonColdStoreDirectoryWalker.IsReparsePoint(linkPath);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateFileLink(string linkPath, string targetPath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return TryCreateWindowsFileLink(linkPath, targetPath);

            File.CreateSymbolicLink(linkPath, targetPath);
            return File.Exists(linkPath)
                && JsonColdStoreDirectoryWalker.IsReparsePoint(linkPath);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateWindowsFileLink(string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo);
        if (process is null)
            return false;

        if (!process.WaitForExit(milliseconds: 5000))
        {
            process.Kill(entireProcessTree: true);
            return false;
        }

        return process.ExitCode == 0
            && File.Exists(linkPath)
            && JsonColdStoreDirectoryWalker.IsReparsePoint(linkPath);
    }

    private static bool TryCreateWindowsJunction(string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo);
        if (process is null)
            return false;

        if (!process.WaitForExit(milliseconds: 5000))
        {
            process.Kill(entireProcessTree: true);
            return false;
        }

        return process.ExitCode == 0
            && Directory.Exists(linkPath)
            && JsonColdStoreDirectoryWalker.IsReparsePoint(linkPath);
    }
}
