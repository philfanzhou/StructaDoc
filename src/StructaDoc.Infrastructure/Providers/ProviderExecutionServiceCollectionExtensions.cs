using System.Net;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Application.Providers;

namespace StructaDoc.Infrastructure.Providers;

public static class ProviderExecutionServiceCollectionExtensions
{
    public static IServiceCollection AddStructaDocParseProviders(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(_ => new ProviderHttpClientOwner(CreateProviderHttpClient()));
        services.AddSingleton(serviceProvider => new MinerUCloudParseProvider(
            serviceProvider.GetRequiredService<ProviderHttpClientOwner>().Client));
        services.AddSingleton(serviceProvider => new MinerULocalParseProvider(
            serviceProvider.GetRequiredService<ProviderHttpClientOwner>().Client));
        services.AddSingleton<IParseProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<MinerUCloudParseProvider>());
        services.AddSingleton<IParseProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<MinerULocalParseProvider>());
        return services;
    }

    private static HttpClient CreateProviderHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
    }

    private sealed class ProviderHttpClientOwner(HttpClient client) : IDisposable
    {
        public HttpClient Client { get; } = client;

        public void Dispose() => Client.Dispose();
    }
}
