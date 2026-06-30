using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace EFC.JSONColdStore.Storage;

internal static class JsonColdStorePayloadCodec
{
    private const byte Version = 1;
    private const byte CompressedFlag = 1;
    private const byte EncryptedFlag = 2;
    private const byte ChecksumFlag = 4;
    private const byte KeyedIntegrityFlag = 8;
    private const byte KnownFlags = CompressedFlag | EncryptedFlag | ChecksumFlag | KeyedIntegrityFlag;
    private const byte CompressionNone = 0;
    private const byte CompressionBrotli = 1;
    private const byte EncryptionNone = 0;
    private const byte EncryptionAes256Gcm = 1;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int ChecksumLength = 32;
    private const int HeaderLength = 20;
    private const int AutoCompressionThresholdBytes = 4096;

    private static ReadOnlySpan<byte> Magic => "JCS1"u8;

    internal static byte[] Encode(ReadOnlySpan<byte> plaintext, JsonColdStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var payload = plaintext.ToArray();
        byte flags = 0;
        byte compression = CompressionNone;

        if (ShouldTryCompression(options.Compression, plaintext.Length))
        {
            var compressed = CompressBrotli(payload);
            if (options.Compression == JsonColdStoreCompression.Brotli || compressed.Length < payload.Length)
            {
                payload = compressed;
                flags |= CompressedFlag;
                compression = CompressionBrotli;
            }
        }

        byte encryption = EncryptionNone;
        var keyIdBytes = Array.Empty<byte>();
        var nonce = Array.Empty<byte>();
        var tag = Array.Empty<byte>();

        if (options.Encryption is not null)
        {
            keyIdBytes = string.IsNullOrWhiteSpace(options.Encryption.KeyId)
                ? []
                : Encoding.UTF8.GetBytes(options.Encryption.KeyId);

            (payload, nonce, tag) = Encrypt(payload, options.Encryption.Key);
            flags |= EncryptedFlag;
            encryption = EncryptionAes256Gcm;
        }

        var checksum = Array.Empty<byte>();
        if (options.Integrity.EnableChecksums)
        {
            checksum = ComputeIntegrityTag(payload, options.Integrity.Key);
            flags |= ChecksumFlag;
            if (options.Integrity.Key is not null)
                flags |= KeyedIntegrityFlag;
        }

        return WriteEnvelope(flags, compression, encryption, keyIdBytes, nonce, tag, checksum, payload);
    }

    internal static byte[] Decode(ReadOnlySpan<byte> encoded, JsonColdStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var envelope = ReadEnvelope(encoded);
        if (envelope.Encrypted && options.Encryption is null)
            throw new InvalidOperationException("An encryption key is required to read this payload.");

        if (!envelope.Encrypted && options.Encryption?.RequireEncryptedStore == true)
            throw new InvalidOperationException("The store requires encrypted payloads.");

        var payload = envelope.Payload.ToArray();
        if (options.Integrity.VerifyOnRead)
        {
            if (!envelope.HasChecksum)
                throw new InvalidDataException("The payload does not contain a checksum.");

            var actualChecksum = envelope.HasKeyedIntegrity
                ? ComputeRequiredIntegrityTag(payload, options.Integrity.Key)
                : SHA256.HashData(payload);
            if (!CryptographicOperations.FixedTimeEquals(actualChecksum, envelope.Checksum.Span))
                throw new InvalidDataException("The payload checksum is invalid.");
        }

        if (envelope.Encrypted)
        {
            if (!string.IsNullOrEmpty(envelope.KeyId)
                && !string.IsNullOrEmpty(options.Encryption!.KeyId)
                && !string.Equals(envelope.KeyId, options.Encryption.KeyId, StringComparison.Ordinal))
            {
                throw new CryptographicException("The encrypted payload key id does not match the configured key.");
            }

            payload = Decrypt(payload, envelope.Nonce.Span, envelope.Tag.Span, options.Encryption!.Key);
        }

        if (envelope.Compressed)
            payload = DecompressBrotli(payload);

        return payload;
    }

    private static bool ShouldTryCompression(JsonColdStoreCompression compression, int byteCount) =>
        compression switch
        {
            JsonColdStoreCompression.None => false,
            JsonColdStoreCompression.Brotli => true,
            JsonColdStoreCompression.Auto => byteCount >= AutoCompressionThresholdBytes,
            _ => throw new ArgumentOutOfRangeException(nameof(compression), compression, "Unsupported compression mode."),
        };

