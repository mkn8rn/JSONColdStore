using Microsoft.EntityFrameworkCore.Storage;

namespace EFC.JSONColdStore.Infrastructure;

internal sealed class JsonColdStoreTypeMappingSource(TypeMappingSourceDependencies dependencies)
    : TypeMappingSource(dependencies)
{
    protected override CoreTypeMapping? FindMapping(in TypeMappingInfo mappingInfo)
    {
        var clrType = mappingInfo.ClrType;
        if (clrType is null)
            return null;

        var jsonValueReaderWriter = Dependencies.JsonValueReaderWriterSource.FindReaderWriter(clrType);
        return new JsonColdStoreTypeMapping(clrType, jsonValueReaderWriter: jsonValueReaderWriter);
    }
}
