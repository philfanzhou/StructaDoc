using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using StructaDoc.Application.Settings;
using StructaDoc.Contracts.Settings;

namespace StructaDoc.Host.Tests;

/// <summary>
/// Worker concurrency and the upload ceiling are the two settings an administrator changes often
/// enough to want without a restart, so both are held by the running service and applied by change
/// listeners. These tests hold the behaviour to that promise: a change takes effect on the next
/// request, the report says what is running rather than what startup bound, and the write is
/// answered with the restart it does or does not need.
/// </summary>
public sealed class LiveSettingTests
{
    [Fact]
    public async Task Changing_the_upload_limit_moves_the_rejection_boundary_without_a_restart()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory(builder =>
        {
            // The upload endpoint must exist for the test to reach, but the size key itself is left
            // unpinned so a stored write is the value in force.
            builder.UseSetting("Documents:UploadApiEnabled", "true");
        });
        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);

        // The shipped 100 MiB default accepts a four-kilobyte document.
        using (var accepted = await client.PostAsync(
            "/api/v1/documents",
            Upload("small", "%PDF-1.7\nlive-setting\n%%EOF"u8.ToArray()),
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        }

        using (var lowered = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.MaxUploadBytes, "2048"),
            cancellationToken: TestContext.Current.CancellationToken))
        {
            lowered.EnsureSuccessStatusCode();
            var written = await lowered.Content.ReadFromJsonAsync<SettingUpdateResponse>(
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(written!.RestartRequired);
        }

        var state = await GetAsync(client, SettingCatalog.MaxUploadBytes);
        Assert.Equal("2048", state.Value);
        Assert.True(state.IsStored);
        Assert.False(state.IsPendingRestart);
        Assert.False(state.RequiresRestart);

        // The boundary the running service enforces moved: the same size that just uploaded is now
        // too large, and the 413 comes from the storage write that is the authority on size.
        using (var rejected = await client.PostAsync(
            "/api/v1/documents",
            Upload("too-large-now", new byte[4096]),
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
        }

        // And back up again, on the same process, with the same response the first small upload had.
        await WriteAsync(client, SettingCatalog.MaxUploadBytes, "104857600");
        using (var acceptedAgain = await client.PostAsync(
            "/api/v1/documents",
            Upload("accepted-again", Pdf(4096)),
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Created, acceptedAgain.StatusCode);
        }
    }

    [Fact]
    public async Task Clearing_the_upload_limit_applies_the_default_without_a_restart()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory(builder =>
            builder.UseSetting("Documents:UploadApiEnabled", "true"));
        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);

        await WriteAsync(client, SettingCatalog.MaxUploadBytes, "2048");
        using (var rejected = await client.PostAsync(
            "/api/v1/documents",
            Upload("capped", new byte[4096]),
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
        }

        using (var cleared = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.MaxUploadBytes, string.Empty),
            cancellationToken: TestContext.Current.CancellationToken))
        {
            cleared.EnsureSuccessStatusCode();
            var written = await cleared.Content.ReadFromJsonAsync<SettingUpdateResponse>(
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(written!.RestartRequired);
        }

        var state = await GetAsync(client, SettingCatalog.MaxUploadBytes);
        Assert.False(state.IsStored);
        Assert.Equal("104857600", state.Value);
        Assert.False(state.IsPendingRestart);

        using (var accepted = await client.PostAsync(
            "/api/v1/documents",
            Upload("restored", Pdf(4096)),
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        }
    }

    [Fact]
    public async Task Worker_concurrency_written_here_is_reported_as_running()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);

        // This host runs no Workers, which is beside the point: the gate the listener applies to is
        // the same state the report reads, and the execution behaviour itself is covered against a
        // host that does run them, in ParseRunConcurrencyTests.
        using (var response = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.ParseMaxConcurrency, "3"),
            cancellationToken: TestContext.Current.CancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var written = await response.Content.ReadFromJsonAsync<SettingUpdateResponse>(
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(written!.RestartRequired);
        }

        var state = await GetAsync(client, SettingCatalog.ParseMaxConcurrency);
        Assert.Equal("3", state.Value);
        Assert.True(state.IsStored);
        Assert.False(state.IsPendingRestart);
        Assert.False(state.RequiresRestart);
    }

    private static async Task<SettingResponse> GetAsync(HttpClient client, string key)
    {
        var settings = await client.GetFromJsonAsync<SettingResponse[]>("/api/v1/admin/settings");
        return settings!.Single(setting => setting.Key == key);
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

    private static MultipartFormDataContent Upload(string fileName, byte[] content)
    {
        var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/pdf");
        var multipart = new MultipartFormDataContent();
        multipart.Add(fileContent, "file", $"{fileName}.pdf");
        return multipart;
    }

    /// <summary>
    /// A file large enough to straddle a lowered limit that still carries the PDF signature, because
    /// an accepted upload has to pass type detection too and an all-zero buffer never would.
    /// </summary>
    private static byte[] Pdf(int size)
    {
        var content = new byte[size];
        "%PDF-1.7\n"u8.CopyTo(content);
        return content;
    }
}
