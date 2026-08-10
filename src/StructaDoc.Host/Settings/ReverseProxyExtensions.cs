using System.Collections.Concurrent;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Primitives;

namespace StructaDoc.Host.Settings;

public static class ReverseProxyExtensions
{
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

        var forwarded = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = options.ForwardLimit,
        };

        // The framework trusts loopback out of the box. That is a reasonable default for a service
        // started by hand and the wrong one here: in a container, loopback is the container itself,
        // so the only effect would be to believe anything that reached it from inside.
        forwarded.KnownIPNetworks.Clear();
        forwarded.KnownProxies.Clear();

        foreach (var address in options.ProxyAddresses)
        {
            forwarded.KnownProxies.Add(address);
        }

        foreach (var network in options.ProxyNetworks)
        {
            forwarded.KnownIPNetworks.Add(network);
        }

        if (options.HostNames.Count > 0)
        {
            forwarded.ForwardedHeaders |= ForwardedHeaders.XForwardedHost;
            foreach (var host in options.HostNames)
            {
                forwarded.AllowedHosts.Add(host);
            }
        }

        return app.UseForwardedHeaders(forwarded);
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
