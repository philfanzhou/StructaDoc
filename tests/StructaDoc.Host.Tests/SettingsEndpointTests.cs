using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StructaDoc.Adapters.Authentication;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Application.Settings;
using StructaDoc.Contracts.ParseRuns;
using StructaDoc.Contracts.Settings;

namespace StructaDoc.Host.Tests;

/// <summary>
/// Settings decide what the service does, so what may be written and what wins between the browser
/// and the deployment matter more than the round trip.
/// </summary>
public sealed class SettingsEndpointTests
{

    [Fact]
    public async Task A_stored_setting_replaces_the_shipped_default_and_survives_a_restart()
    {
        using var deployment = new SettingsTestDeployment();

        using (var factory = deployment.CreateFactory())
        using (var client = await SignedInClientAsync(factory))
        {
            var before = await GetAsync(client, SettingCatalog.ParseMaxConcurrency);
            Assert.False(before.IsStored);
            Assert.Equal("1", before.Value);
            Assert.False(before.IsManagedExternally);

            using var response = await client.PutAsJsonAsync(
                "/api/v1/admin/settings",
                new SettingUpdateRequest(SettingCatalog.ParseMaxConcurrency, "4"),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var written = await response.Content.ReadFromJsonAsync<SettingUpdateResponse>(
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(written!.RestartRequired);
        }

        // A setting that only lived in the writing process would be a setting nobody set.
        using (var restarted = deployment.CreateFactory())
        using (var client = await SignedInClientAsync(restarted))
        {
            var after = await GetAsync(client, SettingCatalog.ParseMaxConcurrency);
            Assert.True(after.IsStored);
            Assert.Equal("4", after.Value);
        }
    }

    [Fact]
    public async Task Clearing_a_setting_restores_the_default_rather_than_storing_nothing()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SignedInClientAsync(factory);

        using var stored = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.MaxUploadBytes, "2048"),
            cancellationToken: TestContext.Current.CancellationToken);
        stored.EnsureSuccessStatusCode();
        Assert.Equal("2048", (await GetAsync(client, SettingCatalog.MaxUploadBytes)).Value);

