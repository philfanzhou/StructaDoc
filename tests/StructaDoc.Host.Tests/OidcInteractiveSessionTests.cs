using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Contracts.Authentication;

namespace StructaDoc.Host.Tests;

/// <summary>
/// The interactive OIDC sign-in flow on the framework's own OpenID Connect handler — no
/// provider-specific package — against an in-process authority that answers with the discovery
/// shape a standards-compliant provider (SignaCore among them) publishes: code flow only, S256
/// PKCE, the query response mode, and the standard end-session endpoint.
/// </summary>
public sealed class OidcInteractiveSessionTests
{
    [Fact]
    public async Task Workspace_login_redirects_with_the_standard_query_code_request()
    {
        using var host = OidcHost();
        using var client = Client(host.Factory);

        var handshake = await StartSignInAsync(client, "/api/v1/session/login?returnUrl=%2F");

        Assert.Equal("code", handshake.Query["response_type"]);
        // The one encoding an authority that delivers only the standard response mode can accept:
        // no response_mode parameter at all, which is what `query` plus `code` produces. The
        // framework default (`form_post`) is what this assertion keeps from coming back.
        Assert.DoesNotContain("response_mode", handshake.Query.Keys);
        Assert.Equal("S256", handshake.Query["code_challenge_method"]);
        Assert.Equal(OidcTestAuthority.ClientId, handshake.Query["client_id"]);
        Assert.Equal("http://localhost/signin-oidc", handshake.Query["redirect_uri"]);
        // The shipped appsettings list; a deployment pins a shorter one through the same keys,
        // which the scope-binding test below holds in place.
        Assert.Equal("openid profile email", handshake.Query["scope"]);
    }

