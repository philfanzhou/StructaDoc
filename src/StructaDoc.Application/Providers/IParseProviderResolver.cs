namespace StructaDoc.Application.Providers;

public interface IParseProviderResolver
{
    IParseProvider? Resolve(string providerType);
}

public sealed class ParseProviderResolver : IParseProviderResolver
{
    private readonly IReadOnlyDictionary<string, IParseProvider> providers;

    public ParseProviderResolver(IEnumerable<IParseProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var resolvedProviders = new Dictionary<string, IParseProvider>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            ArgumentException.ThrowIfNullOrWhiteSpace(provider.ProviderType);

            if (!resolvedProviders.TryAdd(provider.ProviderType, provider))
            {
                throw new InvalidOperationException(
                    $"More than one parser Provider is registered for type '{provider.ProviderType}'.");
            }
        }

        this.providers = resolvedProviders;
    }

    public IParseProvider? Resolve(string providerType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerType);
        return providers.GetValueOrDefault(providerType);
    }
}
