using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using StructaDoc.Contracts.System;

namespace StructaDoc.Host.Tests;

public sealed class ServiceInfoEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
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
}
