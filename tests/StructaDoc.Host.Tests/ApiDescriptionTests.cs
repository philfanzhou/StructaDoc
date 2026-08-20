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

    [Theory]
    [InlineData("/api/v1/parse-runs/{parseRunId}/exports/{format}", "get", "Export")]
    [InlineData("/api/v1/documents/{documentId}/access-grants", "post", "Share")]
    [InlineData("/api/v1/parse-runs/{id}/cancel", "post", "Parse")]
    [InlineData("/api/v1/documents/{id}", "delete", "Delete")]
    [InlineData("/api/v1/parse-runs/{parseRunId}/markdown", "get", "Read")]
    public async Task An_operation_names_the_Document_permission_it_needs_beyond_its_scope(
        string path,
        string method,
        string permission)
    {
        using var document = await ReadDescriptionAsync();
        var described = document.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method)
            .GetProperty("description").GetString();

        // The scope is what a credential carries; the permission is what the Document's owner
        // handed out, and only the second one separates a caller who may export from one who may
        // only read. Held back, the document invites a call that is answered `404`.
        Assert.Contains($"`{permission}` permission", described);
        Assert.Contains("404", described);
    }

    [Fact]
    public async Task Every_operation_on_a_named_Document_or_Parse_Run_states_its_permission()
    {
        using var document = await ReadDescriptionAsync();

        var silent = document.RootElement.GetProperty("paths").EnumerateObject()
            .Where(path => path.Name.StartsWith("/api/v1/documents/{", StringComparison.Ordinal)
                || path.Name.StartsWith("/api/v1/parse-runs/{", StringComparison.Ordinal))
            .SelectMany(path => path.Value.EnumerateObject()
                .Select(operation => (Route: $"{operation.Name.ToUpperInvariant()} {path.Name}", Operation: operation.Value)))
            .Where(entry => !(entry.Operation.TryGetProperty("description", out var description)
                && (description.GetString() ?? string.Empty).Contains("permission on the Document", StringComparison.Ordinal)))
            .Select(entry => entry.Route)
            .ToArray();

        // A route that names a Document, or a Parse Run belonging to one, is admitted by a
        // permission on that Document. Adding such a route without declaring which permission
        // leaves the description confidently describing an endpoint that turns the reader away.
        Assert.True(
            silent.Length == 0,
            $"These operations do not say which Document permission they require: {string.Join(", ", silent)}.");
    }

    [Fact]
    public async Task The_export_route_lists_the_formats_it_accepts()
    {
        using var document = await ReadDescriptionAsync();

        var format = document.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/parse-runs/{parseRunId}/exports/{format}")
            .GetProperty("get")
            .GetProperty("parameters")
            .EnumerateArray()
            .Single(parameter => parameter.GetProperty("name").GetString() == "format");

        // Described as a bare string, the one parameter with four legal values reads like free
        // text, and the caller finds the four by being rejected three times.
        var values = format.GetProperty("schema").GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();
        Assert.Equal(["markdown", "html", "zip", "pdf"], values);
    }

    [Fact]
    public async Task A_browser_only_operation_does_not_offer_the_credential()
    {
        using var document = await ReadBrowserDescriptionAsync();
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
        using var document = await ReadBrowserDescriptionAsync();
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
        Assert.DoesNotContain(paths, path => path.Name.StartsWith("/api/v1/admin", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, path => path.Name.StartsWith("/api/v1/session", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, path => path.Name.StartsWith("/api/v1/setup", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_browser_description_covers_the_complete_surface_separately()
    {
        using var document = await ReadBrowserDescriptionAsync();
        var paths = document.RootElement.GetProperty("paths");

        Assert.True(paths.TryGetProperty("/api/v1/documents", out _));
        Assert.True(paths.TryGetProperty("/api/v1/admin/api-clients", out _));
        Assert.True(paths.TryGetProperty("/api/v1/session", out _));
        Assert.Equal(
            "StructaDoc Browser API",
            document.RootElement.GetProperty("info").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Every_consumer_operation_has_a_stable_unique_operation_id()
    {
        using var document = await ReadDescriptionAsync();
        var operations = document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Select(operation => (
                    Route: $"{operation.Name.ToUpperInvariant()} {path.Name}",
                    Id: operation.Value.TryGetProperty("operationId", out var id) ? id.GetString() : null)))
            .ToArray();

        var missing = operations.Where(operation => string.IsNullOrWhiteSpace(operation.Id)).ToArray();
        Assert.True(missing.Length == 0, $"Missing operationId: {string.Join(", ", missing.Select(operation => operation.Route))}.");

        var duplicate = operations.GroupBy(operation => operation.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        Assert.True(duplicate.Length == 0, $"Duplicate operationId: {string.Join(", ", duplicate)}.");
    }

    [Fact]
    public async Task Upload_is_a_named_single_file_multipart_request()
    {
        using var document = await ReadDescriptionAsync();
        var operation = document.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/documents")
            .GetProperty("post");
        var schema = operation.GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("multipart/form-data")
            .GetProperty("schema");

        Assert.Equal("UploadDocument", operation.GetProperty("operationId").GetString());
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Contains("file", schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        var file = schema.GetProperty("properties").GetProperty("file");
        Assert.Equal("string", file.GetProperty("type").GetString());
        Assert.Equal("binary", file.GetProperty("format").GetString());
    }

    [Fact]
    public async Task Parse_creation_exposes_idempotency_and_request_constraints()
    {
        using var document = await ReadDescriptionAsync();
        var operation = document.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/documents/{documentId}/parse-runs")
            .GetProperty("post");
        var parameter = operation.GetProperty("parameters").EnumerateArray()
            .Single(value => value.GetProperty("name").GetString() == "Idempotency-Key");

        Assert.Equal("header", parameter.GetProperty("in").GetString());
        Assert.Equal(256, parameter.GetProperty("schema").GetProperty("maxLength").GetInt32());

        var requestSchemaReference = operation.GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema")
            .GetProperty("$ref")
            .GetString();
        var schemaName = requestSchemaReference!.Split('/').Last();
        var request = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(schemaName);
        var maxAttempts = request.GetProperty("properties").GetProperty("maxAttempts");
        Assert.Equal(1, maxAttempts.GetProperty("minimum").GetInt32());
        Assert.Equal(10, maxAttempts.GetProperty("maximum").GetInt32());
        Assert.Equal(3, maxAttempts.GetProperty("default").GetInt32());
        Assert.Equal("object", request.GetProperty("properties").GetProperty("options").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Generated_clients_can_see_authentication_cache_and_range_responses()
    {
        using var document = await ReadDescriptionAsync();
        var paths = document.RootElement.GetProperty("paths");

        var getDocument = paths.GetProperty("/api/v1/documents/{id}").GetProperty("get");
        Assert.True(getDocument.GetProperty("responses").TryGetProperty("401", out _));
        Assert.True(getDocument.GetProperty("responses").TryGetProperty("403", out _));

        var download = paths.GetProperty("/api/v1/documents/{id}/content").GetProperty("get");
        Assert.True(download.GetProperty("responses").TryGetProperty("206", out _));
        Assert.True(download.GetProperty("responses").TryGetProperty("416", out _));

        var preview = paths.GetProperty("/api/v1/parse-runs/{parseRunId}/markdown/preview").GetProperty("get");
        Assert.True(preview.GetProperty("responses").TryGetProperty("304", out _));
        Assert.Contains(
            preview.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString() == "If-None-Match"
                && parameter.GetProperty("in").GetString() == "header");
    }

    [Fact]
    public async Task Pagination_parameters_describe_defaults_and_bounds()
    {
        using var document = await ReadDescriptionAsync();
        var parameters = document.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/documents")
            .GetProperty("get")
            .GetProperty("parameters");
        var limit = parameters.EnumerateArray()
            .Single(parameter => parameter.GetProperty("name").GetString() == "limit");
        var schema = limit.GetProperty("schema");

        Assert.Equal(1, schema.GetProperty("minimum").GetInt32());
        Assert.Equal(200, schema.GetProperty("maximum").GetInt32());
        Assert.Equal(50, schema.GetProperty("default").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(limit.GetProperty("description").GetString()));
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
        using var document = path.StartsWith("/api/v1/admin", StringComparison.Ordinal)
            ? await ReadBrowserDescriptionAsync()
            : await ReadDescriptionAsync();
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

    // What a call returns is the part an integrator writes code against, and the generator infers
    // none of it here: these handlers return `IResult`, so an operation nobody declared is
    // described as answering nothing at all. Nothing fails when that happens, and the reader takes
    // silence for the contract, which is why this is an invariant over the whole document rather
    // than a test per endpoint.
    [Fact]
    public async Task Every_operation_says_what_a_successful_call_returns()
    {
        using var document = await ReadDescriptionAsync();

        var undescribed = document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Select(operation => (Route: $"{operation.Name.ToUpperInvariant()} {path.Name}", Operation: operation.Value)))
            .Where(entry => !DescribesSuccess(entry.Operation))
            .Select(entry => entry.Route)
            .ToArray();

        Assert.True(
            undescribed.Length == 0,
            $"These operations do not say what a successful call returns: {string.Join(", ", undescribed)}.");
    }

    [Fact]
    public async Task The_result_types_a_client_is_generated_against_are_in_the_document()
    {
        using var document = await ReadDescriptionAsync();
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        // Blocks are the product's output, and the overview asks a client to tolerate Block types
        // it does not know. That instruction only means something to a client that was given the
        // shape to begin with.
        var block = schemas.GetProperty("ParseBlockResponse").GetProperty("properties");
        foreach (var field in new[] { "sequence", "type", "content", "boundingBox" })
        {
            Assert.True(block.TryGetProperty(field, out _), $"Block field '{field}' is missing.");
        }

        foreach (var name in new[] { "ParsePageResponse", "ParseAssetResponse", "ParseArtifactResponse", "BoundingBoxResponse" })
        {
            Assert.True(schemas.TryGetProperty(name, out _), $"Schema '{name}' is missing.");
        }
    }

    [Theory]
    [InlineData("/api/v1/parse-runs/{parseRunId}/markdown", "get", "Returns the stored Markdown Artifact without rendering it.")]
    [InlineData("/api/v1/parse-runs/{parseRunId}/markdown/preview", "get", "The Markdown result rendered as a self-contained HTML page, for display rather than for saving.")]
    [InlineData("/api/v1/parse-runs/{parseRunId}/exports/{format}", "get", "Creates a packaged Markdown, HTML, ZIP, or PDF deliverable.")]
    [InlineData("/api/v1/documents/{documentId}/access-grants", "get", "Lists the explicit access grants on a Document.")]
    [InlineData("/api/v1/documents/{documentId}/access-grants", "post", "Creates or replaces a grant for one OIDC user or API client.")]
    [InlineData("/api/v1/documents/{documentId}/access-grants/{grantId}", "delete", "Revokes one explicit access grant.")]
    [InlineData("/api/v1/documents/{documentId}/parse-runs", "post", "Creates a durable Parse Run for a Document.")]
    [InlineData("/api/v1/parse-runs/{id}/cancel", "post", "Requests best-effort cancellation of a Parse Run.")]
    [InlineData("/api/v1/parse-runs/{parseRunId}/blocks", "get", "Lists Blocks in stable reading order.")]
    public async Task Operations_whose_routes_do_not_explain_their_semantics_have_summaries(
        string path,
        string method,
        string summary)
    {
        using var document = await ReadDescriptionAsync();
        var operation = document.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);

        Assert.Equal(summary, operation.GetProperty("summary").GetString());
    }

    private static bool DescribesSuccess(JsonElement operation)
    {
        if (!operation.TryGetProperty("responses", out var responses))
        {
            return false;
        }

        var success = responses.EnumerateObject()
            .Where(response => response.Name.StartsWith('2') || response.Name.StartsWith('3'))
            .ToArray();
        // `204` and a redirect describe an empty body, which is an answer. Every other success has
        // a shape, and a status code declared without one describes half of it.
        return success.Length > 0
            && success.All(response =>
                response.Name == "204"
                || response.Name.StartsWith('3')
                || response.Value.TryGetProperty("content", out _));
    }

    private static async Task<JsonDocument> ReadDescriptionAsync()
        => await ReadDescriptionAsync("/api/v1/openapi.json");

    private static async Task<JsonDocument> ReadBrowserDescriptionAsync()
        => await ReadDescriptionAsync("/api/v1-browser/openapi.json");

    private static async Task<JsonDocument> ReadDescriptionAsync(string path)
    {
        using var factory = new StructaDocWebApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(
            path,
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await ReadDocumentAsync(response);
    }

    private static async Task<JsonDocument> ReadDocumentAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
}
