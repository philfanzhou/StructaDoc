using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StructaDoc.Host.Tests;

// The description is generated from the endpoints, so what is worth testing is not that a route
// appears in it but that the parts a generator cannot infer are right: who may call an endpoint,
// with which scope, and how the credential is presented. A document that describes the routes
// correctly and the authentication wrongly is worse than none, because it is followed.
public sealed class ApiDescriptionTests
{
    [Fact]
    public async Task The_description_is_served_without_a_credential()
    {
        using var factory = new StructaDocWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/v1/openapi.json",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var document = await ReadDocumentAsync(response);
        Assert.Equal("StructaDoc API", document.RootElement.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal("v1", document.RootElement.GetProperty("info").GetProperty("version").GetString());
    }

    [Fact]
    public async Task The_credential_is_described_with_the_scheme_that_makes_it_work()
    {
        using var document = await ReadDescriptionAsync();

        var scheme = document.RootElement
            .GetProperty("components")
            .GetProperty("securitySchemes")
            .GetProperty("ApiKey");

        // The header value carries its own `ApiKey ` prefix, so the whole header is the described
        // value. Described as an HTTP scheme instead, a generated example would omit the prefix and
        // fail to authenticate.
        Assert.Equal("apiKey", scheme.GetProperty("type").GetString());
        Assert.Equal("header", scheme.GetProperty("in").GetString());
        Assert.Equal("Authorization", scheme.GetProperty("name").GetString());
        Assert.Contains("ApiKey <credential>", scheme.GetProperty("description").GetString());
    }

    [Theory]
    [InlineData("/api/v1/documents", "get", "documents:read")]
    [InlineData("/api/v1/documents", "post", "documents:write")]
    [InlineData("/api/v1/documents/{id}", "delete", "documents:write")]
    [InlineData("/api/v1/documents/{documentId}/parse-runs", "post", "parses:write")]
    [InlineData("/api/v1/parse-runs/{id}", "get", "parses:read")]
    public async Task A_scope_gated_operation_names_its_scope_and_offers_the_credential(
        string path,
        string method,
        string scope)
    {
        using var document = await ReadDescriptionAsync();
        var operation = document.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);

        Assert.Contains(scope, operation.GetProperty("description").GetString());
        var requirement = Assert.Single(operation.GetProperty("security").EnumerateArray());
        Assert.True(requirement.TryGetProperty("ApiKey", out _));
    }

    [Fact]
    public async Task A_browser_only_operation_does_not_offer_the_credential()
    {
        using var document = await ReadDescriptionAsync();
        var operation = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/v1/admin/api-clients")
            .GetProperty("post");

        // Offering the credential here would produce a document that invites an integration to
        // create API clients, which no scope permits and no key can reach.
        Assert.False(operation.TryGetProperty("security", out _));
        Assert.Contains("browser session", operation.GetProperty("description").GetString());
    }

    [Fact]
    public async Task An_operation_that_opts_out_of_authorization_is_described_as_open()
    {
        using var document = await ReadDescriptionAsync();
        var operation = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/v1/admin/antiforgery")
            .GetProperty("get");

        // This one sits among administrator endpoints and is reachable without signing in, which is
        // what makes signing in possible. Reading the group it is grouped under rather than what is
        // enforced on it would describe a credential nobody has yet.
        Assert.False(operation.TryGetProperty("security", out _));
        Assert.Contains("unauthenticated", operation.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Every_route_the_overview_points_at_is_one_the_document_describes()
    {
        using var document = await ReadDescriptionAsync();
        var paths = document.RootElement.GetProperty("paths")
            .EnumerateObject()
            .Select(path => path.Name)
            .ToHashSet(StringComparer.Ordinal);

        // The overview is prose, and prose is where a route goes stale without anything failing.
        var described = document.RootElement.GetProperty("info").GetProperty("description").GetString();
        var referenced = Regex.Matches(described ?? string.Empty, @"/api/v1/[A-Za-z0-9\-/{}]+")
            .Select(match => match.Value)
            .ToArray();

        Assert.NotEmpty(referenced);
        Assert.All(referenced, route => Assert.Contains(route, paths));
    }

    [Fact]
    public async Task The_description_covers_the_service_api_and_nothing_else()
    {
        using var document = await ReadDescriptionAsync();

        var paths = document.RootElement.GetProperty("paths").EnumerateObject().ToArray();
        Assert.NotEmpty(paths);
        Assert.All(paths, path => Assert.StartsWith("/api/", path.Name));
        // Health probes are endpoints, but they are an operational contract rather than the API,
        // and describing them would invite an integration to depend on their shape.
        Assert.DoesNotContain(paths, path => path.Name.StartsWith("/health", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_operation_is_grouped_under_a_named_tag()
    {
        using var document = await ReadDescriptionAsync();
        var known = document.RootElement.GetProperty("tags")
            .EnumerateArray()
            .Select(tag => tag.GetProperty("name").GetString())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                var tags = operation.Value.GetProperty("tags")
                    .EnumerateArray()
                    .Select(tag => tag.GetString());
                // Ungrouped, every operation lands under the assembly name and the reader is given
                // one undifferentiated list.
                Assert.All(tags, tag => Assert.Contains(tag, known));
            }
        }
    }

    [Theory]
    [InlineData("/api/v1/documents", "post", "Documents")]
    [InlineData("/api/v1/documents/{documentId}/access-grants", "get", "Documents")]
    [InlineData("/api/v1/documents/{documentId}/parse-runs", "post", "Parse Runs")]
    [InlineData("/api/v1/parse-runs/{id}", "get", "Parse Runs")]
    [InlineData("/api/v1/admin/api-clients", "post", "Administration")]
    public async Task An_operation_is_grouped_by_its_subject_rather_than_its_leading_segments(
        string path,
        string method,
        string tag)
    {
        using var document = await ReadDescriptionAsync();
        var operation = document.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);

        // Grouping is by path prefix, so the endpoint that starts parsing would fall under Documents
        // on its route alone, which is the one group an integrator looking for it will not open.
        var tags = operation.GetProperty("tags").EnumerateArray().Select(entry => entry.GetString());
        Assert.Equal(tag, Assert.Single(tags));
    }

    [Fact]
    public async Task The_browsable_page_is_served_from_the_service_itself()
    {
        using var factory = new StructaDocWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/v1/docs/index.html",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        // The page must be the description viewer rather than the application shell the Host
        // returns for unmatched client routes.
        Assert.Contains("swagger", page, StringComparison.OrdinalIgnoreCase);

        // The prefix itself is what a person types or links to. The Host answers an unmatched
        // `/api` path with a problem document, so this has to resolve before that rule sees it.
        using var redirect = await client.GetAsync(
            "/api/v1/docs",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, redirect.StatusCode);
        Assert.Equal("text/html", redirect.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<JsonDocument> ReadDescriptionAsync()
    {
        using var factory = new StructaDocWebApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(
            "/api/v1/openapi.json",
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await ReadDocumentAsync(response);
    }

    private static async Task<JsonDocument> ReadDocumentAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
}