        using var cleared = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.MaxUploadBytes, string.Empty),
            cancellationToken: TestContext.Current.CancellationToken);
        cleared.EnsureSuccessStatusCode();

        var restored = await GetAsync(client, SettingCatalog.MaxUploadBytes);
        Assert.False(restored.IsStored);
        Assert.Equal("104857600", restored.Value);

        // Nothing is pending: this process started with no stored row, so the default it bound at
        // startup is the value the clear returns to.
        Assert.False(restored.IsPendingRestart);
    }

    [Fact]
    public async Task Clearing_a_setting_this_process_started_with_reports_the_default_as_pending()
    {
        using var deployment = new SettingsTestDeployment();

        using (var factory = deployment.CreateFactory())
        using (var client = await SignedInClientAsync(factory))
        {
            using var stored = await client.PutAsJsonAsync(
                "/api/v1/admin/settings",
                new SettingUpdateRequest(SettingCatalog.ParseMaxConcurrency, "4"),
                cancellationToken: TestContext.Current.CancellationToken);
            stored.EnsureSuccessStatusCode();
        }

        // This process bound its options from a configuration that included the row, so clearing it
        // must report the default that now applies and say the running service has not caught up.
        // Reading the startup snapshot instead would report the deleted value as still chosen.
        using (var restarted = deployment.CreateFactory())
        using (var client = await SignedInClientAsync(restarted))
        {
            Assert.Equal("4", (await GetAsync(client, SettingCatalog.ParseMaxConcurrency)).Value);

            using var cleared = await client.PutAsJsonAsync(
                "/api/v1/admin/settings",
                new SettingUpdateRequest(SettingCatalog.ParseMaxConcurrency, string.Empty),
                cancellationToken: TestContext.Current.CancellationToken);
            cleared.EnsureSuccessStatusCode();

            var state = await GetAsync(client, SettingCatalog.ParseMaxConcurrency);
            Assert.Equal("1", state.Value);
            Assert.False(state.IsStored);
            Assert.True(state.IsPendingRestart);
        }
    }

    [Fact]
    public async Task Clearing_a_live_setting_reports_the_default_rather_than_the_cleared_value()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SignedInClientAsync(factory);

        using var stored = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.ParseMaxConcurrency, "4"),
            cancellationToken: TestContext.Current.CancellationToken);
        stored.EnsureSuccessStatusCode();
        Assert.True((await GetAsync(client, SettingCatalog.ParseMaxConcurrency)).IsPendingRestart);

        using var cleared = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.ParseMaxConcurrency, string.Empty),
            cancellationToken: TestContext.Current.CancellationToken);
        cleared.EnsureSuccessStatusCode();

        // Clearing restores the default, and the default is what the running process is already
        // using. Reporting from the startup snapshot alone would be right here by accident; reporting
        // the value the deleted row held would say a change is waiting that nothing is waiting for.
        var state = await GetAsync(client, SettingCatalog.ParseMaxConcurrency);
        Assert.Equal("1", state.Value);
        Assert.False(state.IsStored);
        Assert.False(state.IsPendingRestart);
    }

    [Fact]
    public async Task A_value_the_deployment_pins_cannot_be_written_here()
    {
        using var deployment = new SettingsTestDeployment();

        // A pin is an environment variable or a command-line argument, and both are process-wide.
        // The registration is replaced instead, so one test cannot change what a Host running beside
        // it sees. Precedence itself is covered where the rule lives, in
        // StructaDocSettingsConfigurationTests.
        using var factory = deployment.CreateFactory(builder => builder.ConfigureServices(
            (context, services) => services.AddSingleton(
                StructaDocSettingsConfiguration.Create(
                    context.Configuration,
                    new ControlPlaneOptions
                    {
                        DatabasePath = context.Configuration["ControlPlane:DatabasePath"]!,
                    },
                    ["--Worker:MaxConcurrency=7"],
                    new FakeSettingSecretProtector(),
                    new SettingsStartupFault()))));
        using var client = await SignedInClientAsync(factory);

        // Only the refusal is asserted here. The pin reaches this Host through the replaced
        // registration rather than through its configuration, so the value it reports is the one it
        // actually started with; what a real pin does to the value is the unit test's subject.
        Assert.True((await GetAsync(client, SettingCatalog.ParseMaxConcurrency)).IsManagedExternally);

        using var response = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.ParseMaxConcurrency, "2"),
            cancellationToken: TestContext.Current.CancellationToken);

        // Storing a value the service would never use reads as a change that did not happen.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False((await GetAsync(client, SettingCatalog.ParseMaxConcurrency)).IsStored);
    }

    [Fact]
    public async Task Parse_execution_status_reports_a_host_that_runs_no_workers()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SignedInClientAsync(factory);

        // This deployment starts a Host with no Workers, which is the one remaining way a Parse Run
        // queues behind nothing. It is a deployment choice and not settable here, so all the service
        // can do is report it -- and it has to, or a queue that will never move looks like one that
        // is about to.
        var status = await client
            .GetFromJsonAsync<ParseExecutionStatusResponse>(
                "/api/v1/parse-execution",
                cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(status!.WorkerEnabled);
    }

    [Fact]
    public async Task Parse_execution_status_is_not_readable_without_signing_in()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/v1/parse-execution",
            TestContext.Current.CancellationToken);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                or HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Unauthenticated access returned {(int)response.StatusCode}.");
    }

    [Theory]
    [InlineData(SettingCatalog.ParseMaxConcurrency, "0")]
    [InlineData(SettingCatalog.ParseMaxConcurrency, "65")]
    [InlineData(SettingCatalog.ParseMaxConcurrency, "many")]
    [InlineData(SettingCatalog.UploadApiEnabled, "yes")]
    [InlineData(SettingCatalog.MaxUploadBytes, "512")]
    [InlineData(SettingCatalog.WorkerLeaseDuration, "00:00:00.4")]
    [InlineData(SettingCatalog.WorkerLeaseDuration, "soon")]
    [InlineData(SettingCatalog.WorkerRecoveryBatchSize, "0")]
    [InlineData(SettingCatalog.ProviderResultsMaxCompressionRatio, "0.5")]
    [InlineData(SettingCatalog.ProviderResultsMaxCompressionRatio, "lots")]
    [InlineData(SettingCatalog.ProviderResultsMaxEntryPathBytes, "63")]
    [InlineData(SettingCatalog.LibreOfficeMaxConcurrency, "0")]
    [InlineData(SettingCatalog.LibreOfficeResourceInspectionInterval, "00:00:06")]
    [InlineData(SettingCatalog.AuthenticationAdministratorSessionLifetime, "00:04:00")]
    [InlineData(SettingCatalog.AuthenticationAdministratorSessionLifetime, "8.00:00:00")]
    [InlineData(SettingCatalog.AuthenticationAdministratorSessionLifetime, "forever")]
    [InlineData(SettingCatalog.AuthenticationLoginPermitLimit, "0")]
    // Sixty-one passes the options class and is refused by the ServiceMantle sign-in policies at
    // startup; the write-time bound has to refuse it first, or a browser could store a value the
    // next start rejects.
    [InlineData(SettingCatalog.AuthenticationLoginPermitLimit, "61")]
    [InlineData(SettingCatalog.AuthenticationLoginRateLimitWindow, "00:00:09")]
    [InlineData(SettingCatalog.AuthenticationLoginRateLimitWindow, "00:11:00")]
    public async Task Values_outside_a_settings_range_or_type_are_refused(string key, string value)
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SignedInClientAsync(factory);

        using var response = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(key, value),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False((await GetAsync(client, key)).IsStored);
    }

    [Fact]
    public async Task Keys_outside_the_catalog_cannot_be_written()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SignedInClientAsync(factory);

        // Settings are an allowlist. A key that could reach the store without appearing in the
        // catalog would let one session steer configuration no test covers. This one is refused for
        // a second reason as well: the key ring it locates is read before the store is opened, so a
        // stored value would be read too early to have any effect.
        using var response = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(
                "Authentication:DataProtectionKeysPath",
                "/tmp/somewhere-else"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Settings_are_closed_to_anonymous_callers_and_require_antiforgery()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();

        using var anonymous = factory.CreateClient();
        using var anonymousResponse = await anonymous.GetAsync(
            "/api/v1/admin/settings",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var client = await SignedInClientAsync(factory);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using var withoutToken = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.UploadApiEnabled, "false"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, withoutToken.StatusCode);
    }

    [Fact]
    public async Task Restart_is_administrator_only_and_reports_what_brings_the_service_back()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();

        using var anonymous = factory.CreateClient();
        using var refused = await anonymous.PostAsync(
            "/api/v1/admin/system/restart",
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        using var client = await SignedInClientAsync(factory);
        using var accepted = await client.PostAsync(
            "/api/v1/admin/system/restart",
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var body = await accepted.Content.ReadFromJsonAsync<RestartAcceptedResponse>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("restart policy", body!.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_tuning_keys_are_published_with_their_defaults_and_pending_state()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SignedInClientAsync(factory);

        var settings = await client.GetFromJsonAsync<SettingResponse[]>(
            "/api/v1/admin/settings",
            cancellationToken: TestContext.Current.CancellationToken);

        // The list is the catalog and nothing else: every key the catalog publishes is answered,
        // and no key that is not in it appears.
        Assert.Equal(SettingCatalog.All.Count, settings!.Length);

        var maintenance = settings.Single(setting => setting.Key == SettingCatalog.WorkerMaintenanceInterval);
        Assert.Equal("Duration", maintenance.Kind);
        Assert.Equal("00:00:05", maintenance.Value);
        Assert.False(maintenance.IsStored);
        Assert.False(maintenance.IsManagedExternally);
        Assert.False(maintenance.IsPendingRestart);

        var ratio = settings.Single(setting => setting.Key == SettingCatalog.ProviderResultsMaxCompressionRatio);
        Assert.Equal("Number", ratio.Kind);
        Assert.Equal("200", ratio.Value);

        var conversion = settings.Single(setting => setting.Key == SettingCatalog.LibreOfficeEnabled);
        Assert.Equal("Boolean", conversion.Kind);
        Assert.Equal("true", conversion.Value);

        var inspection = settings.Single(
            setting => setting.Key == SettingCatalog.LibreOfficeResourceInspectionInterval);
        // The sub-second interval is reported in the one spelling the service stores, not the
        // shorter one the image's configuration file happens to write.
        Assert.Equal("00:00:00.2500000", inspection.Value);

        // The sign-in tuning half of Authentication is settable; the key-ring half is not, and
        // these three are the whole published set for the section.
        var session = settings.Single(
            setting => setting.Key == SettingCatalog.AuthenticationAdministratorSessionLifetime);
        Assert.Equal("Duration", session.Kind);
        Assert.Equal("08:00:00", session.Value);
        Assert.False(session.IsStored);
        Assert.False(session.IsManagedExternally);
        Assert.False(session.IsPendingRestart);

        var permit = settings.Single(setting => setting.Key == SettingCatalog.AuthenticationLoginPermitLimit);
        Assert.Equal("Integer", permit.Kind);
        Assert.Equal("10", permit.Value);

        var loginWindow = settings.Single(
            setting => setting.Key == SettingCatalog.AuthenticationLoginRateLimitWindow);
        Assert.Equal("Duration", loginWindow.Kind);
        Assert.Equal("00:01:00", loginWindow.Value);
    }

    [Fact]
    public async Task Tuning_values_round_trip_through_the_store_and_survive_a_restart()
    {
        using var deployment = new SettingsTestDeployment();

        using (var factory = deployment.CreateFactory())
        using (var client = await SignedInClientAsync(factory))
        {
            await WriteAsync(client, SettingCatalog.WorkerLeaseDuration, "00:05:00");
            await WriteAsync(client, SettingCatalog.ProviderResultsMaxCompressionRatio, "150.5");
            await WriteAsync(client, SettingCatalog.LibreOfficeResourceInspectionInterval, "00:00:01");

            var lease = await GetAsync(client, SettingCatalog.WorkerLeaseDuration);
            Assert.True(lease.IsStored);
            Assert.True(lease.IsPendingRestart);
        }

        using (var restarted = deployment.CreateFactory())
        using (var client = await SignedInClientAsync(restarted))
        {
            Assert.Equal("00:05:00", (await GetAsync(client, SettingCatalog.WorkerLeaseDuration)).Value);
            Assert.Equal("150.5", (await GetAsync(client, SettingCatalog.ProviderResultsMaxCompressionRatio)).Value);
            Assert.Equal("00:00:01", (await GetAsync(client, SettingCatalog.LibreOfficeResourceInspectionInterval)).Value);

            // This process bound its options with the rows in place, so nothing is waiting on a
            // restart any more.
            Assert.False((await GetAsync(client, SettingCatalog.WorkerLeaseDuration)).IsPendingRestart);
        }
    }

    [Fact]
    public async Task A_duration_written_in_shorthand_is_reported_in_the_stored_spelling()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SignedInClientAsync(factory);

        // "0:45" and "00:45:00" are the same duration, and the binder reads either. What comes
        // back is the one canonical spelling, so a saved value never looks different from itself
        // after a reload.
        using var response = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.WorkerRetryDelay, "0:45"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var state = await GetAsync(client, SettingCatalog.WorkerRetryDelay);
        Assert.Equal("00:45:00", state.Value);
        Assert.True(state.IsStored);
    }

    [Fact]
    public async Task A_tuning_key_the_deployment_pins_cannot_be_written_here()
    {
        using var deployment = new SettingsTestDeployment();

        using var factory = deployment.CreateFactory(builder => builder.ConfigureServices(
            (context, services) => services.AddSingleton(
                StructaDocSettingsConfiguration.Create(
                    context.Configuration,
                    new ControlPlaneOptions
                    {
                        DatabasePath = context.Configuration["ControlPlane:DatabasePath"]!,
                    },
                    ["--Worker:LeaseDuration=00:09:00"],
                    new FakeSettingSecretProtector(),
                    new SettingsStartupFault()))));
        using var client = await SignedInClientAsync(factory);

        Assert.True((await GetAsync(client, SettingCatalog.WorkerLeaseDuration)).IsManagedExternally);

        using var response = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.WorkerLeaseDuration, "00:05:00"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False((await GetAsync(client, SettingCatalog.WorkerLeaseDuration)).IsStored);
    }

    [Fact]
    public async Task A_combination_that_cannot_start_is_dropped_reported_and_repairable()
    {
        using var deployment = new SettingsTestDeployment();

        // Each value is one the key accepts alone; together they break the rule that a heartbeat
        // must be shorter than the lease it renews. Nothing a single write sees can refuse that.
        using (var writer = deployment.CreateFactory())
        using (var client = await SignedInClientAsync(writer))
        {
            await WriteAsync(client, SettingCatalog.WorkerLeaseDuration, "00:02:00");
            await WriteAsync(client, SettingCatalog.WorkerHeartbeatInterval, "00:05:00");

            var faults = await client.GetFromJsonAsync<SettingStartupFaultResponse[]>(
                "/api/v1/admin/settings/startup-faults",
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Empty(faults!);
        }

        // The next start refuses the combination, drops the stored Worker section, and runs on the
        // shipped tuning instead of crash-looping.
        using (var restarted = deployment.CreateFactory())
        using (var client = await SignedInClientAsync(restarted))
        {
            var faults = await client.GetFromJsonAsync<SettingStartupFaultResponse[]>(
                "/api/v1/admin/settings/startup-faults",
                cancellationToken: TestContext.Current.CancellationToken);
            var fault = faults!.Single();
            Assert.Equal(SettingCatalog.WorkerSection, fault.Section);
            Assert.Contains("HeartbeatInterval", fault.Detail, StringComparison.Ordinal);

            // The administration surface the repair needs is what stayed working. Raising the lease
            // above the heartbeat is the repair; it has to beat it rather than match it, which is
            // the same rule the next start will enforce.
            await WriteAsync(client, SettingCatalog.WorkerLeaseDuration, "00:10:00");
        }

        using (var repaired = deployment.CreateFactory())
        using (var client = await SignedInClientAsync(repaired))
        {
            var faults = await client.GetFromJsonAsync<SettingStartupFaultResponse[]>(
                "/api/v1/admin/settings/startup-faults",
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Empty(faults!);
            Assert.Equal("00:10:00", (await GetAsync(client, SettingCatalog.WorkerLeaseDuration)).Value);
        }
    }

    [Fact]
    public async Task Authentication_tuning_round_trips_and_drives_the_sign_in_cookie_after_a_restart()
    {
        using var deployment = new SettingsTestDeployment();

        using (var factory = deployment.CreateFactory())
        using (var client = await SignedInClientAsync(factory))
        {
            await WriteAsync(client, SettingCatalog.AuthenticationAdministratorSessionLifetime, "00:30:00");
            await WriteAsync(client, SettingCatalog.AuthenticationLoginPermitLimit, "5");
            await WriteAsync(client, SettingCatalog.AuthenticationLoginRateLimitWindow, "00:02:00");

            var session = await GetAsync(client, SettingCatalog.AuthenticationAdministratorSessionLifetime);
            Assert.True(session.IsStored);
            Assert.True(session.IsPendingRestart);
        }

        using (var restarted = deployment.CreateFactory())
        using (var client = await SignedInClientAsync(restarted))
        {
            Assert.Equal(
                "00:30:00",
                (await GetAsync(client, SettingCatalog.AuthenticationAdministratorSessionLifetime)).Value);
            Assert.Equal(
                "5",
                (await GetAsync(client, SettingCatalog.AuthenticationLoginPermitLimit)).Value);
            Assert.Equal(
                "00:02:00",
                (await GetAsync(client, SettingCatalog.AuthenticationLoginRateLimitWindow)).Value);

            // This process bound its tuning with the rows in place, so nothing waits on a restart.
            Assert.False(
                (await GetAsync(client, SettingCatalog.AuthenticationAdministratorSessionLifetime)).IsPendingRestart);

            // The stored tuning is what the running service composed, not only what the page
            // reports: the bound instance is the one both sign-in rate-limit policies were built
            // from, and the administrator cookie carries a half-hour window where the shipped
            // default is eight hours. The sign-in cookie itself is issued without an Expires
            // attribute — the lifetime is enforced inside the encrypted ticket — so the composed
            // options are where the late binding becomes inspectable.
            Assert.Equal(TimeSpan.FromMinutes(30), AdministratorCookieOptions(restarted).ExpireTimeSpan);
            var tuning = BoundAuthenticationTuning(restarted);
            Assert.Equal(5, tuning.LoginPermitLimit);
            Assert.Equal(TimeSpan.FromMinutes(2), tuning.LoginRateLimitWindow);
        }
    }

    [Fact]
    public async Task An_authentication_tuning_key_the_deployment_pins_cannot_be_written_here()
    {
        using var deployment = new SettingsTestDeployment();

        using var factory = deployment.CreateFactory(builder => builder.ConfigureServices(
            (context, services) => services.AddSingleton(
                StructaDocSettingsConfiguration.Create(
                    context.Configuration,
                    new ControlPlaneOptions
                    {
                        DatabasePath = context.Configuration["ControlPlane:DatabasePath"]!,
                    },
                    ["--Authentication:AdministratorSessionLifetime=00:45:00"],
                    new FakeSettingSecretProtector(),
                    new SettingsStartupFault()))));
        using var client = await SignedInClientAsync(factory);

        Assert.True(
            (await GetAsync(client, SettingCatalog.AuthenticationAdministratorSessionLifetime)).IsManagedExternally);

        using var response = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.AuthenticationAdministratorSessionLifetime, "00:30:00"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(
            (await GetAsync(client, SettingCatalog.AuthenticationAdministratorSessionLifetime)).IsStored);
    }

    [Fact]
    public async Task A_stored_authentication_tuning_value_the_service_cannot_use_is_dropped_at_startup()
    {
        using var deployment = new SettingsTestDeployment();

        // A value this wrong cannot arrive through the API, which refuses it at write time. It is
        // seeded the way it would actually happen: a row left behind by an older build whose
        // validation accepted it, or written beside the service. Either way the next start has to
        // drop it, say so, and run on the shipped default rather than refuse to come up.
        using (var factory = deployment.CreateFactory())
        {
            using var client = factory.CreateClient();
            using var warmup = await client.GetAsync(
                "/api/v1/system/info",
                TestContext.Current.CancellationToken);
            warmup.EnsureSuccessStatusCode();
        }

        SeedStoredSetting(
            deployment.ControlPlanePath,
            SettingCatalog.AuthenticationAdministratorSessionLifetime,
            "30.00:00:00");

        using (var restarted = deployment.CreateFactory())
        using (var client = await SignedInClientAsync(restarted))
        {
            var faults = await client.GetFromJsonAsync<SettingStartupFaultResponse[]>(
                "/api/v1/admin/settings/startup-faults",
                cancellationToken: TestContext.Current.CancellationToken);
            var fault = faults!.Single();
            Assert.Equal(SettingCatalog.AuthenticationSection, fault.Section);
            Assert.Contains("AdministratorSessionLifetime", fault.Detail, StringComparison.Ordinal);

            // The stored row is still reported — the administrator can see what they wrote — but
            // says it is not in force.
            var stored = await GetAsync(client, SettingCatalog.AuthenticationAdministratorSessionLifetime);
            Assert.True(stored.IsStored);
            Assert.Equal("30.00:00:00", stored.Value);

            // What stayed running is the shipped default, in the same composed options the
            // round-trip test reads: an eight-hour cookie and the shipped permit and window, not
            // the thirty days the dropped row held.
            Assert.Equal(TimeSpan.FromHours(8), AdministratorCookieOptions(restarted).ExpireTimeSpan);
            var tuning = BoundAuthenticationTuning(restarted);
            Assert.Equal(10, tuning.LoginPermitLimit);
            Assert.Equal(TimeSpan.FromMinutes(1), tuning.LoginRateLimitWindow);
        }
    }

    private static CookieAuthenticationOptions AdministratorCookieOptions(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider
            .GetRequiredService<IOptionsSnapshot<CookieAuthenticationOptions>>()
            .Get(StructaDoc.Host.Authentication.AuthenticationSchemes.AdministratorCookie);
    }

    private static AuthenticationTuningOptions BoundAuthenticationTuning(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AuthenticationTuningOptions>();
    }

    private static void SeedStoredSetting(string controlPlanePath, string key, string value)
    {
        using var connection = new SqliteConnection($"Data Source={controlPlanePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO settings (key, value, updated_at_utc, updated_by) VALUES (@key, @value, @updatedAt, @updatedBy)";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("@updatedBy", "seeded");
        command.ExecuteNonQuery();
    }

    private static async Task WriteAsync(HttpClient client, string key, string value)
    {
        using var response = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(key, value),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(
            response.IsSuccessStatusCode,
            $"Writing '{key}' returned {(int)response.StatusCode}.");
    }

    private static async Task<SettingResponse> GetAsync(HttpClient client, string key)
    {
        var settings = await client.GetFromJsonAsync<SettingResponse[]>("/api/v1/admin/settings");
        return settings!.Single(setting => setting.Key == key);
    }

    private static Task<HttpClient> SignedInClientAsync(WebApplicationFactory<Program> factory)
    {
        return SettingsTestDeployment.SignedInClientAsync(factory);
    }
}
