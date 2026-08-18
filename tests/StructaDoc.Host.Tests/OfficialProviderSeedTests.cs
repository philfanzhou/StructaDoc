using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using StructaDoc.Contracts.Documents;
using StructaDoc.Contracts.ParseRuns;
using StructaDoc.Contracts.Providers;

namespace StructaDoc.Host.Tests;

/// <summary>
/// The state a deployment starts in. This class owns its own Host so nothing here observes a
/// Provider another test configured.
/// </summary>
public sealed class OfficialProviderSeedTests(StructaDocWebApplicationFactory factory)
    : IClassFixture<StructaDocWebApplicationFactory>
{
    // Assembling the first Provider out of an address, a model name, and a default marker is the step
    // a first-run administrator has no way to get right, and getting it wrong looks like a service
    // that cannot parse. The deployment therefore arrives with the official endpoint configured and
    // one field left: its token. It is one test rather than several because supplying that token is
    // a one-way change to the Host these assertions share.
    [Fact]
    public async Task The_official_endpoint_arrives_configured_and_parsing_waits_for_its_credential()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();

        var configs = await client.GetFromJsonAsync<ProviderConfigResponse[]>(
            "/api/v1/admin/provider-configs",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(configs);
        var official = Assert.Single(configs, config => config.Name == "official");
        Assert.Equal("mineru-cloud", official.ProviderType);
        Assert.Equal("https://mineru.net/", official.BaseUrl);
        Assert.Equal("vlm", official.Model);
        Assert.True(official.IsEnabled);
        Assert.True(official.IsDefault);
        // The token belongs to the deployment's own MinerU account, so no image can carry one.
        Assert.False(official.HasCredential);

        // A configured Provider with no token would otherwise accept Parse Runs and produce nothing
        // but failures against the service. Refusing at creation keeps the reason next to the
        // request, and the workspace reads the same fact so it can say so before anyone clicks.
        var status = await client.GetFromJsonAsync<ParseExecutionStatusResponse>(
            "/api/v1/parse-execution",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(status);
        Assert.True(status.WorkerEnabled);
        Assert.True(status.ProviderCredentialMissing);

        var document = await UploadDocumentAsync(client);
        using var refused = await CreateParseRunAsync(client, document.Id);
        var refusedBody = await refused.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Contains("credential", refusedBody, StringComparison.OrdinalIgnoreCase);

        // Supplying the token is the whole of what was missing. The address moves to one that
        // refuses connections in the same edit, so accepting the run here cannot reach the real
        // hosted service.
        using var update = await client.PutAsJsonAsync(
            $"/api/v1/admin/provider-configs/{official.Id:D}",
            new ProviderConfigRequest(
                official.Name,
                official.ProviderType,
                "http://127.0.0.1:9/",
                Model: official.Model,
                Credential: "official-endpoint-test-token",
                IsEnabled: true,
                IsDefault: true),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var readyStatus = await client.GetFromJsonAsync<ParseExecutionStatusResponse>(
            "/api/v1/parse-execution",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(readyStatus);
        Assert.False(readyStatus.ProviderCredentialMissing);

        using var accepted = await CreateParseRunAsync(client, document.Id);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    private static async Task<DocumentResponse> UploadDocumentAsync(HttpClient client)
    {
        using var content = new ByteArrayContent("%PDF-1.7\nOfficial provider seed\n%%EOF"u8.ToArray());
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/pdf");
        using var multipart = new MultipartFormDataContent();
        multipart.Add(content, "file", "official-provider.pdf");
        using var response = await client.PostAsync("/api/v1/documents", multipart);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DocumentResponse>()
            ?? throw new InvalidOperationException("Upload returned no Document.");
    }

    private static Task<HttpResponseMessage> CreateParseRunAsync(HttpClient client, Guid documentId) =>
        client.PostAsJsonAsync(
            $"/api/v1/documents/{documentId:D}/parse-runs",
            new ParseRunCreateRequest());
}
