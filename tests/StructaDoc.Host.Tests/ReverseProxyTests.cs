using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ServiceMantle;
using ServiceMantle.Web;
using ServiceMantle.Web.Http;
using StructaDoc.Application.Settings;
using StructaDoc.Host.Settings;

namespace StructaDoc.Host.Tests;

/// <summary>
/// What a proxy is allowed to say about a request, and what happens to a deployment that has not
/// named one.
///
/// Every test here goes over a real socket. The whole decision is made from the peer address, and a
/// test client that never opens a connection has no peer address to be believed or refused, so an
/// in-memory host would leave the one thing this code does untested.
/// </summary>
public sealed class ReverseProxyTests
{
    private const string ForwardedScheme = "https";

    [Fact]
    public async Task A_named_proxy_decides_what_the_browser_asked_for()
    {
        await using var service = await ProbeService.StartAsync(
            new ReverseProxyOptions { TrustedProxies = "127.0.0.1, ::1" });

        // Both headers, one entry each: the trust implementation requires header-count symmetry —
        // a real proxy appends X-Forwarded-For and sets X-Forwarded-Proto together, and a request
        // that states only one of them is not applied at all.
        var request = await service.ProbeAsync(
            ("X-Forwarded-Proto", ForwardedScheme),
            ("X-Forwarded-For", "203.0.113.7"));

        // Cookies take `Secure` from this, and the sign-in redirect address given to an identity
        // provider is built from it.
        Assert.Equal(ForwardedScheme, request.Scheme);
    }

    [Fact]
    public async Task A_range_covers_the_address_the_container_actually_sees()
    {
        // The address to trust is rarely the proxy's own: a proxy on the Docker host arrives as the
        // bridge gateway, which is why naming a range has to work as well as naming an address.
        await using var service = await ProbeService.StartAsync(
            new ReverseProxyOptions { TrustedProxies = "127.0.0.0/8" });

        var request = await service.ProbeAsync(
            ("X-Forwarded-Proto", ForwardedScheme),
            ("X-Forwarded-For", "203.0.113.7"));

        Assert.Equal(ForwardedScheme, request.Scheme);
    }

    // A request that states only one of the forwarded headers is not applied at all: the trust
    // implementation requires symmetric counts, so a client cannot supply the entry a proxy left
    // unset.
    [Fact]
    public async Task An_asymmetric_forwarded_pair_is_not_applied()
    {
        await using var service = await ProbeService.StartAsync(
            new ReverseProxyOptions { TrustedProxies = "127.0.0.1" });

        var request = await service.ProbeAsync(("X-Forwarded-Proto", ForwardedScheme));

        Assert.Equal("http", request.Scheme);
        Assert.Equal("127.0.0.1", request.RemoteAddress);
    }

