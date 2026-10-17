using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using StructaDoc.Application.Authentication;

namespace StructaDoc.Host.Settings;

/// <summary>
/// The fixed limits of the connection-test policy. They are registered in code rather than made
/// settable because the budget the policy protects is the service's own outbound requests, not a
/// tuning an administrator is better placed to choose: twenty attempts a minute is far beyond
/// clicking a test button while adjusting a configuration, and far below the frequency a probe of
/// internal addresses needs. The values sit inside the range ServiceMantle validates for its
/// consumer policies, in the same sliding-window shape the sign-in limits use.
/// </summary>
public sealed class ConnectionTestRateLimitOptions
{
    public int PermitLimit { get; set; } = 20;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

    public int SegmentsPerWindow { get; set; } = 6;

    public void Validate()
    {
        if (PermitLimit is < 1 or > 10000)
        {
            throw new InvalidOperationException(
                "The connection-test permit limit must be between 1 and 10000.");
        }

        if (Window < TimeSpan.FromSeconds(1) || Window > TimeSpan.FromMinutes(10))
        {
            throw new InvalidOperationException(
                "The connection-test window must be between one second and ten minutes.");
        }

        if (SegmentsPerWindow is < 1 or > 60
            || SegmentsPerWindow > Window.TotalSeconds)
        {
            throw new InvalidOperationException(
                "The connection-test window needs one to sixty segments, each at least one second.");
        }
    }
}

/// <summary>
/// The rate-limit policy shared by the three settings connection tests — identity-provider
/// discovery, the storage probe, and the database probe — which are the settings endpoints that
/// make the service reach an address the caller chose.
///
/// The policy is registered locally because neither ServiceMantle policy can count by StructaDoc
/// administrator: <c>servicemantle.management</c> partitions by the library's own operator
/// identity, which a StructaDoc principal never carries, and consumer policies partition by client
/// address alone. Registering on the same <see cref="RateLimiterOptions"/> the library configured
/// is what keeps a refusal identical to sign-in's: the shared <c>OnRejected</c> answers with the
/// library's safe <c>application/problem+json</c> body — the rule that fired, a correlation id,
/// and nothing about the address probed or the caller — and logs the warning that is the visible
/// trace a rejection leaves.
/// </summary>
public static class ConnectionTestRateLimiting
{
    /// <summary>
    /// Below the library's reserved <c>servicemantle.</c> prefix, in the naming grammar its policy
    /// names follow, so the two registrations cannot collide.
    /// </summary>
    public const string PolicyName = "structadoc.connection-test";

    /// <summary>
    /// Registers the policy and its fixed limits. Call after the ServiceMantle rate-limiting
    /// registration: the order does not decide anything — this registration never touches
    /// <c>OnRejected</c> — but keeping the two together keeps the pipeline legible.
    /// </summary>
    public static IServiceCollection AddConnectionTestRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ConnectionTestRateLimitOptions();
        options.Validate();
        services.AddSingleton(options);
        services.AddRateLimiter(rateLimiterOptions =>
            rateLimiterOptions.AddPolicy(PolicyName, Partition));
        return services;
    }

    internal static RateLimitPartition<string> Partition(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<ConnectionTestRateLimitOptions>();
        return RateLimitPartition.GetSlidingWindowLimiter(
            PartitionKey(context),
            _ => new SlidingWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = options.PermitLimit,
                Window = options.Window,
                SegmentsPerWindow = options.SegmentsPerWindow,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            });
    }

    private static string PartitionKey(HttpContext context) =>
        ActorPartitionKey(context.User)
        ?? AddressPartitionKey(context.Connection.RemoteIpAddress);

    /// <summary>
    /// The partition of an authenticated caller: the stable (issuer, subject) identity the service
    /// already persists for principals, hashed the way the library hashes its operators so the raw
    /// identity never floats inside the limiter. Two sessions of one account share one bucket,
    /// which is the point — the account is what the endpoints trust.
    /// </summary>
    private static string? ActorPartitionKey(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        try
        {
            var actor = CanonicalActor.FromPrincipal(principal);
            return "connection-test-actor:"
                + Convert.ToHexStringLower(SHA256.HashData(
                    Encoding.UTF8.GetBytes(actor.Issuer + "\0" + actor.Subject)));
        }
        catch (InvalidOperationException)
        {
            // A principal whose claims do not form a canonical actor is not one to fail a request
            // over; the address partition still bounds it, and authorization decides the rest.
            return null;
        }
    }

    private static string AddressPartitionKey(IPAddress? address)
    {
        if (address is null)
        {
            return "connection-test-unknown-client";
        }

        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return "connection-test-client:" + normalized;
    }
}
