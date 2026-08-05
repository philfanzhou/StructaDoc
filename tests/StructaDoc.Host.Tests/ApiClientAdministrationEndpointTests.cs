using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using StructaDoc.Application.Authentication;
using StructaDoc.Contracts.Authentication;

namespace StructaDoc.Host.Tests;

public sealed class ApiClientAdministrationEndpointTests(StructaDocWebApplicationFactory factory)
    : IClassFixture<StructaDocWebApplicationFactory>
{
    [Fact]
    public async Task Management_endpoints_require_an_administrator()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/admin/api-clients");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Mutation_requires_antiforgery_token()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/api-clients",
            new ApiClientRequest("Integration client", [AuthenticationScopes.DocumentsRead]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("", "documents:read")]
    [InlineData("Integration client", "unknown:scope")]
    public async Task Create_rejects_invalid_definition(string name, string scope)
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/api-clients",
            new ApiClientRequest(name, [scope]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Administrator_can_manage_complete_api_client_lifecycle()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();

        using var createResponse = await client.PostAsJsonAsync(
            "/api/v1/admin/api-clients",
            new ApiClientRequest(
                "  Integration client  ",
                [AuthenticationScopes.DocumentsWrite, AuthenticationScopes.DocumentsWrite]));
        var created = await createResponse.Content
            .ReadFromJsonAsync<ApiClientCredentialResponse>();

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal("no-store", createResponse.Headers.CacheControl?.ToString());
        Assert.NotNull(created);
        Assert.Equal("Integration client", created.Client.Name);
        Assert.Equal([AuthenticationScopes.DocumentsWrite], created.Client.Scopes);
        Assert.True(created.Client.IsActive);

        using var listResponse = await client.GetAsync("/api/v1/admin/api-clients");
        var listJson = await listResponse.Content.ReadAsStringAsync();
        var listed = await listResponse.Content
            .ReadFromJsonAsync<ApiClientResponse[]>();

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Contains(listed!, candidate => candidate.Id == created.Client.Id);
        Assert.DoesNotContain(created.Credential, listJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secretHash", listJson, StringComparison.OrdinalIgnoreCase);

        using var updateResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/api-clients/{created.Client.Id:D}",
            new ApiClientRequest(
                "Read-only integration",
                [AuthenticationScopes.DocumentsRead]));
        var updated = await updateResponse.Content.ReadFromJsonAsync<ApiClientResponse>();

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        Assert.NotNull(updated);
        Assert.Equal("Read-only integration", updated.Name);
        Assert.Equal([AuthenticationScopes.DocumentsRead], updated.Scopes);

        using var forbiddenUpload = await UploadAsync(client, created.Credential);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenUpload.StatusCode);

        using var rotateResponse = await client.PostAsync(
            $"/api/v1/admin/api-clients/{created.Client.Id:D}/rotate",
            content: null);
        var rotated = await rotateResponse.Content
            .ReadFromJsonAsync<ApiClientCredentialResponse>();

        Assert.Equal(HttpStatusCode.OK, rotateResponse.StatusCode);
        Assert.Equal("no-store", rotateResponse.Headers.CacheControl?.ToString());
        Assert.NotNull(rotated);
        Assert.NotEqual(created.Credential, rotated.Credential);

        using var oldCredentialUpload = await UploadAsync(client, created.Credential);
        Assert.Equal(HttpStatusCode.Unauthorized, oldCredentialUpload.StatusCode);

        using var rotatedCredentialUpload = await UploadAsync(client, rotated.Credential);
        Assert.Equal(HttpStatusCode.Forbidden, rotatedCredentialUpload.StatusCode);

        using var revokeResponse = await client.DeleteAsync(
            $"/api/v1/admin/api-clients/{created.Client.Id:D}");
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        using var repeatedRevokeResponse = await client.DeleteAsync(
            $"/api/v1/admin/api-clients/{created.Client.Id:D}");
        Assert.Equal(HttpStatusCode.NoContent, repeatedRevokeResponse.StatusCode);

        using var revokedCredentialUpload = await UploadAsync(client, rotated.Credential);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedCredentialUpload.StatusCode);

        using var rotateRevokedResponse = await client.PostAsync(
            $"/api/v1/admin/api-clients/{created.Client.Id:D}/rotate",
            content: null);
        Assert.Equal(HttpStatusCode.Conflict, rotateRevokedResponse.StatusCode);

        using var finalListResponse = await client.GetAsync("/api/v1/admin/api-clients");
        var finalList = await finalListResponse.Content
            .ReadFromJsonAsync<ApiClientResponse[]>();
        var revokedClient = Assert.Single(
            finalList!,
            candidate => candidate.Id == created.Client.Id);
        Assert.False(revokedClient.IsActive);
        Assert.NotNull(revokedClient.RevokedAt);
    }

    [Fact]
    public async Task Api_client_cannot_use_administrator_management_endpoints()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();
        using var createResponse = await client.PostAsJsonAsync(
            "/api/v1/admin/api-clients",
            new ApiClientRequest("Non-administrator", [AuthenticationScopes.DocumentsWrite]));
        var created = await createResponse.Content
            .ReadFromJsonAsync<ApiClientCredentialResponse>();
        Assert.NotNull(created);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "ApiKey",
            created.Credential);
        using var response = await client.GetAsync("/api/v1/admin/api-clients");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        string credential)
    {
        using var file = new ByteArrayContent(
            "%PDF-1.7\nAPI Client lifecycle\n%%EOF"u8.ToArray());
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("application/pdf");
        using var multipart = new MultipartFormDataContent();
        multipart.Add(file, "file", "lifecycle.pdf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents")
        {
            Content = multipart,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("ApiKey", credential);
        return await client.SendAsync(request);
    }
}
