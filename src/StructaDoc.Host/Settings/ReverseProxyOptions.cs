using System.Net;

namespace StructaDoc.Host.Settings;

/// <summary>
/// Which peer is allowed to say what the browser actually asked for.
///
/// Behind a proxy that terminates TLS, every request reaches the Host as plain HTTP from the proxy's
/// address. Left at that, the service is wrong about three things at once: session cookies are issued
/// without <c>Secure</c> because the request it can see is not secure, the sign-in redirect address
/// composed for an identity provider says <c>http</c> and no longer matches what was registered, and
/// the sign-in rate limiter partitions every visitor into one bucket belonging to the proxy. The
/// proxy states the truth in <c>X-Forwarded-Proto</c>, <c>X-Forwarded-Host</c>, and
/// <c>X-Forwarded-For</c>; the only question is who is allowed to state it.
///
/// A forwarded header is a claim by whoever sent it, so nothing is trusted until a deployment names
/// the address it trusts. That address is a fact about the network the container was placed in, which
/// is why this is configuration rather than a stored setting an administrator could write from a
/// browser: an administrator reaching the service through the proxy cannot see what is in front of
/// it, and a wrong answer here lets a caller choose its own apparent address and scheme.
/// </summary>
public sealed class ReverseProxyOptions
{
    public const string SectionName = "ReverseProxy";

    /// <summary>
    /// Addresses and CIDR ranges whose forwarded headers are believed, separated by commas or
    /// whitespace, as in <c>172.17.0.1</c> or <c>10.0.0.0/8, ::1</c>.
    ///
    /// One string rather than an array because the deployments that need it set it once on
    /// <c>docker run</c>, where <c>ReverseProxy__TrustedProxies=10.0.0.0/8</c> is an argument a person
    /// can write and read back, and an array is index-numbered environment variables.
    ///
    /// The address to name is the one the container sees, which is rarely the one the proxy has: a
    /// proxy on the Docker host arrives as the bridge gateway, and a proxy in another container
    /// arrives as its address on the shared network. The service reports the address it saw when a
    /// forwarded header arrives from a peer it does not trust, so this does not have to be guessed
    /// twice.
    /// </summary>
    public string TrustedProxies { get; init; } = string.Empty;

    /// <summary>
    /// The host names the deployment is published under. Empty means <c>X-Forwarded-Host</c> is
    /// ignored entirely, which is the default.
    ///
    /// Host is separated from the rest because it is the one forwarded value a proxy usually does not
    /// set and usually does pass through: a client that sends <c>X-Forwarded-Host</c> to a typical
    /// proxy has it relayed unchanged, so trusting the peer is not enough to trust this header. The
    /// host decides the sign-in redirect address composed for an identity provider, so an accepted
    /// forged one sends an authorization code somewhere else. Naming the published hosts costs one
    /// setting and closes that.
    /// </summary>
    public string PublicHosts { get; init; } = string.Empty;

    /// <summary>
    /// How many proxies stand in front of the service. Each consumes one entry of each forwarded
    /// header, so a service behind a CDN and an ingress needs 2, and a value larger than the number
    /// of proxies lets the client supply the entry the last one did not.
    /// </summary>
    public int ForwardLimit { get; init; } = 1;

    /// <summary>Whether any peer is trusted at all. Nothing is forwarded when this is false.</summary>
    public bool IsEnabled => ProxyAddresses.Count > 0 || ProxyNetworks.Count > 0;

    public IReadOnlyList<IPAddress> ProxyAddresses { get; private set; } = [];

    public IReadOnlyList<IPNetwork> ProxyNetworks { get; private set; } = [];

    public IReadOnlyList<string> HostNames { get; private set; } = [];

    /// <summary>
    /// Parses the two lists and rejects anything that would otherwise be discovered as a proxy that
    /// silently does nothing. This runs at startup rather than lazily because an unusable value here
    /// is a security posture the deployment thinks it has.
    /// </summary>
    public void Validate()
    {
        var addresses = new List<IPAddress>();
        var networks = new List<IPNetwork>();

        foreach (var entry in Split(TrustedProxies))
        {
            if (entry.Contains('/', StringComparison.Ordinal))
            {
                networks.Add(ParseNetwork(entry));
                continue;
            }

            if (!IPAddress.TryParse(entry, out var address))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:{nameof(TrustedProxies)} contains '{entry}', which is neither an IP address nor a CIDR range.");
            }

            addresses.Add(address);
        }

        var hosts = new List<string>();
        foreach (var entry in Split(PublicHosts))
        {
            // A host name, not an address: a value carrying a scheme or a path is a URL somebody
            // pasted, and it would never match the header it is compared against.
            if (entry.Contains('/', StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:{nameof(PublicHosts)} contains '{entry}', which is a URL rather than a host name.");
            }

            hosts.Add(entry);
        }

        if (hosts.Count > 0 && addresses.Count == 0 && networks.Count == 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(PublicHosts)} is set without {SectionName}:{nameof(TrustedProxies)}, so no forwarded header would be read at all.");
        }

        if (ForwardLimit is < 1 or > 16)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(ForwardLimit)} must be between 1 and 16.");
        }

        ProxyAddresses = addresses;
        ProxyNetworks = networks;
        HostNames = hosts;
    }

    /// <summary>
    /// How a peer would be written into <see cref="TrustedProxies"/>, which is not always how the
    /// socket reports it: a dual-stack listener reports an IPv4 client in its IPv6-mapped form, and
    /// <c>::ffff:172.17.0.1</c> is a needlessly alarming thing to hand somebody as the address of
    /// their own reverse proxy. Both forms are matched, so this only decides what is said.
    /// </summary>
    public static string DescribePeer(IPAddress? address)
    {
        return address switch
        {
            null => "an unknown address",
            { IsIPv4MappedToIPv6: true } => address.MapToIPv4().ToString(),
            _ => address.ToString(),
        };
    }

    private static IPNetwork ParseNetwork(string entry)
    {
        // A range written from an address that sits inside it — `172.17.0.1/16` for the range an
        // operator read off a running container — parses to the range that was meant rather than
        // being refused, which is the reading that makes the setting usable from what Docker prints.
        if (IPNetwork.TryParse(entry, out var network))
        {
            return network;
        }

        throw new InvalidOperationException(
            $"{SectionName}:{nameof(TrustedProxies)} contains '{entry}', which is not a CIDR range.");
    }

    private static IEnumerable<string> Split(string value)
    {
        return value.Split(
            [',', ';', ' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