    [Fact]
    public void Configured_scopes_replace_the_documented_default_list()
    {
        // SignaCore delivers only openid and profile; a deployment removing `email` from the
        // scope list is that case. The binder appends indexed values to a non-empty default
        // array, which used to resurrect `email` in every such list, so the default is empty and
        // the effective list is what this test holds onto.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Oidc:Enabled"] = "true",
                ["Oidc:Authority"] = OidcTestAuthority.Issuer,
                ["Oidc:ClientId"] = OidcTestAuthority.ClientId,
                ["Oidc:Scopes:0"] = "openid",
                ["Oidc:Scopes:1"] = "profile",
            })
            .Build();

        var options = configuration.GetSection("Oidc")
            .Get<StructaDoc.Adapters.Authentication.OidcAuthenticationOptions>()!;

        Assert.Equal(["openid", "profile"], options.EffectiveScopes);
        options.Validate();
    }

    [Fact]
    public async Task Workspace_callback_establishes_the_interactive_session()
    {
        using var host = OidcHost();
        using var client = Client(host.Factory);
        var handshake = await StartSignInAsync(client, "/api/v1/session/login?returnUrl=%2F");

        using var callback = await CompleteSignInAsync(
            client,
            handshake,
            host.Authority.CodeForRole(handshake.Query, role: null));

        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        Assert.Equal("/", callback.Headers.Location!.OriginalString);
        Assert.Contains(
            callback.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("StructaDoc.Interactive="));

        var session = await client.GetFromJsonAsync<InteractiveSessionResponse>(
            "/api/v1/session",
            TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        Assert.True(session.Authenticated);
        Assert.Equal("user", session.SubjectType);
        Assert.Equal(OidcTestAuthority.SubjectId, session.Subject);
        Assert.Equal(OidcTestAuthority.Issuer, session.Issuer);
        Assert.False(session.IsAdministrator);

        // A workspace session without the administrator role reaches no administration endpoint.
        using var administration = await client.GetAsync(
            "/api/v1/admin/session",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, administration.StatusCode);
    }

    [Fact]
    public async Task Administration_entry_signs_in_a_subject_with_the_administrator_role()
    {
        using var host = OidcHost();
        using var client = Client(host.Factory);
        var handshake = await StartSignInAsync(
            client,
            "/api/v1/admin/session/login?returnUrl=%2Fadmin%2Fproviders");

        using var callback = await CompleteSignInAsync(
            client,
            handshake,
            host.Authority.Code(handshake.Query));

        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        Assert.Equal("/admin/providers", callback.Headers.Location!.OriginalString);
        Assert.Contains(
            callback.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("StructaDoc.Interactive="));

        var session = await client.GetFromJsonAsync<InteractiveSessionResponse>(
            "/api/v1/session",
            TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        Assert.True(session.Authenticated);
        Assert.True(session.IsAdministrator);

        // The code was redeemed with a PKCE verifier, the registered callback URI, and the client
        // secret, so the handshake — not just the response — bound the flow.
        var redemption = host.Authority.TokenForms.Last();
        Assert.Equal("http://localhost/signin-oidc", redemption["redirect_uri"]);
        Assert.False(string.IsNullOrWhiteSpace(redemption["code_verifier"]));
        Assert.Equal(OidcTestAuthority.ClientSecret, redemption["client_secret"]);

        // The administration session report is the local-account surface: an identity-provider
        // administrator holds the administrator policy but has no local account to report, so the
        // endpoint answers denial rather than failing on its external subject identifier.
        using var administration = await client.GetAsync(
            "/api/v1/admin/session",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, administration.StatusCode);
    }

    [Fact]
    public async Task Administration_entry_refuses_a_subject_without_the_administrator_role()
    {
        using var host = OidcHost();
        using var client = Client(host.Factory);
        var handshake = await StartSignInAsync(client, "/api/v1/admin/session/login");

        using var callback = await CompleteSignInAsync(
            client,
            handshake,
            host.Authority.CodeForRole(handshake.Query, role: "workspace-user"));

        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        Assert.Equal("/admin/signin?authentication=not-admin", callback.Headers.Location!.OriginalString);
        // The refusal establishes nothing: no session cookie is written.
        Assert.DoesNotContain(
            callback.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("StructaDoc.Interactive="));

        var session = await client.GetFromJsonAsync<InteractiveSessionResponse>(
            "/api/v1/session",
            TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        Assert.False(session.Authenticated);
    }

    [Fact]
    public async Task Cancelled_sign_in_lands_on_the_denied_outcome_of_its_entry()
    {
        using var host = OidcHost();
        using var client = Client(host.Factory);
        var workspace = await StartSignInAsync(client, "/api/v1/session/login");
        using var cancelled = await CompleteSignInAsync(
            client,
            workspace,
            code: null,
            error: "access_denied");

        Assert.Equal(HttpStatusCode.Found, cancelled.StatusCode);
        Assert.Equal("/signin?authentication=denied", cancelled.Headers.Location!.OriginalString);

        var administration = await StartSignInAsync(client, "/api/v1/admin/session/login");
        using var administrationCancelled = await CompleteSignInAsync(
            client,
            administration,
            code: null,
            error: "access_denied");

        Assert.Equal(HttpStatusCode.Found, administrationCancelled.StatusCode);
        Assert.Equal(
            "/admin/signin?authentication=denied",
            administrationCancelled.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Unreachable_authority_lands_on_the_unavailable_outcome()
    {
        using var host = OidcHost(discoveryFails: true);
        using var client = Client(host.Factory);

        using var response = await client.GetAsync(
            "/api/v1/admin/session/login",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(
            "/admin/signin?authentication=unavailable",
            response.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("/api/v1/session/login", null)]
    [InlineData("/api/v1/admin/session/login", OidcTestAuthority.AdministratorRole)]
    public async Task Logout_form_redirects_to_end_session_with_the_stored_id_token_hint(
        string loginPath,
        string? role)
    {
        using var host = OidcHost();
        using var client = Client(host.Factory);
        var handshake = await StartSignInAsync(client, loginPath);
        using var callback = await CompleteSignInAsync(
            client,
            handshake,
            host.Authority.CodeForRole(handshake.Query, role));
        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);

        // The SPA sends the signed-in user's antiforgery token in the form body.
        var antiforgery = await client.GetAntiforgeryTokenAsync();
        using var form = new FormUrlEncodedContent([
            KeyValuePair.Create("__RequestVerificationToken", antiforgery.RequestToken),
        ]);
        using var response = await client.PostAsync(
            "/api/v1/session/logout?redirectToHome=true", form, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal(
            OidcTestAuthority.Issuer + "/oauth2/session/end",
            location.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(location.Query);
        // The stored ID token is the hint the authority resolves the signing-out application from.
        Assert.Equal(host.Authority.LastIdToken, query["id_token_hint"].ToString());
        Assert.Equal("http://localhost/signout-callback-oidc", query["post_logout_redirect_uri"].ToString());
        Assert.False(string.IsNullOrWhiteSpace(query["state"].ToString()));
        // The local session ends on the same response the redirect is the answer of.
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("StructaDoc.Interactive=;"));

        var session = await client.GetFromJsonAsync<InteractiveSessionResponse>(
            "/api/v1/session",
            TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        Assert.False(session.Authenticated);
    }

    [Fact]
    public async Task Logout_form_returns_home_when_the_authority_has_no_end_session_endpoint()
    {
        using var host = OidcHost(hasEndSessionEndpoint: false);
        using var client = Client(host.Factory);
        var handshake = await StartSignInAsync(client, "/api/v1/admin/session/login");
        using var callback = await CompleteSignInAsync(client, handshake, host.Authority.Code(handshake.Query));
        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        var antiforgery = await client.GetAntiforgeryTokenAsync();

        using var form = new FormUrlEncodedContent([
            KeyValuePair.Create("__RequestVerificationToken", antiforgery.RequestToken),
        ]);
        using var response = await client.PostAsync(
            "/api/v1/session/logout?redirectToHome=true", form, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.OriginalString);
        var session = await client.GetFromJsonAsync<InteractiveSessionResponse>(
            "/api/v1/session", TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        Assert.False(session.Authenticated);
    }

    [Fact]
    public async Task Local_administrator_logout_without_a_form_returns_no_content()
    {
        using var host = OidcHost();
        using var client = Client(host.Factory);
        await client.LoginAsAdministratorAsync();

        using var response = await client.PostAsync(
            "/api/v1/session/logout", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var session = await client.GetFromJsonAsync<InteractiveSessionResponse>(
            "/api/v1/session", TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        Assert.False(session.Authenticated);
    }

    private sealed record OidcHostScope(
        WebApplicationFactory<Program> Factory,
        OidcTestAuthority Authority) : IDisposable
    {
        public void Dispose()
        {
            Factory.Dispose();
            Authority.Dispose();
        }
    }

    private static OidcHostScope OidcHost(bool discoveryFails = false, bool hasEndSessionEndpoint = true)
    {
        var authority = new OidcTestAuthority
        {
            DiscoveryFails = discoveryFails,
            HasEndSessionEndpoint = hasEndSessionEndpoint,
        };
        var factory = new StructaDocWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Oidc:Enabled", "true");
            builder.UseSetting("Oidc:Authority", OidcTestAuthority.Issuer);
            builder.UseSetting("Oidc:ClientId", OidcTestAuthority.ClientId);
            builder.UseSetting("Oidc:ClientSecret", OidcTestAuthority.ClientSecret);
            builder.UseSetting("Oidc:RequireHttpsMetadata", "true");
            builder.UseSetting("Oidc:Scopes:0", "openid");
            builder.UseSetting("Oidc:Scopes:1", "profile");
            // The OpenID Connect handler builds its backchannel around the options'
            // BackchannelHttpHandler, so the in-process authority becomes the endpoint the host
            // actually calls; no production network is involved.
            builder.ConfigureServices(services => services
                .AddOptions<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>(
                    StructaDoc.Host.Authentication.AuthenticationSchemes.OpenIdConnect)
                .Configure(options => options.BackchannelHttpHandler = authority));
        });
        return new OidcHostScope(factory, authority);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(
        new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

    private sealed record Handshake(Uri Authorize, Dictionary<string, string> Query);

    private static async Task<Handshake> StartSignInAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var authorize = response.Headers.Location!;
        Assert.Equal(
            OidcTestAuthority.Issuer + "/oauth2/authorize",
            authorize.GetLeftPart(UriPartial.Path));
        return new Handshake(
            authorize,
            QueryHelpers.ParseQuery(authorize.Query)
                .ToDictionary(pair => pair.Key, pair => pair.Value.ToString()));
    }

    private static Task<HttpResponseMessage> CompleteSignInAsync(
        HttpClient client,
        Handshake handshake,
        string? code,
        string? error = null)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["state"] = handshake.Query["state"],
            ["iss"] = OidcTestAuthority.Issuer,
        };
        if (code is not null)
        {
            parameters["code"] = code;
        }
        else
        {
            parameters["error"] = error;
        }

        return client.GetAsync(
            QueryHelpers.AddQueryString("/signin-oidc", parameters),
            TestContext.Current.CancellationToken);
    }
}
