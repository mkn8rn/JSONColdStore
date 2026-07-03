using System.Text;

namespace JSONColdStore.Storage;

internal static class JsonColdStoreNameEncoder
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

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

    internal static string DecodePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException("An encoded path segment is required.");
        if (!value.StartsWith("n_", StringComparison.Ordinal))
            throw new InvalidDataException("The encoded path segment prefix is invalid.");

        var encoded = value[2..]
            .Replace('-', '+')
            .Replace('_', '/');
        var padding = encoded.Length % 4;
        if (padding == 1)
            throw new InvalidDataException("The encoded path segment length is invalid.");
        if (padding > 0)
            encoded = encoded.PadRight(encoded.Length + 4 - padding, '=');

        try
        {
            return StrictUtf8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The encoded path segment is invalid.", ex);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("The encoded path segment is not valid UTF-8.", ex);
        }
    }
}
