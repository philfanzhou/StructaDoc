using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Settings;
using StructaDoc.Contracts.Authentication;
using StructaDoc.Contracts.Providers;
using StructaDoc.Contracts.Settings;
using StructaDoc.Contracts.Setup;

namespace StructaDoc.Host.Tests;

/// <summary>
/// Every management audit record point from #104, asserted through the real endpoints it lives
/// in: the action, target, outcome, and operator of the row each point writes, read straight out
/// of the control-plane SQLite file the rows persist to.
/// </summary>
public sealed class ManagementAuditRecordPointTests(StructaDocWebApplicationFactory factory)
    : IClassFixture<StructaDocWebApplicationFactory>
{
    [Fact]
    public async Task Successful_login_records_the_account_and_outcome()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();

        var login = SingleRow(await ReadAuditRowsAsync(), "admin_login.succeeded");

        Assert.Equal("interactive_admin", login.OperatorSource);
        Assert.Equal(
            StructaDocWebApplicationFactory.AdministratorUsername,
            login.OperatorDisplayName);
        Assert.Equal(1, login.Outcome);
        Assert.Equal("admin_session", login.TargetType);
        Assert.Equal(login.OperatorId, login.TargetId);
        Assert.False(string.IsNullOrWhiteSpace(login.OperatorId));
    }

    [Fact]
    public async Task Failed_login_records_the_attempted_identity_as_a_failure()
    {
        using var client = factory.CreateClient();
        var token = await client.GetAntiforgeryTokenAsync();
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/session")
        {
            Content = JsonContent.Create(new AdministratorLoginRequest(
                StructaDocWebApplicationFactory.AdministratorUsername,
                "definitely-wrong-password")),
        };
        login.Headers.Add(token.HeaderName, token.RequestToken);
        using var response = await client.SendAsync(login, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var failed = SingleRow(await ReadAuditRowsAsync(), "admin_login.failed");

        Assert.Equal("anonymous", failed.OperatorSource);
        Assert.Equal(
            $"login-attempt:{StructaDocWebApplicationFactory.AdministratorUsername}",
            failed.OperatorId);
        Assert.Equal(2, failed.Outcome);
        Assert.Equal("admin_session", failed.TargetType);
    }

    [Fact]
    public async Task Administrator_account_creation_is_audited()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();

        using var createResponse = await client.PostAsJsonAsync(
            "/api/v1/admin/administrators",
            new CreateAdministratorRequest(
                "audited-admin",
                "StructaDoc-Audited-2026!",
                "Audited Administrator"),
            cancellationToken: TestContext.Current.CancellationToken);
        var created = await createResponse.Content
            .ReadFromJsonAsync<AdministratorAccountResponse>(
                cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var record = SingleRow(
            await ReadAuditRowsAsync(),
            "structadoc.administrator_account_created");

        Assert.Equal(1, record.Outcome);
        Assert.Equal("structadoc.administrator_account", record.TargetType);
        Assert.Equal(created!.Id.ToString("D"), record.TargetId);
        Assert.Equal("interactive_admin", record.OperatorSource);
        Assert.Equal(
            StructaDocWebApplicationFactory.AdministratorUsername,
            record.OperatorDisplayName);
        Assert.Contains("\"username\":\"audited-admin\"", record.MetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_client_creation_and_revocation_are_audited_without_the_credential()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();

        using var createResponse = await client.PostAsJsonAsync(
            "/api/v1/admin/api-clients",
            new ApiClientRequest("Audited client", [AuthenticationScopes.DocumentsRead]),
            cancellationToken: TestContext.Current.CancellationToken);
        var created = await createResponse.Content
            .ReadFromJsonAsync<ApiClientCredentialResponse>(
                cancellationToken: TestContext.Current.CancellationToken);

        var rows = await ReadAuditRowsAsync();
        var creation = SingleRow(rows, "structadoc.api_client_created");
        Assert.Equal(1, creation.Outcome);
        Assert.Equal("structadoc.api_client", creation.TargetType);
        Assert.Equal(created!.Client.Id.ToString("D"), creation.TargetId);
        Assert.Contains("\"name\":\"Audited client\"", creation.MetadataJson, StringComparison.Ordinal);

        using var revokeResponse = await client.DeleteAsync(
            $"/api/v1/admin/api-clients/{created.Client.Id:D}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        rows = await ReadAuditRowsAsync();
        var revocation = SingleRow(rows, "structadoc.api_client_revoked");
        Assert.Equal(1, revocation.Outcome);
        Assert.Equal(created.Client.Id.ToString("D"), revocation.TargetId);
        Assert.Equal("interactive_admin", revocation.OperatorSource);

        // The credential is the one value the record point must never carry.
        Assert.DoesNotContain(created.Credential, Summarize(rows), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_config_creation_and_version_change_are_audited_without_secrets()
    {
        const string credential = "mineru-audit-credential-token";
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();

        using var createResponse = await client.PostAsJsonAsync(
            "/api/v1/admin/provider-configs",
            new ProviderConfigRequest(
                "Audited MinerU",
                "mineru-cloud",
                "https://mineru.example.test/api/",
                Model: "pipeline-v1",
                Credential: credential,
                IsDefault: true),
            cancellationToken: TestContext.Current.CancellationToken);
        var created = await createResponse.Content
            .ReadFromJsonAsync<ProviderConfigResponse>(
                cancellationToken: TestContext.Current.CancellationToken);

        var rows = await ReadAuditRowsAsync();
        var creation = SingleRow(rows, "structadoc.provider_config_created");
        Assert.Equal(1, creation.Outcome);
        Assert.Equal("structadoc.provider_config", creation.TargetType);
        Assert.Equal(created!.Id.ToString("D"), creation.TargetId);
        Assert.Contains("\"name\":\"Audited MinerU\"", creation.MetadataJson, StringComparison.Ordinal);

        using var updateResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/provider-configs/{created.Id:D}",
            new ProviderConfigRequest(
                "Audited MinerU",
                "mineru-cloud",
                "https://mineru.example.test/api/",
                Model: "pipeline-v2",
                IsDefault: true),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        rows = await ReadAuditRowsAsync();
        var change = SingleRow(rows, "structadoc.provider_config_changed");
        Assert.Equal(1, change.Outcome);
        Assert.Equal(created.Id.ToString("D"), change.TargetId);

        // Only the configuration identity and version are recorded: the credential and the endpoint
        // address never enter an audit field.
        var auditText = Summarize(rows);
        Assert.DoesNotContain(credential, auditText, StringComparison.Ordinal);
        Assert.DoesNotContain("mineru.example.test", auditText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setting_writes_are_audited_with_the_key_name_and_result_only()
    {
        // Oidc:NameClaimType is a settable key the test host does not pin, unlike the Documents and
        // Storage keys it fixes through its own configuration.
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();

        using var write = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.OidcNameClaim, "display_name"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);

        var record = SingleRow(await ReadAuditRowsAsync(), "configuration.changed");

        Assert.Equal(1, record.Outcome);
        Assert.Equal("configuration", record.TargetType);
        Assert.Equal(SettingCatalog.OidcNameClaim, record.TargetId);
        Assert.Equal("interactive_admin", record.OperatorSource);
        Assert.Equal(
            StructaDocWebApplicationFactory.AdministratorUsername,
            record.OperatorDisplayName);

        // A setting's value is never part of the record: the key name and the outcome are all.
        Assert.Equal(string.Empty, record.MetadataJson);
    }

    [Fact]
    public async Task A_deployment_pinned_setting_write_records_a_denial()
    {
        // Storage:RootPath is pinned by the test host, so writing it from the browser is denied
        // rather than rejected as invalid, and the denial is part of the audit trail.
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();

        using var refused = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(
                SettingCatalog.StorageRootPath,
                Path.Combine(Path.GetTempPath(), "audited-storage")),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var denial = SingleRow(await ReadAuditRowsAsync(), "configuration.changed");
        Assert.Equal(3, denial.Outcome);
        Assert.Equal(SettingCatalog.StorageRootPath, denial.TargetId);
    }

    [Fact]
    public async Task Restart_requests_are_audited_before_the_host_stops()
    {
        // A restart stops its host, so this record point gets its own deployment rather than the
        // class's shared one.
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);

        using var accepted = await client.PostAsync(
            "/api/v1/admin/system/restart",
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        // The row is written before the stop is scheduled, so it is already durable here.
        var restart = SingleRow(
            await ReadAuditRowsAsync(deployment.ControlPlanePath),
            "structadoc.restart_requested");

        Assert.Equal(1, restart.Outcome);
        Assert.Equal("service", restart.TargetType);
        Assert.Equal("structadoc", restart.TargetId);
        Assert.Equal("interactive_admin", restart.OperatorSource);
    }

    [Fact]
    public async Task Setup_claim_records_the_first_administrator_creation()
    {
        using var deployment = new SetupAuditDeployment();
        using var factory = deployment.CreateFactory();
        using var client = factory.CreateClient();

        var token = await client.GetAntiforgeryTokenAsync();
        using var claim = new HttpRequestMessage(HttpMethod.Post, "/api/v1/setup")
        {
            Content = JsonContent.Create(new SetupClaimRequest(
                "first-audited",
                "StructaDoc-Setup-Audit-2026!",
                "First Audited Operator")),
        };
        claim.Headers.Add(token.HeaderName, token.RequestToken);
        using var response = await client.SendAsync(claim, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var record = SingleRow(
            await ReadAuditRowsAsync(deployment.ControlPlanePath),
            "structadoc.setup.claimed");

        Assert.Equal(1, record.Outcome);
        Assert.Equal("structadoc.administrator_account", record.TargetType);
        Assert.Equal("first-audited", record.OperatorDisplayName);
        Assert.Equal(record.TargetId, record.OperatorId);
        Assert.Equal("interactive_admin", record.OperatorSource);
    }

    private static string ControlPlanePathOf(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<ControlPlaneOptions>().DatabasePath;

    private async Task<List<AuditRow>> ReadAuditRowsAsync() =>
        await ReadAuditRowsAsync(ControlPlanePathOf(factory));

    private static async Task<List<AuditRow>> ReadAuditRowsAsync(string controlPlanePath)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={controlPlanePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT action, target_type, target_id, outcome, operator_id,
                   operator_display_name, operator_source, metadata_json
            FROM service_audit_logs
            ORDER BY occurred_at_utc, id
            """;
        var rows = new List<AuditRow>();
        await using var reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(new AuditRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? string.Empty : reader.GetString(7)));
        }

        return rows;
    }

    private static AuditRow SingleRow(IReadOnlyList<AuditRow> rows, string action)
    {
        // The shared host serves every test in this class, so each record point asserts on the
        // most recent row its own action produced.
        var matching = rows.Where(row => string.Equals(row.Action, action, StringComparison.Ordinal))
            .TakeLast(1)
            .ToArray();
        return Assert.Single(matching);
    }

    private static string Summarize(IReadOnlyList<AuditRow> rows) => string.Join(
        Environment.NewLine,
        rows.Select(row =>
            $"{row.Action}|{row.TargetType}|{row.TargetId}|{row.Outcome}|{row.OperatorId}"
            + $"|{row.OperatorDisplayName}|{row.OperatorSource}|{row.MetadataJson}"));

    private sealed record AuditRow(
        string Action,
        string TargetType,
        string TargetId,
        int Outcome,
        string? OperatorId,
        string? OperatorDisplayName,
        string OperatorSource,
        string MetadataJson);

    /// <summary>
    /// A dedicated factory with no bootstrap administrator, so the anonymous setup claim can run
    /// and be audited as the first-administrator record point.
    /// </summary>
    private sealed class SetupAuditDeployment : IDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(),
            "structadoc-setup-audit-tests",
            Guid.NewGuid().ToString("N"));

        public SetupAuditDeployment()
        {
            Directory.CreateDirectory(directory);
        }

        public string ControlPlanePath => Path.Combine(directory, "control.db");

        public WebApplicationFactory<Program> CreateFactory()
        {
            return new SetupAuditFactory(directory);
        }

        public void Dispose()
        {
            if (Directory.Exists(directory))
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(directory, recursive: true);
            }
        }

        private sealed class SetupAuditFactory(string directory)
            : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseSetting("Worker:Enabled", "false");
                builder.UseSetting("Authentication:BootstrapAdministratorUsername", string.Empty);
                builder.UseSetting("Authentication:BootstrapAdministratorPassword", string.Empty);
                builder.UseSetting(
                    "Authentication:DataProtectionKeysPath",
                    Path.Combine(directory, "keys"));
                builder.UseSetting("Storage:Provider", "Local");
                builder.UseSetting("Storage:RootPath", Path.Combine(directory, "storage"));
                builder.UseSetting("Database:Provider", "Sqlite");
                builder.UseSetting(
                    "Database:ConnectionString",
                    $"Data Source={Path.Combine(directory, "structadoc.db")};Pooling=False");
                builder.UseSetting(
                    "ControlPlane:DatabasePath",
                    Path.Combine(directory, "control.db"));
            }
        }
    }
}
