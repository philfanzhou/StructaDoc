using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Application.Authentication;
using StructaDoc.Contracts.Authentication;
using StructaDoc.Contracts.Documents;
using StructaDoc.Contracts.ParseRuns;
using StructaDoc.Contracts.Providers;
using StructaDoc.Adapters.Persistence;

namespace StructaDoc.Host.Tests;

public sealed class ProviderConfigAndParseRunEndpointTests(StructaDocWebApplicationFactory factory)
    : IClassFixture<StructaDocWebApplicationFactory>
{
    [Fact]
    public async Task Administrator_can_version_provider_config_and_create_idempotent_parse_runs()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();

        const string credential = "provider-test-secret";
        using var createConfigResponse = await client.PostAsJsonAsync(
            "/api/v1/admin/provider-configs",
            new ProviderConfigRequest(
                "Primary MinerU",
                "mineru-cloud",
                "https://mineru.example.test/api/",
                Model: "pipeline-v1",
                Credential: credential,
                IsDefault: true));
        var createConfigJson = await createConfigResponse.Content.ReadAsStringAsync();
        var config = JsonSerializer.Deserialize<ProviderConfigResponse>(
            createConfigJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(HttpStatusCode.Created, createConfigResponse.StatusCode);
        Assert.NotNull(config);
        Assert.Equal(1, config.VersionNumber);
        Assert.True(config.IsDefault);
        Assert.True(config.HasCredential);
        Assert.DoesNotContain(credential, createConfigJson, StringComparison.Ordinal);
        Assert.DoesNotContain("protectedCredential", createConfigJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"createdAt\"", createConfigJson, StringComparison.Ordinal);
        Assert.DoesNotContain("createdAtUtc", createConfigJson, StringComparison.Ordinal);

        using var listConfigResponse = await client.GetAsync("/api/v1/admin/provider-configs");
        var listConfigJson = await listConfigResponse.Content.ReadAsStringAsync();
        var listedConfigs = JsonSerializer.Deserialize<ProviderConfigResponse[]>(
            listConfigJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(HttpStatusCode.OK, listConfigResponse.StatusCode);
        Assert.Contains(listedConfigs!, listed => listed.Id == config.Id);
        Assert.DoesNotContain(credential, listConfigJson, StringComparison.Ordinal);

        var document = await UploadDocumentAsync(client);
        var firstRequest = new StructaDoc.Contracts.ParseRuns.ParseRunCreateRequest(
            Options: JsonSerializer.Deserialize<JsonElement>("{\"language\":\"zh\"}"),
            MaxAttempts: 4);
        using var firstResponse = await CreateParseRunAsync(client, document.Id, firstRequest, "parse-one");
        var firstJson = await firstResponse.Content.ReadAsStringAsync();
        var first = JsonSerializer.Deserialize<ParseRunResponse>(
            firstJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.NotNull(first);
        Assert.Equal("queued", first.Status);
        Assert.Equal(config.Id, first.ProviderConfigId);
        Assert.Equal(config.CurrentVersionId, first.ProviderConfigVersionId);
        Assert.Equal("zh", first.Options.GetProperty("language").GetString());
        Assert.Equal(4, first.MaxAttempts);
        Assert.Contains("\"nextAttemptAt\"", firstJson, StringComparison.Ordinal);
        Assert.DoesNotContain("nextAttemptAtUtc", firstJson, StringComparison.Ordinal);

        using var replayResponse = await CreateParseRunAsync(client, document.Id, firstRequest, "parse-one");
        var replay = await replayResponse.Content.ReadFromJsonAsync<ParseRunResponse>();
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        Assert.Equal("true", replayResponse.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(first.Id, replay!.Id);

        using var updateResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/provider-configs/{config.Id:D}",
            new ProviderConfigRequest(
                "Primary MinerU v2",
                "mineru-cloud",
                "https://mineru.example.test/v2/",
                Model: "pipeline-v2",
                IsDefault: true));
        var updated = await updateResponse.Content.ReadFromJsonAsync<ProviderConfigResponse>();

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        Assert.NotNull(updated);
        Assert.Equal(2, updated.VersionNumber);
        Assert.NotEqual(config.CurrentVersionId, updated.CurrentVersionId);
        Assert.True(updated.HasCredential);

        using var secondResponse = await CreateParseRunAsync(
            client,
            document.Id,
            new StructaDoc.Contracts.ParseRuns.ParseRunCreateRequest(),
            "parse-two");
        var second = await secondResponse.Content.ReadFromJsonAsync<ParseRunResponse>();
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        Assert.Equal(updated.CurrentVersionId, second!.ProviderConfigVersionId);
        Assert.Equal(config.CurrentVersionId, first.ProviderConfigVersionId);

        // The workspace re-reads this list every time a document is opened, which makes it the
        // busiest read in the product. It is also the one where a condition applied to the projected
        // record rather than to the entity fails only at run time, as a 500.
        var documentRuns = await client.GetFromJsonAsync<ParseRunResponse[]>(
            $"/api/v1/documents/{document.Id:D}/parse-runs");
        Assert.Equal(2, documentRuns!.Length);
        Assert.Equal(second.Id, documentRuns[0].Id);
        Assert.Equal(first.Id, documentRuns[1].Id);

        using var getResponse = await client.GetAsync($"/api/v1/parse-runs/{first.Id:D}");
        var fetched = await getResponse.Content.ReadFromJsonAsync<ParseRunResponse>();
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.NotNull(fetched);
        Assert.Equal(first.Id, fetched.Id);
        Assert.Equal(first.ProviderConfigVersionId, fetched.ProviderConfigVersionId);
        Assert.Equal("zh", fetched.Options.GetProperty("language").GetString());

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
        var versions = await dbContext.ProviderConfigVersions.AsNoTracking()
            .Where(version => version.ProviderConfigId == config.Id)
            .OrderBy(version => version.VersionNumber)
            .ToArrayAsync();
        Assert.Equal(2, versions.Length);
        Assert.NotNull(versions[0].ProtectedCredential);
        Assert.NotEqual(credential, versions[0].ProtectedCredential);
        Assert.Equal(versions[0].ProtectedCredential, versions[1].ProtectedCredential);
        Assert.Equal(2, await dbContext.ParseRuns.CountAsync(run => run.DocumentId == document.Id));
    }

    [Fact]
    public async Task Provider_management_and_parse_runs_enforce_authentication_and_validation()
    {
        using var client = factory.CreateClient();

        using var providerResponse = await client.GetAsync("/api/v1/admin/provider-configs");
        using var parseResponse = await client.PostAsJsonAsync(
            $"/api/v1/documents/{Guid.NewGuid():D}/parse-runs",
            new StructaDoc.Contracts.ParseRuns.ParseRunCreateRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, providerResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, parseResponse.StatusCode);
    }

    [Fact]
    public async Task Provider_definition_rejects_unsafe_endpoint_and_conflicting_credential_actions()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();

        using var unsafeEndpoint = await client.PostAsJsonAsync(
            "/api/v1/admin/provider-configs",
            new ProviderConfigRequest("Unsafe", "mineru-cloud", "file:///etc/passwd"));
        using var conflictingCredential = await client.PostAsJsonAsync(
            "/api/v1/admin/provider-configs",
            new ProviderConfigRequest(
                "Conflict",
                "mineru-local",
                "http://localhost:8000/",
                Credential: "secret",
                ClearCredential: true));

        Assert.Equal(HttpStatusCode.BadRequest, unsafeEndpoint.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, conflictingCredential.StatusCode);
    }

    [Fact]
    public async Task Administrator_can_correct_a_provider_config_and_erase_its_stored_credential()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();

        using var createResponse = await client.PostAsJsonAsync(
            "/api/v1/admin/provider-configs",
            new ProviderConfigRequest(
                "Mistyped MinerU",
                "mineru-local",
                "http://wrong-host.test:8000/",
                Credential: "typed-by-mistake"));
        var created = await createResponse.Content.ReadFromJsonAsync<ProviderConfigResponse>();
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.True(created!.HasCredential);

        // Correcting an address and adding the settings the create form leaves out is the whole
        // point of an editable configuration: neither can be reached any other way from a browser.
        using var correctResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/provider-configs/{created.Id:D}",
            new ProviderConfigRequest(
                "Corrected MinerU",
                "mineru-local",
                "http://right-host.test:8000/",
                Model: "pipeline",
                Backend: "vlm-http-client"));
        var corrected = await correctResponse.Content.ReadFromJsonAsync<ProviderConfigResponse>();

        Assert.Equal(HttpStatusCode.OK, correctResponse.StatusCode);
        Assert.Equal("Corrected MinerU", corrected!.Name);
        Assert.Equal("http://right-host.test:8000/", corrected.BaseUrl);
        Assert.Equal("pipeline", corrected.Model);
        Assert.Equal("vlm-http-client", corrected.Backend);
        // An omitted credential keeps the stored one, or every edit would silently erase it.
        Assert.True(corrected.HasCredential);

        using var clearResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/provider-configs/{created.Id:D}",
            new ProviderConfigRequest(
                "Corrected MinerU",
                "mineru-local",
                "http://right-host.test:8000/",
                Model: "pipeline",
                Backend: "vlm-http-client",
                ClearCredential: true));
        var cleared = await clearResponse.Content.ReadFromJsonAsync<ProviderConfigResponse>();

        Assert.Equal(HttpStatusCode.OK, clearResponse.StatusCode);
        Assert.False(cleared!.HasCredential);
        Assert.Equal(3, cleared.VersionNumber);
    }

    [Fact]
    public async Task Provider_config_deletion_is_refused_while_a_parse_run_records_it()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();

        using var createResponse = await client.PostAsJsonAsync(
            "/api/v1/admin/provider-configs",
            new ProviderConfigRequest(
                "Deletable MinerU",
                "mineru-local",
                "http://deletable.test:8000/",
                IsDefault: true));
        var config = await createResponse.Content.ReadFromJsonAsync<ProviderConfigResponse>();
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var document = await UploadDocumentAsync(client);
        using var createParse = await CreateParseRunAsync(
            client,
            document.Id,
            new StructaDoc.Contracts.ParseRuns.ParseRunCreateRequest(),
            $"delete-guard-{Guid.NewGuid():N}");
        var parseRun = await createParse.Content.ReadFromJsonAsync<ParseRunResponse>();
        Assert.Equal(HttpStatusCode.Created, createParse.StatusCode);

        // A Parse Run reads its Provider configuration version while it executes, so removing the
        // configuration under it would break a run already under way.
        using var deleteWhileActive = await client.DeleteAsync(
            $"/api/v1/admin/provider-configs/{config!.Id:D}");
        Assert.Equal(HttpStatusCode.Conflict, deleteWhileActive.StatusCode);

        using var cancel = await client.PostAsync($"/api/v1/parse-runs/{parseRun!.Id:D}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        await WaitForStatusAsync(client, parseRun.Id, "cancelled");

        // Finishing does not release it: the run keeps the configuration version as the record of
        // how it was produced, and deleting the rows would erase that rather than free anything.
        using var deleteWithHistory = await client.DeleteAsync(
            $"/api/v1/admin/provider-configs/{config.Id:D}");
        Assert.Equal(HttpStatusCode.Conflict, deleteWithHistory.StatusCode);

        using var stillListed = await client.GetAsync("/api/v1/admin/provider-configs");
        var configs = await stillListed.Content.ReadFromJsonAsync<ProviderConfigResponse[]>();
        Assert.Contains(configs!, listed => listed.Id == config.Id);
    }

    [Fact]
    public async Task An_unused_provider_config_is_deleted_with_every_version_of_it()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();

        using var createResponse = await client.PostAsJsonAsync(
            "/api/v1/admin/provider-configs",
            new ProviderConfigRequest("Unused MinerU", "mineru-local", "http://unused.test:8000/"));
        var config = await createResponse.Content.ReadFromJsonAsync<ProviderConfigResponse>();
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        using var updateResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/provider-configs/{config!.Id:D}",
            new ProviderConfigRequest("Unused MinerU", "mineru-local", "http://unused.test:9000/"));
        updateResponse.EnsureSuccessStatusCode();

        using var deleteResponse = await client.DeleteAsync(
            $"/api/v1/admin/provider-configs/{config.Id:D}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        using var repeatResponse = await client.DeleteAsync(
            $"/api/v1/admin/provider-configs/{config.Id:D}");
        Assert.Equal(HttpStatusCode.NotFound, repeatResponse.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
        Assert.False(await dbContext.ProviderConfigs.AnyAsync(item => item.Id == config.Id));
        // Both versions go with it. A version row left behind would keep a credential alive under a
        // configuration the administrator believes is gone.
        Assert.False(await dbContext.ProviderConfigVersions.AnyAsync(
            version => version.ProviderConfigId == config.Id));
    }

    [Fact]
    public async Task Provider_config_deletion_requires_an_administrator()
    {
        using var client = factory.CreateClient();

        using var response = await client.DeleteAsync(
            $"/api/v1/admin/provider-configs/{Guid.NewGuid():D}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Api_client_parse_write_scope_does_not_grant_parse_read_or_provider_administration()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();
        using var providerCreate = await client.PostAsJsonAsync(
            "/api/v1/admin/provider-configs",
            new ProviderConfigRequest(
                "Scoped provider",
                "mineru-local",
                "http://mineru-local.test/",
                IsDefault: true));
        providerCreate.EnsureSuccessStatusCode();
        using var apiClientCreate = await client.PostAsJsonAsync(
            "/api/v1/admin/api-clients",
            new ApiClientRequest(
                "Parse writer",
                [AuthenticationScopes.DocumentsWrite, AuthenticationScopes.ParsesWrite]));
        var issued = await apiClientCreate.Content.ReadFromJsonAsync<ApiClientCredentialResponse>();
        Assert.NotNull(issued);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "ApiKey",
            issued.Credential);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        var document = await UploadDocumentAsync(client);
        using var createParse = await client.PostAsJsonAsync(
            $"/api/v1/documents/{document.Id:D}/parse-runs",
            new StructaDoc.Contracts.ParseRuns.ParseRunCreateRequest());
        var parseRun = await createParse.Content.ReadFromJsonAsync<ParseRunResponse>();

        Assert.Equal(HttpStatusCode.Created, createParse.StatusCode);
        Assert.NotNull(parseRun);

        using var readParse = await client.GetAsync($"/api/v1/parse-runs/{parseRun.Id:D}");
        using var manageProviders = await client.GetAsync("/api/v1/admin/provider-configs");
        Assert.Equal(HttpStatusCode.Forbidden, readParse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, manageProviders.StatusCode);
    }

    [Fact]
    public async Task Cancelling_a_queued_parse_run_releases_its_document_for_deletion()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();
        using var providerCreate = await client.PostAsJsonAsync(
            "/api/v1/admin/provider-configs",
            new ProviderConfigRequest(
                "Cancellation provider",
                "mineru-local",
                "http://mineru-local.test/",
                IsDefault: true));
        providerCreate.EnsureSuccessStatusCode();

        var document = await UploadDocumentAsync(client);
        using var createParse = await CreateParseRunAsync(
            client,
            document.Id,
            new StructaDoc.Contracts.ParseRuns.ParseRunCreateRequest(),
            $"cancel-{Guid.NewGuid():N}");
        var parseRun = await createParse.Content.ReadFromJsonAsync<ParseRunResponse>();
        Assert.Equal(HttpStatusCode.Created, createParse.StatusCode);
        Assert.Equal("queued", parseRun!.Status);

        // Execution is disabled by default, so without cancellation this run — and its Document —
        // would stay non-final forever.
        using var blockedDelete = await client.DeleteAsync($"/api/v1/documents/{document.Id:D}");
        Assert.Equal(HttpStatusCode.Conflict, blockedDelete.StatusCode);

        using var cancel = await client.PostAsync($"/api/v1/parse-runs/{parseRun.Id:D}/cancel", null);
        var cancelling = await cancel.Content.ReadFromJsonAsync<ParseRunResponse>();
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        // An unleased run carries no lease to wait out, so maintenance may already have completed
        // the cancellation. Only finality is promised, never the intermediate status.
        Assert.Contains(cancelling!.Status, new[] { "cancel-requested", "cancelled" });

        // Cancellation is idempotent through completion.
        using var replayCancel = await client.PostAsync($"/api/v1/parse-runs/{parseRun.Id:D}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, replayCancel.StatusCode);

        var cancelled = await WaitForStatusAsync(client, parseRun.Id, "cancelled");
        Assert.NotNull(cancelled.CompletedAt);

        using var repeatAfterCancelled = await client.PostAsync(
            $"/api/v1/parse-runs/{parseRun.Id:D}/cancel",
            null);
        Assert.Equal(HttpStatusCode.Accepted, repeatAfterCancelled.StatusCode);

        using var acceptedDelete = await client.DeleteAsync($"/api/v1/documents/{document.Id:D}");
        Assert.Equal(HttpStatusCode.Accepted, acceptedDelete.StatusCode);
    }

    [Fact]
    public async Task Cancelling_an_unknown_parse_run_is_not_distinguishable_from_no_access()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();

        using var response = await client.PostAsync(
            $"/api/v1/parse-runs/{Guid.NewGuid():D}/cancel",
            null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Parse_run_cancellation_requires_authentication()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            $"/api/v1/parse-runs/{Guid.NewGuid():D}/cancel",
            null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Parse_options_reject_credential_fields()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/documents/{Guid.NewGuid():D}/parse-runs",
            new StructaDoc.Contracts.ParseRuns.ParseRunCreateRequest(
                Options: JsonSerializer.Deserialize<JsonElement>("{\"nested\":{\"apiKey\":\"do-not-store\"}}")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<ParseRunResponse> WaitForStatusAsync(
        HttpClient client,
        Guid parseRunId,
        string expectedStatus)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        ParseRunResponse? parseRun = null;

        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/api/v1/parse-runs/{parseRunId:D}");
            response.EnsureSuccessStatusCode();
            parseRun = await response.Content.ReadFromJsonAsync<ParseRunResponse>();
            if (parseRun?.Status == expectedStatus)
            {
                return parseRun;
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException(
            $"Parse Run '{parseRunId:D}' did not reach '{expectedStatus}'; last status was '{parseRun?.Status}'.");
    }

    private static async Task<DocumentResponse> UploadDocumentAsync(HttpClient client)
    {
        using var content = new ByteArrayContent("%PDF-1.7\nParse Run test\n%%EOF"u8.ToArray());
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/pdf");
        using var multipart = new MultipartFormDataContent();
        multipart.Add(content, "file", "parse-run.pdf");
        using var response = await client.PostAsync("/api/v1/documents", multipart);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DocumentResponse>()
            ?? throw new InvalidOperationException("Upload returned no Document.");
    }

    private static async Task<HttpResponseMessage> CreateParseRunAsync(
        HttpClient client,
        Guid documentId,
        StructaDoc.Contracts.ParseRuns.ParseRunCreateRequest request,
        string idempotencyKey)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/documents/{documentId:D}/parse-runs")
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(message);
    }
}
