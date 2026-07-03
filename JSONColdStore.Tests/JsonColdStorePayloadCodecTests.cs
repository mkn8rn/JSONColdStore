using System.Security.Cryptography;
using System.Text;
using JSONColdStore;
using JSONColdStore.Storage;

namespace JSONColdStore.Tests;

public sealed class JsonColdStorePayloadCodecTests
{
    [Fact]
    public void EncodeDecodeWithoutEncryptionRoundTrips()
    {
        var options = new JsonColdStoreOptionsBuilder(TestDirectory("plain"))
            .UseCompression(JsonColdStoreCompression.None)
            .Build();
        var payload = Encoding.UTF8.GetBytes("""{"message":"hello"}""");

        var encoded = JsonColdStorePayloadCodec.Encode(payload, options);
        var decoded = JsonColdStorePayloadCodec.Decode(encoded, options);

        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void BrotliCompressionRoundTripsRepetitivePayload()
    {
        var options = new JsonColdStoreOptionsBuilder(TestDirectory("compressed"))
            .UseCompression(JsonColdStoreCompression.Brotli)
            .Build();
        var payload = Encoding.UTF8.GetBytes(new string('a', 16_000));

        var encoded = JsonColdStorePayloadCodec.Encode(payload, options);
        var decoded = JsonColdStorePayloadCodec.Decode(encoded, options);

        Assert.True(encoded.Length < payload.Length);
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void EncryptedPayloadDoesNotExposePlaintextAndRoundTrips()
    {
        var keyBytes = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        using var key = JsonColdStoreEncryptionKey.FromBytes(keyBytes);
        var options = new JsonColdStoreOptionsBuilder(TestDirectory("encrypted"))
            .UseCompression(JsonColdStoreCompression.None)
            .UseEncryption(new JsonColdStoreEncryptionOptions
            {
                Key = key,
                KeyId = "test-key",
            })
            .Build();
        var payload = Encoding.UTF8.GetBytes("""{"secret":"consumer data"}""");

        var encoded = JsonColdStorePayloadCodec.Encode(payload, options);
        var encodedText = Encoding.UTF8.GetString(encoded);
        var decoded = JsonColdStorePayloadCodec.Decode(encoded, options);

        Assert.DoesNotContain("consumer data", encodedText);
        Assert.Contains("test-key", encodedText);
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void EncryptedPayloadRejectsWrongKey()
    {
        using var correctKey = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);
        using var wrongKey = JsonColdStoreEncryptionKey.FromBytes(Enumerable.Repeat((byte)7, 32).ToArray());
        var writeOptions = new JsonColdStoreOptionsBuilder(TestDirectory("wrong-key"))
            .UseEncryptionKey(correctKey)
            .Build();
        var readOptions = new JsonColdStoreOptionsBuilder(TestDirectory("wrong-key"))
            .UseEncryptionKey(wrongKey)
            .Build();

        var encoded = JsonColdStorePayloadCodec.Encode("secret"u8, writeOptions);

        Assert.ThrowsAny<CryptographicException>(
            () => JsonColdStorePayloadCodec.Decode(encoded, readOptions));
    }

    [Fact]
    public void DecodeRejectsEnvelopeWithEncryptionMetadataButNoEncryptionFlag()
    {
        using var key = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);
        var options = new JsonColdStoreOptionsBuilder(TestDirectory("inconsistent-encryption-metadata"))
            .UseCompression(JsonColdStoreCompression.None)
            .UseEncryptionKey(key)
            .Build();

        var encoded = JsonColdStorePayloadCodec.Encode("secret"u8, options);
        encoded[5] = (byte)(encoded[5] & ~0x02);
        encoded[7] = 0;

        var exception = Assert.Throws<InvalidDataException>(
            () => JsonColdStorePayloadCodec.Decode(encoded, options));

        Assert.Contains("Encryption metadata", exception.Message);
    }

    [Fact]
    public void RequireEncryptedStoreRejectsPlainEnvelope()
    {
        using var key = JsonColdStoreEncryptionKey.FromBytes(new byte[32]);
        var plainOptions = new JsonColdStoreOptionsBuilder(TestDirectory("require-encrypted"))
            .UseCompression(JsonColdStoreCompression.None)
            .Build();
        var encryptedOptions = new JsonColdStoreOptionsBuilder(TestDirectory("require-encrypted"))
            .UseEncryption(new JsonColdStoreEncryptionOptions
            {
                Key = key,
                RequireEncryptedStore = true,
            })
            .Build();

        var encoded = JsonColdStorePayloadCodec.Encode("plain"u8, plainOptions);

        Assert.Throws<InvalidOperationException>(
            () => JsonColdStorePayloadCodec.Decode(encoded, encryptedOptions));
    }

    [Fact]
    public void ChecksumVerificationRejectsCorruptedPlainPayload()
    {
        var options = new JsonColdStoreOptionsBuilder(TestDirectory("checksum-corrupt"))
            .UseCompression(JsonColdStoreCompression.None)
            .UseChecksums(verifyOnStartup: true, verifyOnRead: true)
            .Build();
        var encoded = JsonColdStorePayloadCodec.Encode("plain payload"u8, options);

        encoded[^1] ^= 0x7F;

        var exception = Assert.Throws<InvalidDataException>(
            () => JsonColdStorePayloadCodec.Decode(encoded, options));
        Assert.Contains("checksum", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DisabledChecksumsDoNotRequireChecksumMetadata()
    {
        var options = new JsonColdStoreOptionsBuilder(TestDirectory("checksum-disabled"))
            .UseCompression(JsonColdStoreCompression.None)
            .DisableChecksums()
            .Build();
        var payload = "payload without checksum"u8.ToArray();

        var encoded = JsonColdStorePayloadCodec.Encode(payload, options);
        var decoded = JsonColdStorePayloadCodec.Decode(encoded, options);

        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void VerifyOnReadRequiresChecksumMetadata()
    {
        var writeOptions = new JsonColdStoreOptionsBuilder(TestDirectory("checksum-required"))
            .UseCompression(JsonColdStoreCompression.None)
            .DisableChecksums()
            .Build();
        var readOptions = new JsonColdStoreOptionsBuilder(TestDirectory("checksum-required"))
            .UseCompression(JsonColdStoreCompression.None)
            .UseChecksums(verifyOnStartup: true, verifyOnRead: true)
            .Build();

        var encoded = JsonColdStorePayloadCodec.Encode("payload"u8, writeOptions);

        Assert.Throws<InvalidDataException>(
            () => JsonColdStorePayloadCodec.Decode(encoded, readOptions));
    }

    [Fact]
    public void KeyedIntegrityPayloadRoundTripsWithMatchingKey()
    {
        using var integrityKey = JsonColdStoreIntegrityKey.FromBytes(
            Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray());
        var options = new JsonColdStoreOptionsBuilder(TestDirectory("integrity-keyed"))
            .UseCompression(JsonColdStoreCompression.None)
            .UseChecksums(verifyOnStartup: true, verifyOnRead: true)
            .UseIntegrityKey(integrityKey)
            .Build();
        var payload = "authenticated payload"u8.ToArray();

        var encoded = JsonColdStorePayloadCodec.Encode(payload, options);
        var decoded = JsonColdStorePayloadCodec.Decode(encoded, options);

        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void KeyedIntegrityPayloadRequiresIntegrityKeyForVerification()
    {
        using var integrityKey = JsonColdStoreIntegrityKey.FromBytes(new byte[32]);
        var writeOptions = new JsonColdStoreOptionsBuilder(TestDirectory("integrity-key-required"))
            .UseCompression(JsonColdStoreCompression.None)
            .UseChecksums(verifyOnStartup: true, verifyOnRead: true)
            .UseIntegrityKey(integrityKey)
            .Build();
        var readOptions = new JsonColdStoreOptionsBuilder(TestDirectory("integrity-key-required"))
            .UseCompression(JsonColdStoreCompression.None)
            .UseChecksums(verifyOnStartup: true, verifyOnRead: true)
            .Build();

        var encoded = JsonColdStorePayloadCodec.Encode("keyed"u8, writeOptions);

        var exception = Assert.Throws<InvalidOperationException>(
            () => JsonColdStorePayloadCodec.Decode(encoded, readOptions));

        Assert.Contains("integrity key", exception.Message);
    }

    [Fact]
    public void KeyedIntegrityPayloadRejectsWrongIntegrityKey()
    {
        using var correctKey = JsonColdStoreIntegrityKey.FromBytes(new byte[32]);
        using var wrongKey = JsonColdStoreIntegrityKey.FromBytes(Enumerable.Repeat((byte)8, 32).ToArray());
        var writeOptions = new JsonColdStoreOptionsBuilder(TestDirectory("integrity-wrong-key"))
            .UseCompression(JsonColdStoreCompression.None)
            .UseChecksums(verifyOnStartup: true, verifyOnRead: true)
            .UseIntegrityKey(correctKey)
            .Build();
        var readOptions = new JsonColdStoreOptionsBuilder(TestDirectory("integrity-wrong-key"))
            .UseCompression(JsonColdStoreCompression.None)
            .UseChecksums(verifyOnStartup: true, verifyOnRead: true)
            .UseIntegrityKey(wrongKey)
            .Build();

        var encoded = JsonColdStorePayloadCodec.Encode("keyed"u8, writeOptions);

        Assert.Throws<InvalidDataException>(
            () => JsonColdStorePayloadCodec.Decode(encoded, readOptions));
    }

    private static string TestDirectory(string name) =>
        Path.Combine(Path.GetTempPath(), "jsoncoldstore-codec-tests", name);
}
