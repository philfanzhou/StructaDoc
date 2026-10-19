using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace StructaDoc.Host.Tests;

/// <summary>
/// A MinerU Local service far enough to be parsed against, over a real socket.
///
/// The adapters under test exist to speak HTTP: they build a multipart body, read a task ID out of
/// a JSON response, poll a status route, and stream a ZIP back. Substituting an in-process fake for
/// <see cref="StructaDoc.Application.Providers.IParseProvider"/> leaves every one of those steps
/// untested, which is exactly the part a deployment finds out about first.
/// </summary>
internal sealed class StubMinerUServer : IAsyncDisposable
{
    public const string TaskId = "stub-task-0001";
    public const string MarkdownHeading = "StructaDoc end-to-end";
    public const string MarkdownBody = "Parsed by the stub MinerU service.";
    public const string ImageCaption = "Figure 1 Stub figure";
    public const string ImageFootnote = "Drawn by the stub.";

    private readonly WebApplication app;
    private int statusRequests;

    private StubMinerUServer(WebApplication app, string baseUrl)
    {
        this.app = app;
        BaseUrl = baseUrl;
    }

    /// <summary>The address to configure a <c>mineru-local</c> Provider with.</summary>
    public string BaseUrl { get; }

    public int SubmitRequests { get; private set; }

    public int StatusRequests => statusRequests;

    public int ResultRequests { get; private set; }

    /// <summary>The file name the adapter put in the multipart body.</summary>
    public string? ReceivedFileName { get; private set; }

    /// <summary>How many bytes of document actually crossed the socket.</summary>
    public long ReceivedFileBytes { get; private set; }

    public string? ReceivedContentType { get; private set; }

    public string? ReceivedBackend { get; private set; }

    public string? ReceivedAuthorization { get; private set; }

    /// <param name="runningResponses">
    /// How many times the status route answers <c>processing</c> before <c>completed</c>, so the
    /// poll loop is exercised rather than skipped by an immediate success.
    /// </param>
    /// <param name="rejectSubmission">
    /// Answers a submission with <c>400</c>, which is how a Provider reports a document it will
    /// never accept. The adapter has to read that as permanent rather than retry it.
    /// </param>
    public static async Task<StubMinerUServer> StartAsync(
        int runningResponses = 1,
        bool rejectSubmission = false,
        bool v4 = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();
        StubMinerUServer? server = null;
        var archive = CreateResultArchive(v4);
        var prefix = v4 ? "/v1/tasks" : "/tasks";

        app.MapPost(prefix, async (HttpRequest request) =>
        {
            var current = server!;
            current.SubmitRequests++;
            current.ReceivedAuthorization = request.Headers.Authorization.ToString();

            if (!request.HasFormContentType)
            {
                return Results.BadRequest();
            }

            var form = await request.ReadFormAsync();
            var file = form.Files["files"];
            if (file is null || rejectSubmission
                || v4 && (form.Count != 0 || form.Files.Count != 1
                    || request.Query["tier"] != "flash"
                    || request.Query["ocr_mode"] != "auto"
                    || request.Query["output_formats"] != "zip"))
            {
                return Results.BadRequest();
            }

            current.ReceivedFileName = file.FileName;
            current.ReceivedContentType = file.ContentType;
            current.ReceivedBackend = v4 ? request.Query["tier"].ToString() : form["backend"].ToString();

            // Counted rather than trusted from a header, so a body that was announced but never
            // sent cannot pass for a document that arrived.
            await using var content = file.OpenReadStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await content.ReadAsync(buffer)) > 0)
            {
                current.ReceivedFileBytes += read;
            }

            // MinerU Local answers a submission with 202 and nothing else; the adapter refuses any
            // other success code.
            return Results.Json(new { task_id = TaskId }, statusCode: StatusCodes.Status202Accepted);
        });

        app.MapGet(prefix + "/{id}", (string id) =>
        {
            var current = server!;
            var attempt = Interlocked.Increment(ref current.statusRequests);
            return id == TaskId
                ? Results.Json(new
                {
                    task_id = TaskId,
                    status = attempt <= runningResponses ? (v4 ? "running" : "processing") : "completed",
                    files = new[] { new { status = attempt <= runningResponses ? "running" : "completed" } },
                    status_url = "https://untrusted.invalid/status",
                    result_url = "https://untrusted.invalid/result",
                })
                : Results.NotFound();
        });

        app.MapGet(prefix + "/{id}/result", (string id, HttpRequest request) =>
        {
            var current = server!;
            current.ResultRequests++;
            return id == TaskId && (!v4 || request.Query["response_format"] == "zip")
                ? Results.File(archive, "application/zip", "result.zip")
                : Results.NotFound();
        });

        await app.StartAsync();
        server = new StubMinerUServer(app, app.Urls.First().TrimEnd('/'));
        return server;
    }

    /// <summary>
    /// The observed MinerU layout the normalizer recognizes: a Markdown rendering, a content list
    /// that becomes Blocks and Pages, and an image an image Block points at.
    /// </summary>
    internal static byte[] CreateResultArchive(bool v4 = false)
    {
        // A 1x1 PNG. The normalizer identifies an Asset by its signature rather than by extension,
        // so bytes that only claim to be an image would be dropped.
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, v4 ? "markdown.md" : "full.md", Encoding.UTF8.GetBytes($"# {MarkdownHeading}\n\n{MarkdownBody}\n"));
            if (v4)
            {
                // Synthetic physical PDF output in the observed 4.0.12 renderer shape; no inference claim.
                Write(archive, "structured_content.json", Encoding.UTF8.GetBytes($$$"""
                {"metadata":{"file_suffix":"pdf","document":{"page_count_kind":"physical"}},"pages":[
                  {"page_idx":0,"blocks":[{"type":"doc_title","level":1,"content":"{{{MarkdownHeading}}}"},{"type":"text","content":"{{{MarkdownBody}}}"}]},
                  {"page_idx":1,"blocks":[{"type":"image","content":"","image_source":"images/figure-1.png","captions":[{"content":"{{{ImageCaption}}}"}],"footnotes":[{"content":"{{{ImageFootnote}}}"}]}]}
                ]}
                """));
                Write(archive, "middle_json.json", "{}"u8.ToArray());
                Write(archive, "model_output.json", "{}"u8.ToArray());
            }
            else
            {
                Write(
                    archive,
                    "content_list.json",
                    Encoding.UTF8.GetBytes($$"""
                [
                  { "type": "text", "text": "{{MarkdownHeading}}", "text_level": 1, "page_idx": 0 },
                  { "type": "text", "text": "{{MarkdownBody}}", "page_idx": 0 },
                  { "type": "image", "img_path": "images/figure-1.png", "image_caption": ["{{ImageCaption}}"], "image_footnote": ["{{ImageFootnote}}"], "page_idx": 1 }
                ]
                """));
            }
            Write(archive, "images/figure-1.png", png);
        }

        return buffer.ToArray();
    }

    private static void Write(ZipArchive archive, string path, byte[] content)
    {
        using var entry = archive.CreateEntry(path).Open();
        entry.Write(content);
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
