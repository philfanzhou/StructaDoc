using System.Net;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Application.Providers;

namespace StructaDoc.Adapters.Providers;

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

    /// <summary>
    /// Registers the layout-detection execution contract for the bound <paramref name="options"/>.
    ///
    /// With no runtime configured — the shipped state — no <see cref="ILayoutDetectionProvider"/>
    /// implementation is registered at all, which is what makes the capability resolution answer
    /// "unsupported" instead of the service guessing an endpoint. With a runtime configured, the
    /// adapter gets its own <see cref="HttpClient"/>: no proxy, no redirects, and the configured
    /// request timeout as the client's own deadline, so an expired bound surfaces as the transient
    /// timeout failure rather than the 10-minute ceiling the full-parsing clients share.
    /// </summary>
    public static IServiceCollection AddStructaDocLayoutDetection(
        this IServiceCollection services,
        LayoutRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var runtime = options.ToRuntimeConfiguration();
        // The bound configuration is registered for the endpoint that resolves per request: the
        // public layout route reads it and hands it to the capability resolution, so what the
        // endpoint resolves with is exactly what was bound here, restart to restart.
        services.AddSingleton(runtime);
        if (runtime.BaseUri is null)
        {
            services.AddSingleton<ILayoutDetectionProviderResolver>(_ =>
                new LayoutDetectionProviderResolver(Enumerable.Empty<ILayoutDetectionProvider>()));
            return services;
        }

        var layoutRuntimeClient = CreateHttpClient(connectCallback: null);
        layoutRuntimeClient.Timeout = runtime.RequestTimeout;
        services.AddSingleton(_ => new LayoutRuntimeHttpClientOwner(layoutRuntimeClient));
        services.AddSingleton<ILayoutDetectionProvider>(serviceProvider =>
            new MinerULayoutDetectionProvider(
                serviceProvider.GetRequiredService<LayoutRuntimeHttpClientOwner>().Client,
                runtime));
        services.AddSingleton<ILayoutDetectionProviderResolver>(serviceProvider =>
            new LayoutDetectionProviderResolver(
                serviceProvider.GetServices<ILayoutDetectionProvider>()));
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

    private sealed class LayoutRuntimeHttpClientOwner(HttpClient client) : IDisposable
    {
        public HttpClient Client { get; } = client;

        public void Dispose()
        {
            Client.Dispose();
        }
    }
}
