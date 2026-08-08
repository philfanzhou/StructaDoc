using System.Net.Http.Json;
using StructaDoc.Contracts.Authentication;

namespace StructaDoc.Host.Tests;

internal static class AuthenticationTestClientExtensions
{
    public static async Task LoginAsAdministratorAsync(this HttpClient client)
    {
        var anonymousToken = await client.GetAntiforgeryTokenAsync();
        using var loginRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/admin/session")
        {
            Content = JsonContent.Create(new AdministratorLoginRequest(
                StructaDocWebApplicationFactory.AdministratorUsername,
                StructaDocWebApplicationFactory.AdministratorPassword)),
        };
        loginRequest.Headers.Add(anonymousToken.HeaderName, anonymousToken.RequestToken);
        using var loginResponse = await client.SendAsync(loginRequest);
        loginResponse.EnsureSuccessStatusCode();

        var authenticatedToken = await client.GetAntiforgeryTokenAsync();
        client.DefaultRequestHeaders.Remove(authenticatedToken.HeaderName);
        client.DefaultRequestHeaders.Add(
            authenticatedToken.HeaderName,
            authenticatedToken.RequestToken);
    }

    public static async Task<AntiforgeryTokenResponse> GetAntiforgeryTokenAsync(
        this HttpClient client)
    {
        return await client.GetFromJsonAsync<AntiforgeryTokenResponse>(
            "/api/v1/admin/antiforgery")
            ?? throw new InvalidOperationException("Antiforgery endpoint returned no token.");
    }
}
