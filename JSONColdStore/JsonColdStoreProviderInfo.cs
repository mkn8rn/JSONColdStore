using System.Reflection;

namespace JSONColdStore;

internal static class JsonColdStoreProviderInfo
{
    internal static string Version
    {
        get
        {
            var assembly = typeof(JsonColdStoreProviderInfo).Assembly;
            return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "0.0.0";
        }
    }
}
