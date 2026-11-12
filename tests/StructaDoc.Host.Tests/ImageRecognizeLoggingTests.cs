using System.Net;
using StructaDoc.Application.Authentication;
using static StructaDoc.Host.Tests.LayoutDetectEndpointTests;

namespace StructaDoc.Host.Tests;

// ADR-0015 point 5: the recognition path's logs carry request identifiers, timing, counts, and
// status only — never image bytes or recognized text. A sentinel rides inside the submitted image
// and the recognized text is distinctive, and neither may appear in the captured console output
// while the completion event does. It shares the console-output collection because it swaps
// Console.Out.
[Collection("Console output")]
public sealed class ImageRecognizeLoggingTests
{
    private const string Sentinel = "RECOGNITION-LOG-SENTINEL-7C1D";

    [Fact]
    public async Task The_completion_log_carries_status_timing_and_counts_only()
    {
        using var factory = new ImageRecognizeEndpointTests.RecognitionFactory();
        factory.Stub.Handler = (_, _) => Task.FromResult(ImageRecognizeEndpointTests.SuccessResult());
        using var client = factory.Host.CreateClient();
        var apiKey = await factory.CreateApiClientAsync(AuthenticationScopes.ImageRecognize);
        var image = CreatePng(100, 100, trailing: System.Text.Encoding.ASCII.GetBytes(Sentinel));

        var originalOutput = Console.Out;
        using var capture = new ChainingConsoleWriter(originalOutput);
        Console.SetOut(capture);
        try
        {
            using var response = await client.PostWithApiKeyAsync(
                "/api/v1/image/recognize",
                CreateUpload(image, "page.png", "image/png"),
                apiKey,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        var output = capture.Snapshot();
        Assert.Contains("Image recognition ended with status 200", output, StringComparison.Ordinal);
        Assert.Contains("2 blocks, and 2 lines", output, StringComparison.Ordinal);

        Assert.DoesNotContain(Sentinel, output, StringComparison.Ordinal);
        Assert.DoesNotContain("apple", output, StringComparison.Ordinal);
        Assert.DoesNotContain("苹果", output, StringComparison.Ordinal);
        Assert.DoesNotContain("<table>", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Synthetic caption", output, StringComparison.Ordinal);
    }
}
