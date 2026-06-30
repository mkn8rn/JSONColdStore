using EFC.JSONColdStore;

namespace EFC.JSONColdStore.Tests;

public sealed class JsonColdStoreIntegrityKeyTests
{
    [Fact]
    public void FromBytesRejectsWrongLength()
    {
        var key = new byte[31];

        var exception = Assert.Throws<ArgumentException>(
            () => JsonColdStoreIntegrityKey.FromBytes(key));

        Assert.Contains("32 bytes", exception.Message);
    }

    [Fact]
    public void FromBase64RejectsInvalidText()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => JsonColdStoreIntegrityKey.FromBase64("not base64"));

        Assert.Contains("valid base64", exception.Message);
    }

    [Fact]
    public void ToStringDoesNotExposeKeyMaterial()
    {
        var raw = Enumerable.Range(0, 32).Select(i => (byte)(255 - i)).ToArray();
        using var key = JsonColdStoreIntegrityKey.FromBytes(raw);

        var display = key.ToString();

        Assert.DoesNotContain(Convert.ToBase64String(raw), display);
        Assert.Contains("****", display);
    }

    [Fact]
    public void CopyBytesReturnsDefensiveCopy()
    {
        var raw = Enumerable.Range(0, 32).Select(i => (byte)(255 - i)).ToArray();
        using var key = JsonColdStoreIntegrityKey.FromBytes(raw);

        var copy = key.CopyBytes();
        copy[0] = 0;

        Assert.Equal(raw[0], key.CopyBytes()[0]);
    }
}
