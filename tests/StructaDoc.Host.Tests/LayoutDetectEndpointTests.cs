using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StructaDoc.Adapters.Authentication;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Providers;
using StructaDoc.Contracts.Layout;
using StructaDoc.Host.Layout;

namespace StructaDoc.Host.Tests;

// The public non-archiving layout endpoint (ADR-0013). What these tests hold is the whole outcome
// model recorded on Issue #194: every row of the event/result/response/persistence matrix, with
// "persists nothing" verified end to end against the real host and the real stores rather than
// structurally — the proof the merged contract's architecture test defers to this task. The
// runtime behind the exchange is a stub provider registered over the adapter, so no request
// leaves the process and no stub answer says anything about the MinerU wire protocol.
public sealed class LayoutDetectEndpointTests
{
    private const string Route = "/api/v1/layout/detect";

    [Fact]
    public async Task A_supported_image_returns_normalized_regions_and_persists_nothing()
    {
        using var factory = new LayoutRuntimeFactory();
        factory.Stub.Handler = (_, _) => Task.FromResult(SuccessResult());
        using var client = factory.CreateClient();
        var apiKey = await factory.CreateApiClientAsync(AuthenticationScopes.LayoutDetect);
        var countsBefore = await TakeCountsAsync(factory.Host);
        var image = CreatePng(2480, 3508, trailing: "LAYOUT-LOG-SENTINEL"u8.ToArray());

        using var response = await client.PostWithApiKeyAsync(
            Route,
            CreateUpload(image, "page.png", "image/png"),
            apiKey,
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The full documented response shape: request identifier, runtime dimensions, the
        // explicit coordinate frame, response-local region IDs, labels and boxes in canonical
        // coordinates, engine information, and the bounded diagnostics slot.
        Assert.Equal(
            GetHeader(response, "x-correlation-id"),
            body.GetProperty("requestId").GetString());
        Assert.Equal(2480, body.GetProperty("width").GetInt32());
        Assert.Equal(3508, body.GetProperty("height").GetInt32());
        Assert.Equal(
            LayoutDetectResponse.CanonicalCoordinateFrame,
            body.GetProperty("coordinateFrame").GetString());
        // A PNG carries no EXIF orientation, and the response says so with its absence.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("orientation").ValueKind);
        var regions = body.GetProperty("regions").EnumerateArray().ToArray();
        Assert.Equal(2, regions.Length);
        Assert.Equal(0, regions[0].GetProperty("id").GetInt32());
        Assert.Equal("title", regions[0].GetProperty("label").GetString());
        Assert.Equal(0.125, regions[0].GetProperty("x0").GetDouble());
        Assert.Equal(0.24, regions[0].GetProperty("y0").GetDouble());
        Assert.Equal(0.875, regions[0].GetProperty("x1").GetDouble());
        Assert.Equal(0.42, regions[0].GetProperty("y1").GetDouble());
        Assert.Equal(1, regions[1].GetProperty("id").GetInt32());
        Assert.Equal("table", regions[1].GetProperty("label").GetString());
        Assert.Equal("mineru", body.GetProperty("engine").GetString());
        Assert.Equal("MinerU2.0-Layout-Detect", body.GetProperty("model").GetString());
        Assert.Equal("2.0.7", body.GetProperty("version").GetString());
        Assert.Equal(
            ["one recoverable note"],
            body.GetProperty("diagnostics").EnumerateArray().Select(note => note.GetString()).ToArray());

        // Nothing recognized, extracted, or addressable rides along.
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("markdown", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", raw, StringComparison.OrdinalIgnoreCase);

        await AssertNothingPersistedAsync(factory.Host, countsBefore);
        AssertNoTemporaryFiles(factory);
    }

    [Fact]
    public async Task An_empty_page_is_a_documented_success_with_no_regions()
    {
        using var factory = new LayoutRuntimeFactory();
        factory.Stub.Handler = (_, _) => Task.FromResult(new LayoutDetectionResult([], 100, 100));
        using var client = factory.CreateClient();
        var apiKey = await factory.CreateApiClientAsync(AuthenticationScopes.LayoutDetect);
        var countsBefore = await TakeCountsAsync(factory.Host);

        using var response = await client.PostWithApiKeyAsync(
            Route,
            CreateUpload(CreatePng(100, 100), "page.png", "image/png"),
            apiKey,
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: TestContext.Current.CancellationToken);

        // The runtime looked and found nothing: a real answer, carried as an empty list.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, body.GetProperty("regions").GetArrayLength());
        await AssertNothingPersistedAsync(factory.Host, countsBefore);
    }

    [Fact]
    public async Task A_jpeg_reports_its_exif_orientation()
    {
        using var factory = new LayoutRuntimeFactory();
        factory.Stub.Handler = (_, _) => Task.FromResult(SuccessResult());
        using var client = factory.CreateClient();
        var apiKey = await factory.CreateApiClientAsync(AuthenticationScopes.LayoutDetect);
        var countsBefore = await TakeCountsAsync(factory.Host);

        using var response = await client.PostWithApiKeyAsync(
            Route,
            CreateUpload(CreateJpeg(2480, 3508, orientation: 6), "page.jpg", "image/jpeg"),
            apiKey,
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Orientation 6 is one of the transposing values (5–8); the response carries it so the
        // consumer maps the normalized boxes back onto the original image correctly.
        Assert.Equal(6, body.GetProperty("orientation").GetInt32());
        await AssertNothingPersistedAsync(factory.Host, countsBefore);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("image/jpeg")]
    public async Task A_declared_type_that_contradicts_the_signature_is_refused(string declaredType)
    {
        using var factory = new LayoutRuntimeFactory();
        factory.Stub.Handler = (_, _) => Task.FromResult(SuccessResult());
        using var client = factory.CreateClient();
        var apiKey = await factory.CreateApiClientAsync(AuthenticationScopes.LayoutDetect);
        var countsBefore = await TakeCountsAsync(factory.Host);

        // Valid PNG bytes behind a wrong or unusable declared type: the signature decides.
        using var response = await client.PostWithApiKeyAsync(
            Route,
            CreateUpload(CreatePng(100, 100), "page.png", declaredType),
            apiKey,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        await AssertProblemCodeAsync(response, "layout-media-type-unsupported");
        Assert.Equal(0, factory.Stub.RequestCount);
        await AssertNothingPersistedAsync(factory.Host, countsBefore);
    }

    [Fact]
    public async Task An_unsupported_or_malformed_image_is_refused()
    {
        using var factory = new LayoutRuntimeFactory();
        factory.Stub.Handler = (_, _) => Task.FromResult(SuccessResult());
        using var client = factory.CreateClient();
        var apiKey = await factory.CreateApiClientAsync(AuthenticationScopes.LayoutDetect);
        var countsBefore = await TakeCountsAsync(factory.Host);

        // Neither of the two signatures; a truncated JPEG (SOI with no frame header) answers the
        // same way.
        foreach (var image in new[]
                 {
                     "GIF89a not an image at all"u8.ToArray(),
                     new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 },
                 })
        {
            using var response = await client.PostWithApiKeyAsync(
                Route,
                CreateUpload(image, "page.bin", "application/octet-stream"),
                apiKey,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
            await AssertProblemCodeAsync(response, "layout-media-type-unsupported");
        }

        Assert.Equal(0, factory.Stub.RequestCount);
        await AssertNothingPersistedAsync(factory.Host, countsBefore);
    }

    [Fact]
    public async Task Pixel_bounds_are_enforced_against_the_orientation_adjusted_size()
    {
        using var factory = new LayoutRuntimeFactory();
        factory.Stub.Handler = (_, _) => Task.FromResult(SuccessResult());
        using var client = factory.CreateClient();
        var apiKey = await factory.CreateApiClientAsync(AuthenticationScopes.LayoutDetect);
        var countsBefore = await TakeCountsAsync(factory.Host);

        // One dimension past the per-dimension bound, and a per-dimension-valid pair past the
        // total bound. The orientation-adjusted case — a transposing orientation swapping a
        // stored oversized dimension out of the bound — passes, because the bound is checked
        // against the size the image presents, not the stored one.
        var oversizedDimension = CreatePng(32_001, 100);
        var oversizedTotal = CreatePng(20_000, 20_000);
        var swappedByOrientation = CreateJpeg(31_000, 100, orientation: 6);

        foreach (var image in new[] { oversizedDimension, oversizedTotal })
        {
            using var response = await client.PostWithApiKeyAsync(
                Route,
                CreateUpload(image, "page.png", "image/png"),
                apiKey,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            await AssertProblemCodeAsync(response, "layout-input-too-large");
        }

        using var oriented = await client.PostWithApiKeyAsync(
            Route,
            CreateUpload(swappedByOrientation, "page.jpg", "image/jpeg"),
            apiKey,
            TestContext.Current.CancellationToken);

        // Stored 31,000 × 100 with orientation 6 presents as 100 × 31,000 — inside both bounds.
        Assert.Equal(HttpStatusCode.OK, oriented.StatusCode);
        Assert.Equal(1, factory.Stub.RequestCount);
        await AssertNothingPersistedAsync(factory.Host, countsBefore);
    }

    [Fact]
    public async Task A_byte_bound_violation_is_refused_before_execution()
    {
        using var factory = new LayoutRuntimeFactory();
        factory.Stub.Handler = (_, _) => Task.FromResult(SuccessResult());
        using var client = factory.CreateClient();
        var apiKey = await factory.CreateApiClientAsync(AuthenticationScopes.LayoutDetect);
        var countsBefore = await TakeCountsAsync(factory.Host);

        using var response = await client.PostWithApiKeyAsync(
            Route,
            CreateUpload(
                new byte[LayoutDetectionRequest.MaximumImageBytes + 1],
                "page.png",
                "image/png"),
            apiKey,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await AssertProblemCodeAsync(response, "layout-input-too-large");
        Assert.Equal(0, factory.Stub.RequestCount);
        await AssertNothingPersistedAsync(factory.Host, countsBefore);
    }

    [Fact]
    public async Task An_image_exactly_at_the_byte_bound_is_accepted()
    {
        using var factory = new LayoutRuntimeFactory();
        factory.Stub.Handler = (_, _) => Task.FromResult(SuccessResult());
        using var client = factory.CreateClient();
        var apiKey = await factory.CreateApiClientAsync(AuthenticationScopes.LayoutDetect);

        var header = CreatePng(100, 100);
        var image = new byte[LayoutDetectionRequest.MaximumImageBytes];
        header.CopyTo(image, 0);

        using var response = await client.PostWithApiKeyAsync(
            Route,
            CreateUpload(image, "page.png", "image/png"),
            apiKey,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("not-multipart")]
    [InlineData("missing-file")]
    [InlineData("wrong-field")]
    [InlineData("two-files")]
    public async Task An_invalid_multipart_request_is_refused_before_the_body_reaches_execution(string shape)
    {
        using var factory = new LayoutRuntimeFactory();
        factory.Stub.Handler = (_, _) => Task.FromResult(SuccessResult());
        using var client = factory.CreateClient();
        var apiKey = await factory.CreateApiClientAsync(AuthenticationScopes.LayoutDetect);
        var countsBefore = await TakeCountsAsync(factory.Host);

        using HttpContent content = shape == "not-multipart"
            ? new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            : CreateShapedUpload(shape, CreatePng(100, 100));
        using var response = await client.PostWithApiKeyAsync(
            Route,
            content,
            apiKey,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemCodeAsync(response, "layout-request-invalid");
        Assert.Equal(0, factory.Stub.RequestCount);
        await AssertNothingPersistedAsync(factory.Host, countsBefore);
    }

    [Fact]
    public async Task An_unconfigured_runtime_answers_unsupported_capability()
    {
        // The shipped state: no LayoutRuntime base URL, so the capability resolution refuses and
        // nothing falls back to document parsing.
        using var factory = new StructaDocWebApplicationFactory();
        using var client = factory.CreateClient();
        var apiKey = await CreateApiClientAsync(factory, AuthenticationScopes.LayoutDetect);
        var countsBefore = await TakeCountsAsync(factory);

        using var response = await client.PostWithApiKeyAsync(
            Route,
            CreateUpload(CreatePng(100, 100), "page.png", "image/png"),
            apiKey,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await AssertProblemCodeAsync(response, "layout-capability-unsupported");
        await AssertNothingPersistedAsync(factory, countsBefore);
    }

    [Theory]
    [InlineData("mineru-layout-timeout", ProviderFailureCategory.Transient, HttpStatusCode.GatewayTimeout, "layout-timeout")]
    [InlineData("mineru-layout-network-error", ProviderFailureCategory.Transient, HttpStatusCode.BadGateway, "layout-runtime-unavailable")]
    [InlineData("mineru-layout-response-invalid", ProviderFailureCategory.Permanent, HttpStatusCode.BadGateway, "layout-output-invalid")]
    [InlineData("mineru-layout-response-too-large", ProviderFailureCategory.Permanent, HttpStatusCode.BadGateway, "layout-output-invalid")]
    [InlineData("mineru-layout-http-401", ProviderFailureCategory.Configuration, HttpStatusCode.ServiceUnavailable, "layout-runtime-unavailable")]
    [InlineData("mineru-layout-http-400", ProviderFailureCategory.Input, HttpStatusCode.BadGateway, "layout-runtime-rejected")]
    public async Task Every_provider_failure_maps_to_its_stable_http_outcome(
        string errorCode,
        ProviderFailureCategory category,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        using var factory = new LayoutRuntimeFactory();
        factory.Stub.Handler = (_, _) => throw new ProviderException(
            errorCode,
            "A bounded, sanitized failure message.",
            category);
        using var client = factory.CreateClient();
        var apiKey = await factory.CreateApiClientAsync(AuthenticationScopes.LayoutDetect);
        var countsBefore = await TakeCountsAsync(factory.Host);

        using var response = await client.PostWithApiKeyAsync(
            Route,
            CreateUpload(CreatePng(100, 100), "page.png", "image/png"),
            apiKey,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, response.StatusCode);
        await AssertProblemCodeAsync(response, expectedCode);
        await AssertNothingPersistedAsync(factory.Host, countsBefore);
    }

    [Fact]
    public async Task The_in_flight_cap_rejects_excess_requests_before_execution()
    {
        using var factory = new LayoutRuntimeFactory();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Stub.Handler = async (_, cancellationToken) =>
        {
            await release.Task.WaitAsync(cancellationToken);
            return SuccessResult();
        };
        using var client = factory.CreateClient();
        var apiKey = await factory.CreateApiClientAsync(AuthenticationScopes.LayoutDetect);

        var requests = Enumerable
            .Range(0, LayoutDetectInFlightGate.Capacity + 1)
            .Select(_ => client.PostWithApiKeyAsync(
                Route,
                CreateUpload(CreatePng(100, 100), "page.png", "image/png"),
                apiKey,
                TestContext.Current.CancellationToken))
            .ToArray();

        // Wait until the cap is genuinely full — the stub holds one in-flight execution per
        // taken slot — and then let every request settle.
        await WaitForAsync(
            () => factory.Stub.PeakInFlight >= LayoutDetectInFlightGate.Capacity,
            TimeSpan.FromSeconds(30));
        release.TrySetResult();
        var responses = await Task.WhenAll(requests);

        var statuses = responses.Select(response => response.StatusCode).OrderBy(code => code).ToArray();
        Assert.Equal(
            Enumerable.Repeat(HttpStatusCode.OK, LayoutDetectInFlightGate.Capacity)
                .Append(HttpStatusCode.TooManyRequests)
                .OrderBy(code => code)
                .ToArray(),
            statuses);

        // The rejected request was answered before execution began, with a retry hint.
        var rejected = responses.Single(response => response.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.Equal("1", GetHeader(rejected, "Retry-After"));
        Assert.Equal(LayoutDetectInFlightGate.Capacity, factory.Stub.RequestCount);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Caller_cancellation_closes_the_connection_and_frees_the_slot()
    {
        using var factory = new LayoutRuntimeFactory();
        factory.Stub.Handler = async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return SuccessResult();
        };
        using var client = factory.CreateClient();
        var apiKey = await factory.CreateApiClientAsync(AuthenticationScopes.LayoutDetect);
        var countsBefore = await TakeCountsAsync(factory.Host);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PostWithApiKeyAsync(
            Route,
            CreateUpload(CreatePng(100, 100), "page.png", "image/png"),
            apiKey,
            cancellation.Token));

        // Cancellation propagates rather than becoming a Provider error, and the slot it held is
        // free again: the next request runs immediately.
        factory.Stub.Handler = (_, _) => Task.FromResult(SuccessResult());
        using var followUp = await client.PostWithApiKeyAsync(
            Route,
            CreateUpload(CreatePng(100, 100), "page.png", "image/png"),
            apiKey,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, followUp.StatusCode);
        await AssertNothingPersistedAsync(factory.Host, countsBefore);
    }

    [Fact]
    public async Task The_scope_grants_the_endpoint_and_nothing_else_does()
    {
        using var factory = new LayoutRuntimeFactory();
        factory.Stub.Handler = (_, _) => Task.FromResult(SuccessResult());
        using var client = factory.CreateClient();

        // No credential: 401.
        using var anonymous = await client.PostAsync(
            Route,
            CreateUpload(CreatePng(100, 100), "page.png", "image/png"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        // An interactive administrator session: the policy deliberately does not admit it — the
        // four document/parse policies would, and this one must not.
        await client.LoginAsAdministratorAsync();
        using var interactive = await client.PostAsync(
            Route,
            CreateUpload(CreatePng(100, 100), "page.png", "image/png"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, interactive.StatusCode);

        // Every other scope, all four of them together, is still not layout:detect.
        var otherScopes = string.Join(
            ',',
            AuthenticationScopes.DocumentsRead,
            AuthenticationScopes.DocumentsWrite,
            AuthenticationScopes.ParsesRead,
            AuthenticationScopes.ParsesWrite);
        var broadKey = await factory.CreateApiClientAsync(otherScopes);
        using var broad = await client.PostWithApiKeyAsync(
            Route,
            CreateUpload(CreatePng(100, 100), "page.png", "image/png"),
            broadKey,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, broad.StatusCode);
        Assert.Equal(0, factory.Stub.RequestCount);
    }

    private static async Task AssertProblemCodeAsync(HttpResponseMessage response, string code)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(code, body.GetProperty("code").GetString());
    }

    private static string GetHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.Single() : string.Empty;

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        Assert.Fail("The condition was not reached within the timeout.");
    }

    private static LayoutDetectionResult SuccessResult() => new(
        [
            new LayoutDetectionRegion("title", 0.125, 0.24, 0.875, 0.42),
            new LayoutDetectionRegion("table", 0.1, 0.5, 0.95, 0.9),
        ],
        2480,
        3508,
        "mineru",
        "MinerU2.0-Layout-Detect",
        "2.0.7",
        ["one recoverable note"]);

    /// <summary>
    /// A minimal PNG: signature plus the IHDR chunk the endpoint's bounded parse reads. The CRC
    /// is zeroed because nothing on the layout path verifies it — full decoding is the runtime's
    /// job — and the trailing bytes let a test carry a sentinel the logs must never contain.
    /// </summary>
    private static byte[] CreatePng(int width, int height, byte[]? trailing = null)
    {
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteInt32BigEndian(png, 13);
        png.Write("IHDR"u8);
        WriteInt32BigEndian(png, width);
        WriteInt32BigEndian(png, height);
        png.Write([8, 6, 0, 0, 0]);
        png.Write(new byte[4]);
        if (trailing is not null)
        {
            png.Write(trailing);
        }

        return png.ToArray();
    }

    /// <summary>
    /// A minimal JPEG: SOI, an APP1 EXIF segment carrying exactly one IFD0 entry when an
    /// orientation is asked for, then SOF0 with the stored dimensions. The scan never needs to be
    /// real — the parse stops at the frame header.
    /// </summary>
    private static byte[] CreateJpeg(int width, int height, int? orientation = null)
    {
        using var jpeg = new MemoryStream();
        jpeg.Write([0xFF, 0xD8]);
        if (orientation is { } value)
        {
            using var tiff = new MemoryStream();
            tiff.Write("MM"u8);
            WriteUInt16(tiff, 0x2A);
            WriteInt32(tiff, 8);
            WriteUInt16(tiff, 1);
            WriteUInt16(tiff, 0x0112);
            WriteUInt16(tiff, 3);
            WriteInt32(tiff, 1);
            WriteUInt16(tiff, (ushort)value);
            WriteUInt16(tiff, 0);
            WriteInt32(tiff, 0);

            jpeg.Write([0xFF, 0xE1]);
            WriteUInt16(jpeg, checked((ushort)(6 + tiff.Length + 2)));
            jpeg.Write("Exif\0\0"u8);
            jpeg.Write(tiff.ToArray());
        }

        jpeg.Write([0xFF, 0xC0]);
        WriteUInt16(jpeg, 17);
        jpeg.Write([8]);
        WriteUInt16(jpeg, height);
        WriteUInt16(jpeg, width);
        jpeg.Write([1, 0x22, 0, 2, 0x01, 0x11, 0, 2, 0x02, 0x11, 1]);
        return jpeg.ToArray();
    }

    private static void WriteInt32BigEndian(Stream stream, int value)
    {
        stream.WriteByte((byte)((value >> 24) & 0xFF));
        stream.WriteByte((byte)((value >> 16) & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
        stream.WriteByte((byte)(value & 0xFF));
    }

    private static void WriteUInt16(Stream stream, int value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value & 0xFF));
    }

    private static void WriteInt32(Stream stream, int value)
    {
        stream.WriteByte((byte)((value >> 24) & 0xFF));
        stream.WriteByte((byte)((value >> 16) & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
        stream.WriteByte((byte)(value & 0xFF));
    }

    private static MultipartFormDataContent CreateUpload(
        byte[] content,
        string fileName,
        string mediaType)
    {
        var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        var multipart = new MultipartFormDataContent();
        multipart.Add(fileContent, "file", fileName);
        return multipart;
    }

    private static MultipartFormDataContent CreateShapedUpload(string shape, byte[] image) => shape switch
    {
        "missing-file" => new MultipartFormDataContent
        {
            { new StringContent("a form field instead"), "note" },
        },
        "wrong-field" => new MultipartFormDataContent
        {
            { new ByteArrayContent(image), "image", "page.png" },
        },
        "two-files" => CreateTwoFileUpload(image),
        _ => throw new NotSupportedException($"The upload shape '{shape}' has no builder."),
    };

    private static MultipartFormDataContent CreateTwoFileUpload(byte[] image)
    {
        var multipart = new MultipartFormDataContent();
        multipart.Add(new ByteArrayContent(image), "file", "first.png");
        multipart.Add(new ByteArrayContent(image), "file", "second.png");
        return multipart;
    }

    private static async Task<Dictionary<string, long>> TakeCountsAsync(
        WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
        var cancellation = TestContext.Current.CancellationToken;
        var storageRoot = factory.Services
            .GetRequiredService<IConfiguration>()
            .GetValue<string?>("Storage:RootPath");
        var storage = storageRoot is not null && Directory.Exists(storageRoot)
            ? Directory.GetFiles(storageRoot, "*", SearchOption.AllDirectories).LongLength
            : 0;
        return new Dictionary<string, long>
        {
            ["documents"] = await database.Documents.CountAsync(cancellation),
            ["parseRuns"] = await database.ParseRuns.CountAsync(cancellation),
            ["parsePages"] = await database.ParsePages.CountAsync(cancellation),
            ["parseBlocks"] = await database.ParseBlocks.CountAsync(cancellation),
            ["parseAssets"] = await database.ParseAssets.CountAsync(cancellation),
            ["parseArtifacts"] = await database.ParseArtifacts.CountAsync(cancellation),
            ["parseSegments"] = await database.ParseSegments.CountAsync(cancellation),
            ["cleanupJobs"] = await database.CleanupJobs.CountAsync(cancellation),
            ["accessGrants"] = await database.DocumentAccessGrants.CountAsync(cancellation),
            ["storageObjects"] = storage,
        };
    }

    private static async Task AssertNothingPersistedAsync(
        WebApplicationFactory<Program> factory,
        Dictionary<string, long> before)
    {
        var after = await TakeCountsAsync(factory);
        foreach (var (key, count) in before)
        {
            Assert.True(
                after.TryGetValue(key, out var actual) && actual == count,
                $"The layout path changed the {key} count from {count} to {after.GetValueOrDefault(key)}.");
        }
    }

    private static void AssertNoTemporaryFiles(LayoutRuntimeFactory factory)
    {
        // The buffered multipart read must create no temporary files. The stub provider observes
        // the process temporary directory while the request is executing — the moment a spilled
        // form buffer would exist — and holds what it saw for this assertion.
        Assert.All(
            factory.Stub.TemporaryFilesSeenDuringExecution,
            path => Assert.Fail($"The layout path created a temporary file: {path}"));
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
            Name = $"Layout test client {clientId:N}",
            SecretHash = apiKey.SecretHash,
            Scopes = scopes,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await database.SaveChangesAsync();
        return apiKey;
    }

    /// <summary>
    /// The test host with a configured-but-stubbed layout runtime: the registration path is the
    /// real one (bound <c>LayoutRuntime</c> section, adapter extension, registered configuration),
    /// with the one Provider implementation replaced so no request leaves the process.
    /// </summary>
    private sealed class LayoutRuntimeFactory : IDisposable
    {
        private readonly StructaDocWebApplicationFactory factory = new();

        public StubLayoutDetectionProvider Stub { get; } = new();

        public WebApplicationFactory<Program> Host { get; }

        public LayoutRuntimeFactory()
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

        public async Task<IssuedApiKey> CreateApiClientAsync(string scopes) =>
            await LayoutDetectEndpointTests.CreateApiClientAsync(Host, scopes);

        public void Dispose() => factory.Dispose();
    }

    /// <summary>
    /// The instrumented test layout provider: it counts requests, tracks how many are in flight
    /// at once, records the request-scoped state it can observe (the process temporary directory,
    /// for the no-temporary-files proof), and answers whatever the current test configured.
    /// </summary>
    private sealed class StubLayoutDetectionProvider : ILayoutDetectionProvider
    {
        private int inFlight;
        private int requestCount;
        private int peakInFlight;

        public Func<LayoutDetectionRequest, CancellationToken, Task<LayoutDetectionResult>>? Handler { get; set; }

        public int PeakInFlight => Volatile.Read(ref peakInFlight);

        public List<string> TemporaryFilesSeenDuringExecution { get; } = [];

        public async Task<LayoutDetectionResult> DetectLayoutAsync(
            LayoutDetectionRequest request,
            CancellationToken cancellationToken = default)
        {
            // Atomic counters: several requests are in flight at once in the overload test, and a
            // lost increment there would read as a missing or phantom execution.
            Interlocked.Increment(ref requestCount);
            var current = Interlocked.Increment(ref inFlight);
            int peak;
            do
            {
                peak = Volatile.Read(ref peakInFlight);
                if (current <= peak)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(ref peakInFlight, current, peak) != peak);

            TemporaryFilesSeenDuringExecution.AddRange(
                Directory.GetFiles(Path.GetTempPath(), "ASPNETBUF_*"));
            try
            {
                if (Handler is null)
                {
                    throw new InvalidOperationException("The stub provider has no handler configured.");
                }

                return await Handler(request, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        }

        public int RequestCount => requestCount;
    }
}

/// <summary>
/// Posts with an API client credential, the form every layout endpoint test reaches the endpoint
/// in: the Authorization header, not a cookie, because the endpoint is API-client only.
/// </summary>
internal static class LayoutDetectTestClientExtensions
{
    public static async Task<HttpResponseMessage> PostWithApiKeyAsync(
        this HttpClient client,
        string route,
        HttpContent content,
        IssuedApiKey apiKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("ApiKey", apiKey.Credential);
        return await client.SendAsync(request, cancellationToken);
    }
}
