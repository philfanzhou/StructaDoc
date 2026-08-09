using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Contracts.Authentication;
using StructaDoc.Platform.ControlPlane;

namespace StructaDoc.Host.Tests;

public sealed class AdministratorSessionEndpointTests(StructaDocWebApplicationFactory factory)
    : IClassFixture<StructaDocWebApplicationFactory>
{
    [Fact]
    public async Task Login_requires_antiforgery_token()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/session",
            new AdministratorLoginRequest(
                StructaDocWebApplicationFactory.AdministratorUsername,
                StructaDocWebApplicationFactory.AdministratorPassword));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_password_returns_generic_unauthorized_response()
    {
        using var client = factory.CreateClient();
        var token = await client.GetAntiforgeryTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/session")
        {
            Content = JsonContent.Create(new AdministratorLoginRequest(
                StructaDocWebApplicationFactory.AdministratorUsername,
                "not-the-password")),
        };
        request.Headers.Add(token.HeaderName, token.RequestToken);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_is_rate_limited_by_remote_address()
    {
        using var limitedFactory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Authentication:LoginPermitLimit", "2"));
        using var client = limitedFactory.CreateClient();
        var token = await client.GetAntiforgeryTokenAsync();

        async Task<HttpStatusCode> AttemptAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/session")
            {
                Content = JsonContent.Create(new AdministratorLoginRequest(
                    StructaDocWebApplicationFactory.AdministratorUsername,
                    "not-the-password")),
            };
            request.Headers.Add(token.HeaderName, token.RequestToken);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await AttemptAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, await AttemptAsync());
        Assert.Equal(HttpStatusCode.TooManyRequests, await AttemptAsync());
    }

    [Fact]
    public async Task Login_session_and_logout_use_cookie_with_antiforgery()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();

        using var sessionResponse = await client.GetAsync("/api/v1/admin/session");
        var session = await sessionResponse.Content
            .ReadFromJsonAsync<AdministratorSessionResponse>();

        Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);
        Assert.NotNull(session);
        Assert.Equal(StructaDocWebApplicationFactory.AdministratorUsername, session.Username);

        using var logoutResponse = await client.DeleteAsync("/api/v1/admin/session");
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        using var afterLogout = await client.GetAsync("/api/v1/admin/session");
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public async Task Security_stamp_change_revokes_existing_cookie()
    {
        using var isolatedFactory = new StructaDocWebApplicationFactory();
        using var client = isolatedFactory.CreateClient();
        await client.LoginAsAdministratorAsync();
        Guid originalStamp;

        await using (var scope = isolatedFactory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var administrator = await dbContext.AdminUsers.SingleAsync();
            originalStamp = administrator.SecurityStamp;
            administrator.SecurityStamp = Guid.NewGuid();
            await dbContext.SaveChangesAsync();
        }

        try
        {
            using var response = await client.GetAsync("/api/v1/admin/session");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            await using var scope = isolatedFactory.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var administrator = await dbContext.AdminUsers.SingleAsync();
            administrator.SecurityStamp = originalStamp;
            await dbContext.SaveChangesAsync();
        }
    }
}
