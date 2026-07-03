using System.Security.Cryptography;

namespace JSONColdStore;

/// <summary>
/// Host-owned HMAC-SHA256 key material forwarded to JSONColdStore configuration.
/// </summary>
public sealed class JsonColdStoreIntegrityKey : IDisposable
{
    /// <summary>Required HMAC key length in bytes.</summary>
    public const int RequiredByteLength = 32;

    private byte[]? _keyBytes;

    private JsonColdStoreIntegrityKey(byte[] keyBytes)
    {
        _keyBytes = keyBytes;
    }

    /// <summary>The number of bytes in the validated key.</summary>
    public int Length => _keyBytes?.Length ?? 0;

    /// <summary>Creates a key from exactly 32 bytes of key material.</summary>
    public static JsonColdStoreIntegrityKey FromBytes(ReadOnlySpan<byte> keyBytes)
    {
        if (keyBytes.Length != RequiredByteLength)
        {
            throw new ArgumentException(
                $"JSONColdStore integrity keys must be {RequiredByteLength} bytes.",
                nameof(keyBytes));
        }

        return new JsonColdStoreIntegrityKey(keyBytes.ToArray());
    }

    /// <summary>Creates a key from a base64-encoded 256-bit value.</summary>
    public static JsonColdStoreIntegrityKey FromBase64(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            throw new ArgumentException("An integrity key value is required.", nameof(base64));

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The integrity key must be valid base64.", nameof(base64), ex);
        }

        try
        {
            return FromBytes(decoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    /// <summary>Returns a defensive copy of the key for cryptographic operations.</summary>
    public byte[] CopyBytes()
    {
        ObjectDisposedException.ThrowIf(_keyBytes is null, this);
        return _keyBytes!.ToArray();
    }

    /// <summary>Returns a redacted display value that never includes key material.</summary>
    public override string ToString() => "JsonColdStoreIntegrityKey(****)";

    /// <summary>Clears held key material from memory where practical.</summary>
    public void Dispose()
    {
        if (_keyBytes is null)
            return;

        CryptographicOperations.ZeroMemory(_keyBytes);
        _keyBytes = null;
    }
}