    private static byte[] CompressBrotli(ReadOnlySpan<byte> input)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            brotli.Write(input);
        }

        return output.ToArray();
    }

    private static byte[] DecompressBrotli(ReadOnlySpan<byte> input)
    {
        using var source = new MemoryStream(input.ToArray());
        using var brotli = new BrotliStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }

    private static (byte[] Ciphertext, byte[] Nonce, byte[] Tag) Encrypt(
        ReadOnlySpan<byte> plaintext,
        JsonColdStoreEncryptionKey key)
    {
        var keyBytes = key.CopyBytes();
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceLength);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagLength];
            using var aes = new AesGcm(keyBytes, TagLength);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
            return (ciphertext, nonce, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    private static byte[] ComputeIntegrityTag(
        ReadOnlySpan<byte> payload,
        JsonColdStoreIntegrityKey? key)
    {
        if (key is null)
            return SHA256.HashData(payload);

        var keyBytes = key.CopyBytes();
        try
        {
            return HMACSHA256.HashData(keyBytes, payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    private static byte[] ComputeRequiredIntegrityTag(
        ReadOnlySpan<byte> payload,
        JsonColdStoreIntegrityKey? key)
    {
        if (key is null)
            throw new InvalidOperationException("An integrity key is required to verify this payload.");

        return ComputeIntegrityTag(payload, key);
    }

    private static byte[] Decrypt(
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> tag,
        JsonColdStoreEncryptionKey key)
    {
        var keyBytes = key.CopyBytes();
        try
        {
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(keyBytes, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    private static byte[] WriteEnvelope(
        byte flags,
        byte compression,
        byte encryption,
        ReadOnlySpan<byte> keyId,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> tag,
        ReadOnlySpan<byte> checksum,
        ReadOnlySpan<byte> payload)
    {
        if (keyId.Length > ushort.MaxValue
            || nonce.Length > ushort.MaxValue
            || tag.Length > ushort.MaxValue
            || checksum.Length > ushort.MaxValue)
        {
            throw new InvalidOperationException("Envelope metadata is too large.");
        }

        var output = new byte[
            HeaderLength + keyId.Length + nonce.Length + tag.Length + checksum.Length + payload.Length];
        Magic.CopyTo(output);
        output[4] = Version;
        output[5] = flags;
        output[6] = compression;
        output[7] = encryption;
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(8, 2), checked((ushort)keyId.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(10, 2), checked((ushort)nonce.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(12, 2), checked((ushort)tag.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(14, 2), checked((ushort)checksum.Length));
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(16, 4), payload.Length);

        var cursor = HeaderLength;
        keyId.CopyTo(output.AsSpan(cursor));
        cursor += keyId.Length;
        nonce.CopyTo(output.AsSpan(cursor));
        cursor += nonce.Length;
        tag.CopyTo(output.AsSpan(cursor));
        cursor += tag.Length;
        checksum.CopyTo(output.AsSpan(cursor));
        cursor += checksum.Length;
        payload.CopyTo(output.AsSpan(cursor));
        return output;
    }

    private static Envelope ReadEnvelope(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length < HeaderLength || !encoded[..4].SequenceEqual(Magic))
            throw new InvalidDataException("The payload is not a JSONColdStore envelope.");

        if (encoded[4] != Version)
            throw new InvalidDataException($"Unsupported JSONColdStore envelope version {encoded[4]}.");

        var flags = encoded[5];
        var compression = encoded[6];
        var encryption = encoded[7];
        var keyIdLength = BinaryPrimitives.ReadUInt16LittleEndian(encoded.Slice(8, 2));
        var nonceLength = BinaryPrimitives.ReadUInt16LittleEndian(encoded.Slice(10, 2));
        var tagLength = BinaryPrimitives.ReadUInt16LittleEndian(encoded.Slice(12, 2));
        var checksumLength = BinaryPrimitives.ReadUInt16LittleEndian(encoded.Slice(14, 2));
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(encoded.Slice(16, 4));

        if (payloadLength < 0)
            throw new InvalidDataException("Envelope payload length cannot be negative.");
        if ((flags & ~KnownFlags) != 0)
            throw new InvalidDataException("Unsupported payload envelope flags.");

        var expectedLength = HeaderLength + keyIdLength + nonceLength + tagLength + checksumLength + payloadLength;
        if (encoded.Length != expectedLength)
            throw new InvalidDataException("Envelope length does not match its header.");

        var compressed = (flags & CompressedFlag) != 0;
        var encrypted = (flags & EncryptedFlag) != 0;
        var hasChecksum = (flags & ChecksumFlag) != 0;
        var hasKeyedIntegrity = (flags & KeyedIntegrityFlag) != 0;
        if (compressed && compression != CompressionBrotli)
            throw new InvalidDataException("Unsupported compression algorithm.");
        if (!compressed && compression != CompressionNone)
            throw new InvalidDataException("Compression metadata is inconsistent.");
        if (encrypted && encryption != EncryptionAes256Gcm)
            throw new InvalidDataException("Unsupported encryption algorithm.");
        if (!encrypted && encryption != EncryptionNone)
            throw new InvalidDataException("Encryption metadata is inconsistent.");
        if (encrypted && (nonceLength != NonceLength || tagLength != TagLength))
            throw new InvalidDataException("AES-GCM envelope metadata is invalid.");
        if (hasChecksum && checksumLength != ChecksumLength)
            throw new InvalidDataException("Payload checksum metadata is invalid.");
        if (!hasChecksum && checksumLength != 0)
            throw new InvalidDataException("Payload checksum metadata is inconsistent.");
        if (hasKeyedIntegrity && !hasChecksum)
            throw new InvalidDataException("Keyed integrity metadata requires a checksum.");

        var cursor = HeaderLength;
        var keyId = Encoding.UTF8.GetString(encoded.Slice(cursor, keyIdLength));
        cursor += keyIdLength;
        var nonce = encoded.Slice(cursor, nonceLength).ToArray();
        cursor += nonceLength;
        var tag = encoded.Slice(cursor, tagLength).ToArray();
        cursor += tagLength;
        var checksum = encoded.Slice(cursor, checksumLength).ToArray();
        cursor += checksumLength;
        var payload = encoded.Slice(cursor, payloadLength).ToArray();

        return new Envelope(
            compressed,
            encrypted,
            hasChecksum,
            hasKeyedIntegrity,
            keyId,
            nonce,
            tag,
            checksum,
            payload);
    }

    private sealed record Envelope(
        bool Compressed,
        bool Encrypted,
        bool HasChecksum,
        bool HasKeyedIntegrity,
        string KeyId,
        ReadOnlyMemory<byte> Nonce,
        ReadOnlyMemory<byte> Tag,
        ReadOnlyMemory<byte> Checksum,
        ReadOnlyMemory<byte> Payload);
}
