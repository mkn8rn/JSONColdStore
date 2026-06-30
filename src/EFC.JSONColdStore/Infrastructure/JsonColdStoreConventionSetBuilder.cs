using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;

namespace EFC.JSONColdStore.Infrastructure;

internal sealed class JsonColdStoreConventionSetBuilder(
    ProviderConventionSetBuilderDependencies dependencies)
    : ProviderConventionSetBuilder(dependencies)
{
    public override ConventionSet CreateConventionSet() => base.CreateConventionSet();
}
