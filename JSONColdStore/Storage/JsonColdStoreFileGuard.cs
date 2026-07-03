namespace JSONColdStore.Storage;

internal static class JsonColdStoreFileGuard
{
    internal static void ThrowIfReparsePoint(string path, string message)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (IOException)
        {
            throw new JsonColdStoreUnsafePathException(message);
        }
        catch (UnauthorizedAccessException)
        {
            throw new JsonColdStoreUnsafePathException(message);
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new JsonColdStoreUnsafePathException(message);
    }
}
