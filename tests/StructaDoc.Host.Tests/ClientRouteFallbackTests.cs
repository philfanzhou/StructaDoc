using System.Net;

namespace StructaDoc.Host.Tests;

public sealed class ClientRouteFallbackTests(StructaDocWebApplicationFactory factory)
    : IClassFixture<StructaDocWebApplicationFactory>
{
    // The workspace and the administration area are client-side routes of one SPA, so the Host
    // answers unmatched navigation paths with the application shell. That fallback must not
    // swallow API and health paths: a mistyped route has to fail as an API call.
    [Theory]
    [InlineData("/api/v1/does-not-exist")]
    [InlineData("/api/v1/documents/not-a-route/extra")]
    [InlineData("/health/does-not-exist")]
    public async Task Unmatched_service_paths_fail_as_api_calls(string path)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Mapped_service_paths_still_reach_their_endpoint()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/system/info");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
