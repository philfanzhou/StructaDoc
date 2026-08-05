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

        services.AddSingleton(_ => new ProviderHttpClientOwner(
            CreateProviderApiHttpClient(),
            CreateSignedTransferHttpClient()));
        services.AddSingleton(serviceProvider => new MinerUCloudParseProvider(
            serviceProvider.GetRequiredService<ProviderHttpClientOwner>().ProviderApiClient,
            serviceProvider.GetRequiredService<ProviderHttpClientOwner>().SignedTransferClient));
        services.AddSingleton(serviceProvider => new MinerULocalParseProvider(
            serviceProvider.GetRequiredService<ProviderHttpClientOwner>().ProviderApiClient));
        services.AddSingleton<IParseProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<MinerUCloudParseProvider>());
        services.AddSingleton<IParseProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<MinerULocalParseProvider>());
        return services;
    }

    private static HttpClient CreateProviderApiHttpClient()
    {
        return CreateHttpClient(connectCallback: null);
    }

    private static HttpClient CreateSignedTransferHttpClient()
    {
        return CreateHttpClient(SignedTransferDestinationPolicy.ConnectAsync);
    }

    private static HttpClient CreateHttpClient(
        Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? connectCallback)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            UseProxy = connectCallback is null,
            ConnectCallback = connectCallback,
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
    }

    private sealed class ProviderHttpClientOwner(
        HttpClient providerApiClient,
        HttpClient signedTransferClient) : IDisposable
    {
        public HttpClient ProviderApiClient { get; } = providerApiClient;

        public HttpClient SignedTransferClient { get; } = signedTransferClient;

        public void Dispose()
        {
            ProviderApiClient.Dispose();
            SignedTransferClient.Dispose();
        }
    }
}
