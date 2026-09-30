using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Audit;
using ServiceMantle.Persistence.Relational.Stores;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Application.Authentication;
using StructaDoc.Contracts.Auditing;
using StructaDoc.Contracts.Authentication;

namespace StructaDoc.Host.Tests;

/// <summary>
/// The management audit query API from #105: one read-only administration endpoint over the trail
/// #104 persists. The cursor contract, the filters, the refusal of a misused cursor, and the
/// authorization boundary — an API client credential is not an administrator — are what these
/// tests hold.
///
/// Every test filters on its own action, because the shared host's control plane accumulates the
/// audit rows every test in this class writes.
/// </summary>
public sealed class ManagementAuditQueryEndpointTests(StructaDocWebApplicationFactory factory)
    : IClassFixture<StructaDocWebApplicationFactory>
{
    private const string WalkAction = "structadoc.query_walk";
    private const string CursorAction = "structadoc.query_cursor";
    private const string FilterAction = "structadoc.query_filter";
    private const string PageSizeAction = "structadoc.query_page_size";
    private const string LegacyAction = "structadoc.query_legacy";

    private static readonly DateTimeOffset ProbeBaseTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_cursor_walks_the_whole_trail_in_newest_first_order()
    {
        await SeedAsync(ProbeEvents(WalkAction, 7).ToArray());

        var collected = new List<ManagementAuditEntryResponse>();
        ManagementAuditPageResponse page = await QueryPageAsync(
            $"?action={WalkAction}&pageSize=3");
        collected.AddRange(page.Items);

        while (page.HasNextPage)
        {
            Assert.NotNull(page.ContinuationCursor);
            // The cursor is passed back unchanged, with the page it names and nothing else changed.
            page = await QueryPageAsync(
                $"?action={WalkAction}&pageSize=3&page={page.Page + 1}&cursor={Uri.EscapeDataString(page.ContinuationCursor)}");
            collected.AddRange(page.Items);
        }

        Assert.Equal(7, collected.Count);
        // One entry per seeded event, and the trail reads newest first.
        Assert.Equal(
            Enumerable.Range(1, 7).Select(index => $"target-{index}").OrderDescending().ToArray(),
            collected.Select(entry => entry.TargetId).ToArray());
        Assert.Equal(
            collected.Select(entry => entry.OccurredAtUtc).OrderDescending().ToArray(),
            collected.Select(entry => entry.OccurredAtUtc).ToArray());
        Assert.All(collected, entry =>
        {
            Assert.Equal(WalkAction, entry.Action);
            Assert.Equal(WalkAction, entry.TargetType);
            Assert.Equal("Success", entry.Outcome);
            Assert.Equal("interactive_admin", entry.OperatorSource);
            Assert.Equal("operator-a", entry.OperatorId);
        });
    }

    [Fact]
    public async Task A_cursor_reused_with_changed_filters_or_page_is_refused()
    {
        await SeedAsync(ProbeEvents(CursorAction, 4).ToArray());

        var first = await QueryPageAsync($"?action={CursorAction}&pageSize=2");
        Assert.NotNull(first.ContinuationCursor);
        var cursor = Uri.EscapeDataString(first.ContinuationCursor);

        // The same cursor with one filter added is not the query it was issued for.
        await AssertRefusedAsync(
            $"?action={CursorAction}&targetType={CursorAction}&pageSize=2&page=2&cursor={cursor}",
            "audit.query_cursor_invalid");

        // The same cursor with the page it does not name is refused the same way.
        await AssertRefusedAsync(
            $"?action={CursorAction}&pageSize=2&page=1&cursor={cursor}",
            "audit.query_cursor_invalid");

        // Pages after the first require a cursor at all.
        await AssertRefusedAsync(
            $"?action={CursorAction}&pageSize=2&page=2",
            "audit.query_cursor_required");
    }

    [Fact]
    public async Task Each_filter_and_their_combination_select_their_rows()
    {
        await SeedAsync(
            ProbeEvent(FilterAction, "target-1", "operator-a", ManagementAuditOutcome.Success, ProbeBaseTime),
            ProbeEvent(FilterAction, "target-2", "operator-a", ManagementAuditOutcome.Failure, ProbeBaseTime.AddMinutes(1)),
            ProbeEvent(FilterAction, "target-3", "operator-b", ManagementAuditOutcome.Success, ProbeBaseTime.AddMinutes(2)));

        var byOperator = await QueryPageAsync($"?action={FilterAction}&operator=operator-a");
        Assert.Equal(2, byOperator.TotalCount);
        Assert.All(byOperator.Items, entry => Assert.Equal("operator-a", entry.OperatorId));
        Assert.Contains(byOperator.Items, entry => entry.Outcome == "Failure");

        var byTimeRange = await QueryPageAsync(
            $"?action={FilterAction}&from={Uri.EscapeDataString(ProbeBaseTime.AddSeconds(30).ToString("O"))}"
                + $"&to={Uri.EscapeDataString(ProbeBaseTime.AddMinutes(3).ToString("O"))}");
        // Newest first, so the later event is the earlier item.
        Assert.Equal(
            ["target-3", "target-2"],
            byTimeRange.Items.Select(entry => entry.TargetId).ToArray());

        var combined = await QueryPageAsync(
            $"?action={FilterAction}&targetType={FilterAction}&operator=operator-b");
        Assert.Equal(
            ["target-3"],
            combined.Items.Select(entry => entry.TargetId).ToArray());
    }

    [Fact]
    public async Task An_empty_result_reports_no_items_and_no_cursor()
    {
        var empty = await QueryPageAsync($"?action={FilterAction}&targetType=structadoc.nothing");

        Assert.Empty(empty.Items);
        Assert.Equal(0, empty.TotalCount);
        Assert.Null(empty.ContinuationCursor);
        Assert.False(empty.HasNextPage);
    }

    [Fact]
    public async Task The_default_page_size_is_fifty_and_the_bounds_are_enforced()
    {
        await SeedAsync(ProbeEvents(PageSizeAction, 1).ToArray());

        var unfiltered = await QueryPageAsync($"?action={PageSizeAction}");
        Assert.Equal(50, unfiltered.PageSize);

        await AssertRefusedAsync($"?action={PageSizeAction}&pageSize=0", "audit.query_page_size_invalid");
        await AssertRefusedAsync($"?action={PageSizeAction}&pageSize=201", "audit.query_page_size_invalid");
    }

    [Fact]
    public async Task An_unrecognized_filter_value_is_refused_with_its_error_code()
    {
        await AssertRefusedAsync("?action=not a valid action", "audit.action_invalid");
        await AssertRefusedAsync("?targetType=not a valid type", "audit.target_type_invalid");
    }

    [Fact]
    public async Task An_impossible_time_range_is_refused()
    {
        await AssertRefusedAsync(
            $"?from={Uri.EscapeDataString(ProbeBaseTime.ToString("O"))}"
                + $"&to={Uri.EscapeDataString(ProbeBaseTime.AddDays(-1).ToString("O"))}",
            "audit.query_time_range_invalid");
    }

    [Fact]
    public async Task Unauthenticated_callers_are_refused()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/v1/admin/audit",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // An API client credential is an authenticated principal but not an administrator, which is the
    // same refusal the other administration endpoints give it.
    [Fact]
    public async Task An_api_client_credential_is_not_an_administrator()
    {
        using var administrator = factory.CreateClient();
        await administrator.LoginAsAdministratorAsync();
        using var apiClient = await CreateApiClientAsync(administrator);

        using var response = await apiClient.GetAsync(
            "/api/v1/admin/audit",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // A row that predates the query boundary's text limits — direct SQL is the only way one exists —
    // is not returned partially or truncated: the page it appears on fails whole, with the stable
    // entity error code and none of the oversized text in the body.
    [Fact]
    public async Task A_legacy_row_with_oversized_text_fails_the_whole_page()
    {
        await InsertLegacyRowAsync(new string('a', 5000));

        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();
        using var response = await client.GetAsync(
            $"/api/v1/admin/audit?action={LegacyAction}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var bodyText = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(
            JsonDocument.Parse(bodyText).RootElement.TryGetProperty("errorCode", out var errorCode),
            $"the body carries no error code: {bodyText}");
        Assert.Equal("audit.entity_invalid", errorCode.GetString());
        Assert.DoesNotContain(new string('a', 100), bodyText, StringComparison.Ordinal);
    }

    // Caller cancellation ends the request cancelled rather than answered: the query is the one
    // slow thing in the endpoint, so a stub that never answers is how the propagation is observed.
    [Fact]
    public async Task Caller_request_cancellation_propagates_to_the_query()
    {
        using var probeFactory = factory.WithWebHostBuilder(
            builder => builder.ConfigureServices(services => services
                .AddScoped<IManagementAuditQueryService>(_ => new NeverAnsweringQueryService())));
        using var client = probeFactory.CreateClient();
        await client.LoginAsAdministratorAsync();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync("/api/v1/admin/audit", cancellation.Token));
    }

    // The endpoint is described in the operator document — a browser-only administration read —
    // with the cursor semantics a consumer needs, and stays out of the consumer document an SDK is
    // generated from, because no API client credential can call it.
    [Fact]
    public async Task The_endpoint_is_described_in_the_operator_document_only()
    {
        using var browserFactory = new StructaDocWebApplicationFactory();
        using var client = browserFactory.CreateClient();

        using var browser = await client.GetAsync(
            "/api/v1-browser/openapi.json",
            TestContext.Current.CancellationToken);
        browser.EnsureSuccessStatusCode();
        using var browserDocument = JsonDocument.Parse(
            await browser.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var operation = browserDocument.RootElement
            .GetProperty("paths")
            .GetProperty("/api/v1/admin/audit")
            .GetProperty("get");
        Assert.Contains("continuationCursor", operation.GetProperty("description").GetString());
        Assert.Contains("totalCount", operation.GetProperty("description").GetString());

        using var consumer = await client.GetAsync(
            "/api/v1/openapi.json",
            TestContext.Current.CancellationToken);
        consumer.EnsureSuccessStatusCode();
        using var consumerDocument = JsonDocument.Parse(
            await consumer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(
            consumerDocument.RootElement.GetProperty("paths").TryGetProperty("/api/v1/admin/audit", out _));
    }

    private static IEnumerable<ManagementAuditEvent> ProbeEvents(string action, int count) =>
        Enumerable.Range(1, count)
            .Select(index => ProbeEvent(
                action,
                $"target-{index}",
                "operator-a",
                ManagementAuditOutcome.Success,
                ProbeBaseTime.AddMinutes(index)));

    private static ManagementAuditEvent ProbeEvent(
        string action,
        string targetId,
        string operatorId,
        ManagementAuditOutcome outcome,
        DateTimeOffset occurredAtUtc) =>
        ManagementAuditEvent.Create(
            ManagementAuditOperator.Create(
                WellKnownManagementAuditOperatorSources.InteractiveAdmin,
                operatorId,
                $"Probe {operatorId}"),
            ManagementAuditAction.Parse(action),
            ManagementAuditTarget.Create(ManagementAuditTargetType.Parse(action), targetId),
            outcome,
            occurredAtUtc: occurredAtUtc);

    private async Task SeedAsync(params ManagementAuditEvent[] events)
    {
        using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var writer = new EfCoreManagementAuditWriter<ControlPlaneDbContext>(context);
        foreach (var auditEvent in events)
        {
            await writer.RecordAsync(auditEvent, TestContext.Current.CancellationToken);
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<ManagementAuditPageResponse> QueryPageAsync(string query)
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();

        using var response = await client.GetAsync(
            $"/api/v1/admin/audit{query}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<ManagementAuditPageResponse>(
                TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("The audit query returned no page.");
    }

    private async Task AssertRefusedAsync(string query, string errorCode)
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();

        using var response = await client.GetAsync(
            $"/api/v1/admin/audit{query}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // The stable error code rides as an RFC 7807 extension member, which the default problem
        // serializer writes at the root of the body.
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(errorCode, body.RootElement.GetProperty("errorCode").GetString());
    }

    /// <summary>
    /// A legacy row the writer would never produce: over-limit text in the description column,
    /// written straight to the control-plane SQLite file, which is how rows that predate the query
    /// boundary's limits exist at all.
    /// </summary>
    private async Task InsertLegacyRowAsync(string oversizedDescription)
    {
        var path = factory.Services.GetRequiredService<ControlPlaneOptions>().DatabasePath;
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO service_audit_logs (
                id, occurred_at_utc, operator_source, operator_id, operator_display_name,
                action, target_type, target_id, outcome, security_description, metadata_json)
            VALUES (
                @id, @occurredAtUtc, 'interactive_admin', 'legacy-operator', 'Legacy Operator',
                @action, @targetType, 'legacy-target', 1, @description, NULL)
            """;
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue(
            "@occurredAtUtc",
            ProbeBaseTime.AddMinutes(1).UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("@action", LegacyAction);
        command.Parameters.AddWithValue("@targetType", LegacyAction);
        command.Parameters.AddWithValue("@description", oversizedDescription);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<HttpClient> CreateApiClientAsync(HttpClient administrator)
    {
        using var response = await administrator.PostAsJsonAsync(
            "/api/v1/admin/api-clients",
            new ApiClientRequest("Audit query refusal probe", [.. AuthenticationScopes.All]),
            cancellationToken: TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<ApiClientCredentialResponse>(
                TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("API client creation returned no response.");

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "ApiKey",
            created.Credential);
        return client;
    }

    private sealed class NeverAnsweringQueryService : IManagementAuditQueryService
    {
        public async ValueTask<ManagementAuditQueryResult> QueryAsync(
            ManagementAuditQuery query,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new ManagementAuditQueryResult([], 1, 50, 0, continuationCursor: null);
        }
    }
}
