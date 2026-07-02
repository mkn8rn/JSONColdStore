using System.Diagnostics;
using EFC.JSONColdStore.Storage;

namespace EFC.JSONColdStore.Tests;

internal static class JsonColdStoreReparsePointTestHelper
{
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
