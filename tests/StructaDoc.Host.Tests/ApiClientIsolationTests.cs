using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using StructaDoc.Application.Authentication;
using StructaDoc.Contracts.Authentication;
using StructaDoc.Contracts.Documents;
using StructaDoc.Contracts.ParseRuns;

namespace StructaDoc.Host.Tests;

// An API client is a workspace principal, not a second administrator with narrower verbs. A scope
// says which verbs a key may use; it says nothing about whose Documents they may be used on, and
// these tests are about the difference.
public sealed class ApiClientIsolationTests
{
    [Fact]
    public async Task A_client_reads_only_the_documents_it_uploaded()
    {
        using var factory = new StructaDocWebApplicationFactory();
        using var administrator = factory.CreateClient();
        await administrator.LoginAsAdministratorAsync();

        using var first = await CreateApiClientAsync(factory, administrator, "First integration");
        using var second = await CreateApiClientAsync(factory, administrator, "Second integration");
        var owned = await UploadAsync(first, "owned.pdf");

        using var listedByOwner = await first.GetAsync(
            "/api/v1/documents",
            TestContext.Current.CancellationToken);
        var ownerView = await listedByOwner.Content.ReadFromJsonAsync<DocumentListResponse>(
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, listedByOwner.StatusCode);
        Assert.NotNull(ownerView);
        var ownedItem = Assert.Single(ownerView.Items);
        Assert.Equal(owned.Id, ownedItem.Id);
        Assert.True(ownedItem.OwnedByCurrentUser);

        using var listedByStranger = await second.GetAsync(
            "/api/v1/documents",
            TestContext.Current.CancellationToken);
        var strangerView = await listedByStranger.Content.ReadFromJsonAsync<DocumentListResponse>(
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, listedByStranger.StatusCode);
        Assert.NotNull(strangerView);
        Assert.Empty(strangerView.Items);

        // A Document outside the caller's boundary is absent rather than forbidden, so that holding
        // a key does not confirm which Document IDs exist.
        using var detail = await second.GetAsync(
            $"/api/v1/documents/{owned.Id:D}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);

        using var content = await second.GetAsync(
            $"/api/v1/documents/{owned.Id:D}/content",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, content.StatusCode);
    }

    [Fact]
    public async Task A_client_cannot_parse_or_delete_another_clients_document()
    {
        using var factory = new StructaDocWebApplicationFactory();
        using var administrator = factory.CreateClient();
        await administrator.LoginAsAdministratorAsync();

        using var owner = await CreateApiClientAsync(factory, administrator, "Owner");
        using var stranger = await CreateApiClientAsync(factory, administrator, "Stranger");
        var document = await UploadAsync(owner, "owned.pdf");

        using var strangerParse = await stranger.PostAsJsonAsync(
            $"/api/v1/documents/{document.Id:D}/parse-runs",
            new ParseRunCreateRequest(),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, strangerParse.StatusCode);

        using var strangerDeletion = await stranger.DeleteAsync(
            $"/api/v1/documents/{document.Id:D}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, strangerDeletion.StatusCode);

        // The owner is refused for a reason about parsing rather than about the Document, which is
        // what separates an isolation failure from a deployment that has no Provider configured.
        using var ownerParse = await owner.PostAsJsonAsync(
            $"/api/v1/documents/{document.Id:D}/parse-runs",
            new ParseRunCreateRequest(),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(HttpStatusCode.NotFound, ownerParse.StatusCode);

        using var ownerDeletion = await owner.DeleteAsync(
            $"/api/v1/documents/{document.Id:D}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, ownerDeletion.StatusCode);
    }

    [Fact]
    public async Task An_administrator_still_reaches_every_client_document()
    {
        using var factory = new StructaDocWebApplicationFactory();
        using var administrator = factory.CreateClient();
        await administrator.LoginAsAdministratorAsync();

        using var first = await CreateApiClientAsync(factory, administrator, "First integration");
        using var second = await CreateApiClientAsync(factory, administrator, "Second integration");
        var firstDocument = await UploadAsync(first, "first.pdf");
        var secondDocument = await UploadAsync(second, "second.pdf");

        using var listed = await administrator.GetAsync(
            "/api/v1/documents",
            TestContext.Current.CancellationToken);
        var view = await listed.Content.ReadFromJsonAsync<DocumentListResponse>(
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.NotNull(view);
        Assert.Contains(view.Items, item => item.Id == firstDocument.Id);
        Assert.Contains(view.Items, item => item.Id == secondDocument.Id);
        // An administrator is not a workspace principal, so nothing is owned by one.
        Assert.DoesNotContain(view.Items, item => item.OwnedByCurrentUser);
    }

    [Fact]
    public async Task An_access_grant_names_an_api_client_and_restores_what_isolation_withheld()
    {
        using var factory = new StructaDocWebApplicationFactory();
        using var administrator = factory.CreateClient();
        await administrator.LoginAsAdministratorAsync();

        using var owner = await CreateApiClientAsync(factory, administrator, "Owner");
        var (granteeId, granteeClient) = await CreateIdentifiedApiClientAsync(
            factory,
            administrator,
            "Grantee");
        using var grantee = granteeClient;
        var document = await UploadAsync(owner, "shared.pdf");

        using var beforeGrant = await grantee.GetAsync(
            $"/api/v1/documents/{document.Id:D}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, beforeGrant.StatusCode);

        using var grant = await owner.PostAsJsonAsync(
            $"/api/v1/documents/{document.Id:D}/access-grants",
            new DocumentAccessGrantRequest(
                PrincipalIdentity.ApiClientIssuer,
                PrincipalIdentity.ApiClientSubject(granteeId),
                ["read"]),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);

        using var afterGrant = await grantee.GetAsync(
            $"/api/v1/documents/{document.Id:D}",
            TestContext.Current.CancellationToken);
        var granted = await afterGrant.Content.ReadFromJsonAsync<DocumentResponse>(
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, afterGrant.StatusCode);
        Assert.NotNull(granted);
        Assert.Equal(document.Id, granted.Id);
        // Read was granted; ownership was not.
        Assert.False(granted.OwnedByCurrentUser);

        // The grant carried read alone, so a verb the client's scopes allow still stops here.
        using var deletion = await grantee.DeleteAsync(
            $"/api/v1/documents/{document.Id:D}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, deletion.StatusCode);
    }

    [Fact]
    public async Task A_grant_rejects_an_api_client_subject_that_is_not_a_client_id()
    {
        using var factory = new StructaDocWebApplicationFactory();
        using var administrator = factory.CreateClient();
        await administrator.LoginAsAdministratorAsync();

        using var owner = await CreateApiClientAsync(factory, administrator, "Owner");
        var document = await UploadAsync(owner, "owned.pdf");

        using var response = await owner.PostAsJsonAsync(
            $"/api/v1/documents/{document.Id:D}/access-grants",
            new DocumentAccessGrantRequest(
                PrincipalIdentity.ApiClientIssuer,
                "not-a-client-id",
                ["read"]),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // `write` was a name in the grant vocabulary that no route ever asked for, which is worse than
    // an absent one: whoever granted it believed they had handed something over, and whoever read
    // the grant back believed writes were being gated. Refusing it says so at the moment someone
    // tries, rather than in a document they may not read.
    [Fact]
    public async Task A_grant_rejects_a_permission_no_operation_checks()
    {
        using var factory = new StructaDocWebApplicationFactory();
        using var administrator = factory.CreateClient();
        await administrator.LoginAsAdministratorAsync();

        var (granteeId, granteeClient) = await CreateIdentifiedApiClientAsync(
            factory,
            administrator,
            "Grantee");
        granteeClient.Dispose();
        using var owner = await CreateApiClientAsync(factory, administrator, "Owner");
        var document = await UploadAsync(owner, "owned.pdf");

        using var response = await owner.PostAsJsonAsync(
            $"/api/v1/documents/{document.Id:D}/access-grants",
            new DocumentAccessGrantRequest(
                PrincipalIdentity.ApiClientIssuer,
                PrincipalIdentity.ApiClientSubject(granteeId),
                ["read", "write"]),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The whole request is refused, not the unknown half of it: a caller asking for a grant it
        // named wrongly gets no grant at all.
        using var grants = await owner.GetAsync(
            $"/api/v1/documents/{document.Id:D}/access-grants",
            TestContext.Current.CancellationToken);
        var stored = await grants.Content.ReadFromJsonAsync<IReadOnlyList<DocumentAccessGrantResponse>>(
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, grants.StatusCode);
        Assert.NotNull(stored);
        Assert.Empty(stored);
    }

    private static async Task<HttpClient> CreateApiClientAsync(
        StructaDocWebApplicationFactory factory,
        HttpClient administrator,
        string name)
    {
        var (_, client) = await CreateIdentifiedApiClientAsync(factory, administrator, name);
        return client;
    }

    private static async Task<(Guid Id, HttpClient Client)> CreateIdentifiedApiClientAsync(
        StructaDocWebApplicationFactory factory,
        HttpClient administrator,
        string name)
    {
        using var response = await administrator.PostAsJsonAsync(
            "/api/v1/admin/api-clients",
            new ApiClientRequest(name, [.. AuthenticationScopes.All]),
            cancellationToken: TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<ApiClientCredentialResponse>(
            TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("API client creation returned no response.");

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "ApiKey",
            created.Credential);
        return (created.Client.Id, client);
    }

    private static async Task<DocumentResponse> UploadAsync(HttpClient client, string fileName)
    {
        using var file = new ByteArrayContent("%PDF-1.7\nisolation\n%%EOF"u8.ToArray());
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("application/pdf");
        using var multipart = new MultipartFormDataContent();
        multipart.Add(file, "file", fileName);
        using var response = await client.PostAsync(
            "/api/v1/documents",
            multipart,
            TestContext.Current.CancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Upload failed with {(int)response.StatusCode}: "
                + await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        return await response.Content.ReadFromJsonAsync<DocumentResponse>(
            TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("Document upload returned no response.");
    }
}
