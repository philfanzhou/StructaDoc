namespace StructaDoc.Application.Providers;

public static class ProviderTypes
{
    public const string MinerUCloud = "mineru-cloud";
    public const string MinerULocal = "mineru-local";

    public static bool IsKnown(string providerType)
    {
        return providerType is MinerUCloud or MinerULocal;
    }
}
