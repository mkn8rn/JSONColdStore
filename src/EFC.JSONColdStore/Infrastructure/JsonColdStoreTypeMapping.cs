using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace EFC.JSONColdStore.Infrastructure;

internal sealed class JsonColdStoreTypeMapping : CoreTypeMapping
{
    internal JsonColdStoreTypeMapping(
        Type clrType,
        ValueComparer? comparer = null,
        ValueComparer? keyComparer = null,
        JsonValueReaderWriter? jsonValueReaderWriter = null)
        : base(new CoreTypeMappingParameters(
            clrType,
            converter: null,
            comparer,
            keyComparer,
            jsonValueReaderWriter: jsonValueReaderWriter))
    {
    }

    private JsonColdStoreTypeMapping(CoreTypeMappingParameters parameters)
        : base(parameters)
    {
    }

    public override CoreTypeMapping WithComposedConverter(
        ValueConverter? converter,
        ValueComparer? comparer = null,
        ValueComparer? keyComparer = null,
        CoreTypeMapping? elementMapping = null,
        JsonValueReaderWriter? jsonValueReaderWriter = null)
        => new JsonColdStoreTypeMapping(
            Parameters.WithComposedConverter(
                converter,
                comparer,
                keyComparer,
                elementMapping,
                jsonValueReaderWriter));

    protected override CoreTypeMapping Clone(CoreTypeMappingParameters parameters) =>
        new JsonColdStoreTypeMapping(parameters);
}
