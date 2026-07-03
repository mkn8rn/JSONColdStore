using JSONColdStore.Storage;

namespace JSONColdStore.Tests;

public sealed class JsonColdStoreLegacyRecordNamesTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("10000000-0000-0000-0000-000000000001")]
    [InlineData("record-name")]
    public void IsSafeRecordIdAcceptsPlainFileNames(string recordId)
    {
        Assert.True(JsonColdStoreLegacyRecordNames.IsSafeRecordId(recordId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("_metadata")]
    [InlineData("child/name")]
    [InlineData("child\\name")]
    [InlineData("record?.json")]
    public void IsSafeRecordIdRejectsUnsafeFileNames(string recordId)
    {
        Assert.False(JsonColdStoreLegacyRecordNames.IsSafeRecordId(recordId));
    }

    [Fact]
    public void TryGetRecordIdFromFileNameReturnsSafeStem()
    {
        var ok = JsonColdStoreLegacyRecordNames.TryGetRecordIdFromFileName(
            "10000000-0000-0000-0000-000000000001.json",
            out var recordId);

        Assert.True(ok);
        Assert.Equal("10000000-0000-0000-0000-000000000001", recordId);
    }

    [Theory]
    [InlineData(".json")]
    [InlineData("_index_Value.json")]
    [InlineData("_store.json")]
    [InlineData("../record.json")]
    [InlineData("record?.json")]
    public void TryGetRecordIdFromFileNameRejectsUnsafeNames(string fileName)
    {
        var ok = JsonColdStoreLegacyRecordNames.TryGetRecordIdFromFileName(fileName, out var recordId);

        Assert.False(ok);
        Assert.Equal(string.Empty, recordId);
    }
}
