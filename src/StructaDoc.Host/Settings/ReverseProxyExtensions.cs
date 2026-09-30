using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using ServiceMantle.Web;
using ServiceMantle.Web.Http;

namespace StructaDoc.Host.Settings;

public static class ReverseProxyExtensions
{
    /// <summary>
    /// Registers the trusted-proxy configuration with ServiceMantle, which runs the forwarded
    /// headers through an explicit trust snapshot: the named addresses and ranges, the published
    /// hosts, and the forward limit are validated when the host starts — an unusable value fails
    /// startup rather than becoming a proxy that silently does nothing.
    ///
    /// A deployment that names no proxy registers nothing at all, and is left exactly as it was:
    /// no header is read, and a service published directly cannot be told by a caller that it is
    /// somewhere else.
    /// </summary>
    public static ServiceMantleBuilder AddStructaDocForwardedHeaders(
        this ServiceMantleBuilder serviceMantle,
        ReverseProxyOptions options)
    {
        ArgumentNullException.ThrowIfNull(serviceMantle);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.IsEnabled)
        {
            return serviceMantle;
        }

        return serviceMantle.AddForwardedHeaders(forwarded =>
        {
            // The library parses, deduplicates, and normalizes these into its own snapshot; what it
            // hands the framework is its own ForwardedHeadersOptions with the implicit loopback
            // trust removed. Host trust stays conditional on PublicHosts, exactly as before.
            forwarded.KnownProxies = options.ProxyAddresses.Select(address => address.ToString());
            forwarded.KnownIPNetworks = options.ProxyNetworks.Select(network => network.ToString());
            forwarded.AllowedHosts = options.HostNames;
            forwarded.ForwardLimit = options.ForwardLimit;
        });
    }

    /// <summary>
    /// The peers named in <see cref="ReverseProxyOptions.TrustedProxies"/> get to say what the
    /// browser asked for. This has to run before anything reads the scheme, the host, or the caller's
    /// address, which means before the rate limiter and before authentication.
    ///
    /// A deployment that names no proxy is left exactly as it was: no header is read, and a service
    /// published directly cannot be told by a caller that it is somewhere else.
    /// </summary>
    public static IApplicationBuilder UseStructaDocReverseProxy(
        this IApplicationBuilder app,
        ReverseProxyOptions options,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        // Reported whether or not a proxy is trusted, because the two cases an operator has to tell
        // apart are "the header never arrived" and "it arrived and was refused".
        app.Use(ReportRefusedForwardedHeaders(logger));

        if (!options.IsEnabled)
        {
            return app;
        }

        return app.UseServiceMantleForwardedHeaders();
    }

    /// <summary>
    /// Says once, per peer, that a forwarded scheme arrived and was not applied.
    ///
    /// Getting this wrong looks like a working deployment until sign-in fails, and the address that
    /// has to be trusted is the one the container sees rather than the one the proxy has: a proxy on
    /// the Docker host arrives as the bridge gateway. Nothing outside the container can read that
    /// address off, so the service reports it rather than leaving it to be guessed.
    ///
    /// The check is the outcome rather than the rule: a header the middleware consumed is removed
    /// from the request, so one that survives to here was refused, ignored, or beyond the forward
    /// limit. Peers are remembered so a misconfiguration costs a few lines rather than one per
    /// request, and the set is bounded so a caller cannot fill a log by varying its address.
    /// </summary>
    private static Func<HttpContext, RequestDelegate, Task> ReportRefusedForwardedHeaders(ILogger logger)
    {
        const int reportedPeerLimit = 8;
        var reportedPeers = new ConcurrentDictionary<string, byte>();

        return async (context, next) =>
        {
            await next(context);

            if (!TryReadLastEntry(context.Request.Headers["X-Forwarded-Proto"], out var scheme)
                || string.Equals(context.Request.Scheme, scheme, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var peer = ReverseProxyOptions.DescribePeer(context.Connection.RemoteIpAddress);
            if (reportedPeers.Count >= reportedPeerLimit || !reportedPeers.TryAdd(peer, 0))
            {
                return;
            }

            logger.LogWarning(
                "A request from {Peer} carried X-Forwarded-Proto: {ForwardedScheme}, which was not applied, so the service treated the request as {Scheme}. Add {Peer} to {Setting} if that peer is the reverse proxy in front of this deployment.",
                peer,
                scheme,
                context.Request.Scheme,
                peer,
                $"{ReverseProxyOptions.SectionName}:{nameof(ReverseProxyOptions.TrustedProxies)}");
        };
    }

    /// <summary>
    /// The nearest proxy's entry, which is the one that would have been applied. A forwarded header
    /// may arrive as several header lines or as one comma-separated line, and the entries run from
    /// the original client to the last hop.
    /// </summary>
    private static bool TryReadLastEntry(StringValues header, out string entry)
    {
        for (var line = header.Count - 1; line >= 0; line--)
        {
            var values = header[line];
            if (string.IsNullOrEmpty(values))
            {
                continue;
            }

            var separator = values.LastIndexOf(',');
            var candidate = separator < 0 ? values : values[(separator + 1)..];
            candidate = candidate.Trim();
            if (candidate.Length > 0)
            {
                entry = candidate;
                return true;
            }
        }

        entry = string.Empty;
        return false;
    }
}
