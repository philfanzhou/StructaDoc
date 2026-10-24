using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Adapters.Authentication;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Providers;

namespace StructaDoc.Host.Tests;

// ADR-0013 point 5: the layout path's logs carry request identifiers, timing, counts, and status
// only. This test runs a real request with a sentinel hidden inside the image bytes and asserts
// the completion event appears while the sentinel never does — the log line is the thing, not
// just the absence of an obvious mistake in it. It shares the console-output collection because
// it swaps Console.Out, like every other capture test.
[Collection("Console output")]
public sealed class LayoutDetectLoggingTests
{
    private const string Sentinel = "LAYOUT-LOG-SENTINEL-3F2A";

    [Fact]
    public async Task The_completion_log_carries_status_timing_and_counts_only()
    {
        using var factory = new LoggingLayoutFactory();
        factory.Stub.Handler = (_, _) => Task.FromResult(new LayoutDetectionResult(
            [new LayoutDetectionRegion("text", 0.1, 0.1, 0.9, 0.9)],
            100,
            100,
            diagnostics: []));
        using var client = factory.CreateClient();
        var apiKey = await CreateApiClientAsync(factory.Host, AuthenticationScopes.LayoutDetect);
        var image = BuildSentinelPng();

        var originalOutput = Console.Out;
        using var capture = new ChainingConsoleWriter(originalOutput);
        Console.SetOut(capture);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/layout/detect")
            {
                Content = CreateUpload(image),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("ApiKey", apiKey.Credential);

            using var response = await client.SendAsync(
                request,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        var output = capture.Snapshot();

        // The one structured completion event. The sanitizing console pipeline renders the named
        // values into the message text rather than passing them through as separate fields, so
        // the assertion reads what the line actually carries: the status, a timing value, and the
        // region count of this request.
        Assert.Contains("Layout detection ended with status 200", output, StringComparison.Ordinal);
        Assert.Contains("ms and 1 regions", output, StringComparison.Ordinal);

        // And nothing else: no image bytes (the sentinel sits inside the submitted PNG), no
        // detected content, and no raw runtime payload.
        Assert.DoesNotContain(Sentinel, output, StringComparison.Ordinal);
        Assert.DoesNotContain("one recoverable note", output, StringComparison.Ordinal);
    }

    private static byte[] BuildSentinelPng()
    {
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        png.Write([(byte)0, (byte)0, (byte)0, (byte)13]);
        png.Write("IHDR"u8);
        png.Write([(byte)0, (byte)0, (byte)0, (byte)100]);
        png.Write([(byte)0, (byte)0, (byte)0, (byte)100]);
        png.Write([8, 6, 0, 0, 0]);
        png.Write(new byte[4]);
        png.Write(System.Text.Encoding.ASCII.GetBytes(Sentinel));
        return png.ToArray();
    }

    private static MultipartFormDataContent CreateUpload(byte[] content)
    {
        var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
        var multipart = new MultipartFormDataContent();
        multipart.Add(fileContent, "file", "page.png");
        return multipart;
    }

    private static async Task<IssuedApiKey> CreateApiClientAsync(
        WebApplicationFactory<Program> factory,
        string scopes)
    {
        var clientId = Guid.NewGuid();
        var apiKey = ApiKeyCredential.Create(clientId);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
        database.ApiClients.Add(new ApiClientEntity
        {
            Id = clientId,
            Name = $"Layout log test client {clientId:N}",
            SecretHash = apiKey.SecretHash,
            Scopes = scopes,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await database.SaveChangesAsync();
        return apiKey;
    }

    private sealed class LoggingLayoutFactory : IDisposable
    {
        private readonly StructaDocWebApplicationFactory factory = new();

        public StubLayoutLogProvider Stub { get; } = new();

        public WebApplicationFactory<Program> Host { get; }

        public LoggingLayoutFactory()
        {
            Host = factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("LayoutRuntime:BaseUrl", "http://127.0.0.1:9/");
                builder.ConfigureServices(services =>
                {
                    foreach (var descriptor in services
                                 .Where(descriptor => descriptor.ServiceType == typeof(ILayoutDetectionProvider))
                                 .ToArray())
                    {
                        services.Remove(descriptor);
                    }

                    services.AddSingleton<ILayoutDetectionProvider>(Stub);
                });
            });
        }

        public HttpClient CreateClient() => Host.CreateClient();

        public void Dispose() => factory.Dispose();
    }

    private sealed class StubLayoutLogProvider : ILayoutDetectionProvider
    {
        public Func<LayoutDetectionRequest, CancellationToken, Task<LayoutDetectionResult>>? Handler { get; set; }

        public Task<LayoutDetectionResult> DetectLayoutAsync(
            LayoutDetectionRequest request,
            CancellationToken cancellationToken = default) =>
            Handler is null
                ? throw new InvalidOperationException("The stub provider has no handler configured.")
                : Handler(request, cancellationToken);
    }
}