    [Fact]
    public async Task A_peer_that_was_not_named_cannot_decide_anything()
    {
        // Loopback is the container itself, and the trust implementation removes the framework's
        // implicit loopback trust, so a request from an unnamed peer — loopback included — is
        // refused: no implicit trust to fall back on.
        var logger = new CapturingLogger();
        await using var service = await ProbeService.StartAsync(
            new ReverseProxyOptions { TrustedProxies = "10.9.9.9" },
            logger);

        var request = await service.ProbeAsync(
            ("X-Forwarded-Proto", ForwardedScheme),
            ("X-Forwarded-For", "203.0.113.7"));

        // Otherwise anything that can reach the service directly picks its own apparent address,
        // which is the partition the sign-in rate limiter counts against.
        Assert.Equal("http", request.Scheme);
        Assert.Equal("127.0.0.1", request.RemoteAddress);
        // The refusal is still reported: the diagnostic middleware runs outside the trust decision
        // and says, once per peer, which address would have had to be trusted.
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("127.0.0.1", warning, StringComparison.Ordinal);
        Assert.Contains("ReverseProxy:TrustedProxies", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_deployment_that_names_no_proxy_is_left_exactly_as_it_was()
    {
        await using var service = await ProbeService.StartAsync(new ReverseProxyOptions());

        var request = await service.ProbeAsync(("X-Forwarded-Proto", ForwardedScheme));

        Assert.Equal("http", request.Scheme);
    }

    [Fact]
    public async Task The_caller_behind_the_proxy_becomes_the_address_the_service_counts()
    {
        await using var service = await ProbeService.StartAsync(
            new ReverseProxyOptions { TrustedProxies = "127.0.0.1" });

        var request = await service.ProbeAsync(
            ("X-Forwarded-Proto", ForwardedScheme),
            ("X-Forwarded-For", "203.0.113.7"));

        // Without this the sign-in rate limiter puts every visitor in one bucket belonging to the
        // proxy, so ten wrong passwords from anyone lock out everyone.
        Assert.Equal("203.0.113.7", request.RemoteAddress);
    }

    [Fact]
    public async Task A_forwarded_host_is_ignored_until_the_deployment_names_its_own()
    {
        await using var service = await ProbeService.StartAsync(
            new ReverseProxyOptions { TrustedProxies = "127.0.0.1" });

        var request = await service.ProbeAsync(
            ("X-Forwarded-Proto", ForwardedScheme),
            ("X-Forwarded-For", "203.0.113.7"),
            ("X-Forwarded-Host", "elsewhere.example"));

        // A proxy usually does not set this header and does pass a client's copy of it through, so
        // trusting the peer is not enough to trust the value. The host decides the sign-in redirect
        // address, and an accepted forged one sends an authorization code somewhere else. The other
        // two headers are applied, which is what shows the host alone was the one refused.
        Assert.Equal(ForwardedScheme, request.Scheme);
        Assert.Equal(service.Authority, request.Host);
    }

    [Fact]
    public async Task A_named_public_host_is_accepted_and_anything_else_is_not()
    {
        await using var service = await ProbeService.StartAsync(new ReverseProxyOptions
        {
            TrustedProxies = "127.0.0.1",
            PublicHosts = "docs.example.com",
        });

        var published = await service.ProbeAsync(
            ("X-Forwarded-Proto", ForwardedScheme),
            ("X-Forwarded-For", "203.0.113.7"),
            ("X-Forwarded-Host", "docs.example.com"));
        var forged = await service.ProbeAsync(
            ("X-Forwarded-Proto", ForwardedScheme),
            ("X-Forwarded-For", "203.0.113.7"),
            ("X-Forwarded-Host", "elsewhere.example"));

        Assert.Equal("docs.example.com", published.Host);
        Assert.Equal(service.Authority, forged.Host);
    }

    // The trust implementation matches hosts with wildcards and IDN rules rather than plain string
    // equality, so one published suffix covers its subdomains without listing them.
    [Fact]
    public async Task A_public_host_written_as_a_wildcard_covers_its_subdomains()
    {
        await using var service = await ProbeService.StartAsync(new ReverseProxyOptions
        {
            TrustedProxies = "127.0.0.1",
            PublicHosts = "*.internal.example.com",
        });

        var subdomain = await service.ProbeAsync(
            ("X-Forwarded-Proto", ForwardedScheme),
            ("X-Forwarded-For", "203.0.113.7"),
            ("X-Forwarded-Host", "workspace.internal.example.com"));
        var outside = await service.ProbeAsync(
            ("X-Forwarded-Proto", ForwardedScheme),
            ("X-Forwarded-For", "203.0.113.7"),
            ("X-Forwarded-Host", "internal.example.com"));

        Assert.Equal("workspace.internal.example.com", subdomain.Host);
        // `*.internal.example.com` is a suffix match on subdomains, not the bare host itself.
        Assert.Equal(service.Authority, outside.Host);
    }

    [Fact]
    public async Task A_refused_forwarded_header_is_reported_once_with_the_address_to_trust()
    {
        var logger = new CapturingLogger();
        await using var service = await ProbeService.StartAsync(new ReverseProxyOptions(), logger);

        await service.ProbeAsync(("X-Forwarded-Proto", ForwardedScheme));
        await service.ProbeAsync(("X-Forwarded-Proto", ForwardedScheme));

        // The address that has to be trusted is the one the container sees, and nothing outside the
        // container can read it off. Reporting it turns a deployment that fails at sign-in into one
        // line of log; saying it once keeps a misconfiguration from filling the log.
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("127.0.0.1", warning, StringComparison.Ordinal);
        Assert.Contains("ReverseProxy:TrustedProxies", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_forwarded_header_that_was_applied_is_not_reported()
    {
        var logger = new CapturingLogger();
        await using var service = await ProbeService.StartAsync(
            new ReverseProxyOptions { TrustedProxies = "127.0.0.1" },
            logger);

        await service.ProbeAsync(
            ("X-Forwarded-Proto", ForwardedScheme),
            ("X-Forwarded-For", "203.0.113.7"));

        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void An_address_that_cannot_be_read_is_refused_at_startup()
    {
        var options = new ReverseProxyOptions { TrustedProxies = "nginx" };

        // A deployment that mistyped this would otherwise start believing it had a security posture
        // it does not have.
        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("nginx", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_range_written_from_an_address_inside_it_is_the_range_that_was_meant()
    {
        // What an operator has to hand is an address a running container reported, so `172.17.0.1/16`
        // is how the range around it gets written. Refusing that would be correct and useless.
        var options = new ReverseProxyOptions { TrustedProxies = "172.17.0.1/16" };
        options.Validate();

        Assert.Equal("172.17.0.0/16", Assert.Single(options.ProxyNetworks).ToString());
    }

    [Fact]
    public void A_public_host_written_as_an_address_is_refused()
    {
        var options = new ReverseProxyOptions
        {
            TrustedProxies = "127.0.0.1",
            PublicHosts = "https://docs.example.com/",
        };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("host name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Naming_a_public_host_without_a_proxy_is_refused_rather_than_ignored()
    {
        // Nothing would read the header, so the deployment would believe it had restricted a host it
        // never accepts in the first place.
        var options = new ReverseProxyOptions { PublicHosts = "docs.example.com" };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void A_forward_limit_outside_what_a_deployment_can_have_is_refused()
    {
        var options = new ReverseProxyOptions { TrustedProxies = "127.0.0.1", ForwardLimit = 0 };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    // The trust implementation's hard limit is 10: a value from 11 to 16 that this service used to
    // accept is now refused while the configuration binds, with the configuration path in the
    // message rather than a startup failure naming no setting.
    [Theory]
    [InlineData(11)]
    [InlineData(16)]
    public void A_forward_limit_above_ten_is_refused_with_the_configuration_path(int forwardLimit)
    {
        var options = new ReverseProxyOptions
        {
            TrustedProxies = "127.0.0.1",
            ForwardLimit = forwardLimit,
        };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("ReverseProxy:ForwardLimit", error.Message, StringComparison.Ordinal);
        Assert.Contains("1 and 10", error.Message, StringComparison.Ordinal);
    }

    // Registering the same trust twice is the composition a shared code path can produce, and the
    // snapshot treats it as one registration: the host starts and the headers are applied.
    [Fact]
    public async Task An_equivalent_repeated_registration_starts_and_applies()
    {
        var options = new ReverseProxyOptions { TrustedProxies = "127.0.0.1" };

        await using var service = await ProbeService.StartAsync(
            options,
            extraRegistration: serviceMantle => serviceMantle.AddStructaDocForwardedHeaders(options));

        var request = await service.ProbeAsync(
            ("X-Forwarded-Proto", ForwardedScheme),
            ("X-Forwarded-For", "203.0.113.7"));

        Assert.Equal(ForwardedScheme, request.Scheme);
    }

    // Two registrations that disagree about the trust fail the host start with the stable
    // conflict code, rather than silently picking one of them.
    [Fact]
    public async Task A_conflicting_registration_fails_host_startup_with_a_stable_error_code()
    {
        var options = new ReverseProxyOptions { TrustedProxies = "127.0.0.1" };

        var failed = await ProbeService.TryStartAsync(
            options,
            extraRegistration: serviceMantle => serviceMantle.AddForwardedHeaders(forwarded =>
            {
                forwarded.KnownProxies = ["127.0.0.1"];
                forwarded.ForwardLimit = 2;
            }));

        Assert.NotNull(failed);
        Assert.Equal("forwarded_headers.conflicting_registration", failed.ErrorCode);
    }

    [Fact]
    public void A_reported_peer_is_written_the_way_it_would_be_configured()
    {
        // A dual-stack listener reports an IPv4 proxy in its mapped form, which is what the image
        // does. Both forms are trusted, so this is only about what an operator is asked to type.
        Assert.Equal("172.17.0.1", ReverseProxyOptions.DescribePeer(IPAddress.Parse("::ffff:172.17.0.1")));
        Assert.Equal("172.17.0.1", ReverseProxyOptions.DescribePeer(IPAddress.Parse("172.17.0.1")));
        Assert.Equal("::1", ReverseProxyOptions.DescribePeer(IPAddress.Parse("::1")));
    }

    [Fact]
    public void Nothing_that_decides_which_peer_is_trusted_is_settable_from_a_browser()
    {
        // Which peer may state what the browser asked for is a fact about the network the container
        // was placed in. An administrator reaches this service through that proxy and cannot see what
        // is in front of it, and a wrong answer lets a caller choose its own apparent address and
        // scheme, so it stays with whoever placed the container.
        Assert.DoesNotContain(
            SettingCatalog.All,
            definition => definition.Key.StartsWith(
                ReverseProxyOptions.SectionName + ":",
                StringComparison.Ordinal));
    }

    private sealed record ProbeResult(string Scheme, string Host, string? RemoteAddress);

    /// <summary>
    /// The middleware under test in front of one endpoint that reports what the rest of the pipeline
    /// would have seen, on a real loopback socket so the peer address is a real one.
    /// </summary>
    private sealed class ProbeService : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly HttpClient client = new();

        private ProbeService(WebApplication app, string authority)
        {
            this.app = app;
            Authority = authority;
        }

        /// <summary>The address the socket was opened to, which is what an unforwarded request
        /// reports as its host.</summary>
        public string Authority { get; }

        public static async Task<ProbeService> StartAsync(
            ReverseProxyOptions options,
            ILogger? logger = null,
            Action<ServiceMantleBuilder>? extraRegistration = null)
        {
            options.Validate();

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            // The composition the Host itself uses: identity, explicit trust registration, and the
            // pipeline insertion, so what is under test is the real wiring rather than the options
            // objects on their own.
            var serviceMantle = builder.Services.AddServiceMantle(
                ServiceId.Parse("structadoc"),
                InstanceId.Parse($"structadoc-probe-{Guid.NewGuid():N}"));
            serviceMantle.AddStructaDocForwardedHeaders(options);
            extraRegistration?.Invoke(serviceMantle);

            var app = builder.Build();
            app.UseStructaDocReverseProxy(options, logger ?? NullLogger.Instance);
            app.MapGet("/probe", (HttpContext context) => Results.Json(new
            {
                scheme = context.Request.Scheme,
                host = context.Request.Host.Value,
                remote = context.Connection.RemoteIpAddress?.ToString(),
            }));

            await app.StartAsync();
            var url = app.Urls.First().TrimEnd('/');
            return new ProbeService(app, new Uri(url).Authority);
        }

        /// <summary>
        /// Starts the same composition and reports the trust failure instead of the service: the
        /// registrations under test are validated when the host starts, so this is how a startup
        /// failure is observed without the test throwing first.
        /// </summary>
        public static async Task<ForwardedHeadersConfigurationException?> TryStartAsync(
            ReverseProxyOptions options,
            Action<ServiceMantleBuilder>? extraRegistration = null)
        {
            options.Validate();

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            var serviceMantle = builder.Services.AddServiceMantle(
                ServiceId.Parse("structadoc"),
                InstanceId.Parse($"structadoc-probe-{Guid.NewGuid():N}"));
            serviceMantle.AddStructaDocForwardedHeaders(options);
            extraRegistration?.Invoke(serviceMantle);

            var app = builder.Build();
            app.UseStructaDocReverseProxy(options, NullLogger.Instance);
            app.MapGet("/probe", () => Results.Ok());

            try
            {
                await app.StartAsync();
            }
            catch (ForwardedHeadersConfigurationException error)
            {
                await app.DisposeAsync();
                return error;
            }

            // A start that succeeded is not what this method is for; the caller's assertion on the
            // returned failure will report it.
            await app.StopAsync();
            await app.DisposeAsync();
            return null;
        }

        public async Task<ProbeResult> ProbeAsync(params (string Name, string Value)[] headers)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"http://{Authority}/probe");
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            return new ProbeResult(
                root.GetProperty("scheme").GetString()!,
                root.GetProperty("host").GetString()!,
                root.GetProperty("remote").GetString());
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<string> warnings = [];

        public IReadOnlyList<string> Warnings
        {
            get
            {
                lock (warnings)
                {
                    return warnings.ToArray();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Warning)
            {
                return;
            }

            lock (warnings)
            {
                warnings.Add(formatter(state, exception));
            }
        }
    }
}
