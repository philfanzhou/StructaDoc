using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace StructaDoc.Host.Tests;

/// <summary>
/// An identity provider only far enough to serve a discovery document, over a real socket. The probe
/// under test exists to make a network call, so replacing that call with a stub would leave the part
/// that fails in deployments untested.
/// </summary>
internal sealed class StubIdentityProvider : IAsyncDisposable
{
    private readonly WebApplication app;

    private StubIdentityProvider(WebApplication app, string authority)
    {
        this.app = app;
        Authority = authority;
    }

    public string Authority { get; }

    /// <param name="issuer">
    /// What the document claims to be, which is the authority itself unless a test needs them to
    /// disagree.
    /// </param>
    /// <param name="document">A complete replacement body, for testing what is not a provider.</param>
    public static async Task<StubIdentityProvider> StartAsync(
        string? issuer = null,
        string? document = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();

        // The document names the address it is served from, which is only known once the server has
        // bound a port. Routes cannot be added after the server starts, so the handler reads a value
        // filled in below rather than one captured now.
        var body = string.Empty;
        app.MapGet(
            "/.well-known/openid-configuration",
            () => Results.Text(body, "application/json"));

        await app.StartAsync();

        var authority = app.Urls.First().TrimEnd('/');
        body = document ?? JsonSerializer.Serialize(new
        {
            issuer = issuer ?? authority,
            authorization_endpoint = authority + "/authorize",
            token_endpoint = authority + "/token",
            jwks_uri = authority + "/jwks",
        });

        return new StubIdentityProvider(app, authority);
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
