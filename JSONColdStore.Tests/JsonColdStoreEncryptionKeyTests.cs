using JSONColdStore;

namespace JSONColdStore.Tests;

public sealed class JsonColdStoreEncryptionKeyTests
{
    [Fact]
    public void FromBytesRejectsWrongLength()
    {
        var key = new byte[31];

        var exception = Assert.Throws<ArgumentException>(
            () => JsonColdStoreEncryptionKey.FromBytes(key));

        Assert.Contains("32 bytes", exception.Message);
    }

    [Fact]
    public void FromBase64RejectsInvalidText()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => JsonColdStoreEncryptionKey.FromBase64("not base64"));

        Assert.Contains("valid base64", exception.Message);
    }

    [Fact]
    public void ToStringDoesNotExposeKeyMaterial()
    {
        var raw = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        using var key = JsonColdStoreEncryptionKey.FromBytes(raw);

        var display = key.ToString();

        Assert.DoesNotContain(Convert.ToBase64String(raw), display);
        Assert.Contains("****", display);
    }

    [Fact]
    public void CopyBytesReturnsDefensiveCopy()
    {
        var raw = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        using var key = JsonColdStoreEncryptionKey.FromBytes(raw);

        var copy = key.CopyBytes();
        copy[0] = 255;

        Assert.Equal(raw[0], key.CopyBytes()[0]);
    }
}
