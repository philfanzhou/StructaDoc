using System.IO.Compression;
using System.Net;
using System.Text;
using StructaDoc.Application.Providers;

namespace StructaDoc.Persistence.Tests;

/// <summary>Result archives for the instrumented MinerU Cloud stub.</summary>
internal static class CloudStubArchives
{
    public static byte[] Fixture(string fileName) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MinerU", fileName));

    public static byte[] ZipOf(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    public static byte[] ZipOfBytes(params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                using var stream = entry.Open();
                stream.Write(content);
            }
        }

        return buffer.ToArray();
    }
}

internal sealed class StubCloudSource(LayoutCloudConfigurationSnapshot snapshot) : ILayoutCloudConfigurationSource
{
    public Task<LayoutCloudConfigurationSnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(snapshot);
}

/// <summary>
/// An instrumented MinerU Cloud: the batch, status, signed upload, and result routes of the
/// official protocol, and nothing else — any request to any other path fails the test here,
/// which is how "never the custom layout-runtime route" is enforced on what the adapter called
/// rather than on what the output happens to omit.
/// </summary>
internal sealed class CloudApiStub : HttpMessageHandler
{
    public const string BatchRoute = "https://mineru.example/api/v4/file-urls/batch";

    public const string StatusRoute = "https://mineru.example/api/v4/extract-results/batch/batch-1";

    public const string UploadUrl = "https://files.example/upload?signature=synthetic";

    public const string ResultUrl = "https://files.example/result.zip";

    private int inFlight;
    private int pollCount;

    public Dictionary<string, HttpStatusCode> FailLeg { get; } = [];

    public string? BatchResponse { get; init; }

    public IReadOnlyList<string> PollStates { get; init; } = ["waiting-file", "done"];

    public Func<int, string> PollState { get; init; } = null!;

    public bool DelayEveryRequest { get; set; }

    public TimeSpan? ResultDelay { get; init; }

    /// <summary>When set, the result route streams this many bytes instead of the ZIP.</summary>
    public long? ResultStreamLength { get; init; }

    public byte[] ResultZip { get; init; } = null!;

    public List<CapturedRequest> Requests { get; } = [];

    public int RequestCount => Requests.Count;

    public int PeakInFlight { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var captured = new CapturedRequest(
            request.Method.ToString(),
            request.RequestUri!,
            request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter,
            body,
            request.Content?.Headers.ContentLength,
            request.Content?.Headers.ContentType?.MediaType);
        Requests.Add(captured);

        var current = Interlocked.Increment(ref inFlight);
        PeakInFlight = Math.Max(PeakInFlight, current);
        try
        {
            if (DelayEveryRequest)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return await AnswerAsync(captured, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref inFlight);
        }
    }

    private async Task<HttpResponseMessage> AnswerAsync(
        CapturedRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Method == "POST" && request.Uri.AbsoluteUri == BatchRoute)
        {
            if (FailLeg.TryGetValue("batch", out var batchStatus))
            {
                return JsonResponse(batchStatus, """{"detail":"unavailable"}""");
            }

            return JsonResponse(
                HttpStatusCode.OK,
                BatchResponse
                    ?? "{\"code\": 0, \"data\": {\"batch_id\": \"batch-1\", \"file_urls\": [\""
                    + UploadUrl
                    + "\"]}}");
        }

        if (request.Method == "GET" && request.Uri.AbsoluteUri == StatusRoute)
        {
            if (FailLeg.TryGetValue("status", out var status))
            {
                return JsonResponse(status, """{"detail":"unavailable"}""");
            }

            var state = PollState is not null
                ? PollState(pollCount)
                : pollCount < PollStates.Count ? PollStates[pollCount] : "done";
            pollCount++;
            var payload = state == "done"
                ? "{\"code\": 0, \"data\": {\"extract_result\": [{\"file_name\": \"page.png\", "
                    + "\"state\": \"done\", \"full_zip_url\": \"" + ResultUrl + "\"}]}}"
                : "{\"code\": 0, \"data\": {\"extract_result\": [{\"file_name\": \"page.png\", "
                    + "\"state\": \"" + state + "\"}]}}";
            return JsonResponse(HttpStatusCode.OK, payload);
        }

        if (request.Method == "PUT" && request.Uri.AbsoluteUri == UploadUrl)
        {
            if (FailLeg.TryGetValue("upload", out var uploadStatus))
            {
                return new HttpResponseMessage(uploadStatus);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        if (request.Method == "GET" && request.Uri.AbsoluteUri == ResultUrl)
        {
            if (FailLeg.TryGetValue("result", out var resultStatus))
            {
                return new HttpResponseMessage(resultStatus);
            }

            if (ResultDelay is { } delay)
            {
                await Task.Delay(delay, cancellationToken);
            }

            return ZipResponse();
        }

        Assert.Fail(
            $"The cloud adapter called '{request.Method} {request.Uri.AbsoluteUri}', "
                + "which is not part of the official cloud extraction protocol.");
        return null!;
    }

    private HttpResponseMessage ZipResponse()
    {
        if (ResultStreamLength is { } length)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FixedLengthStream(length)),
            };
        }

        var content = new ByteArrayContent(ResultZip is not null ? ResultZip : DefaultResultZip());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static byte[] DefaultResultZip() => CloudStubArchives.ZipOfBytes(
        ("layout.json", CloudStubArchives.Fixture("cloud-layout-worksheet.json")),
        ("full.md", "Synthetic worksheet"u8.ToArray()),
        ("page_model.json", "{}"u8.ToArray()),
        ("page_content_list.json", "[]"u8.ToArray()));

    private sealed class FixedLengthStream : Stream
    {
        private readonly long length;
        private long remaining;

        public FixedLengthStream(long length)
        {
            this.length = length;
            remaining = length;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var chunk = (int)Math.Min(count, remaining);
            remaining -= chunk;
            return chunk;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal sealed record CapturedRequest(
    string Method,
    Uri Uri,
    string? AuthorizationScheme,
    string? AuthorizationParameter,
    byte[]? Body,
    long? ContentLength,
    string? ContentType);
