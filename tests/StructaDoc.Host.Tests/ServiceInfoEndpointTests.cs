using System.Net;
using System.Net.Http.Json;
using StructaDoc.Contracts.System;

namespace StructaDoc.Host.Tests;

public sealed class ServiceInfoEndpointTests(StructaDocWebApplicationFactory factory)
    : IClassFixture<StructaDocWebApplicationFactory>
{
    [Fact]
    public async Task Service_info_returns_product_identity()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/system/info");
        var payload = await response.Content.ReadFromJsonAsync<ServiceInfoResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        Assert.Equal("StructaDoc", payload.Name);
        Assert.False(string.IsNullOrWhiteSpace(payload.Version));
    }

    // The point of the version is that an operator can ask a deployment which build it is without
    // reaching the machine it runs on, and there are two ways to answer that while looking healthy.
    // The endpoint falls back to "unknown" when the attribute is missing, and an assembly nobody
    // stamped reports the SDK's own 1.0.0, which every such assembly reports and which therefore
    // separates no two builds. Both pass a test that only checks the field is populated.
    [Theory]
    [InlineData("unknown")]
    [InlineData("1.0.0")]
    public async Task Service_info_version_identifies_the_build(string saysNothing)
    {
        using var client = factory.CreateClient();

        var payload = await client.GetFromJsonAsync<ServiceInfoResponse>("/api/v1/system/info");

        Assert.NotNull(payload);
        Assert.NotEqual(saysNothing, payload.Version);
    }
}
