using System.Text;

namespace EFC.JSONColdStore.Storage;

internal static class JsonColdStoreNameEncoder
{
    internal static string EncodePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A path segment value is required.", nameof(value));

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return "n_" + encoded;
    }
}
