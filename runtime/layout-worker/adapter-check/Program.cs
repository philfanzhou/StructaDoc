using System.Text.Json;
using StructaDoc.Adapters.Providers;
using StructaDoc.Application.Providers;

// Adapter-acceptance check for the StructaDoc layout worker: runs the real
// MinerULayoutDetectionProvider — the strict wire parser that rejects unknown
// properties — against a live worker and reports whether it accepted the
// worker's response. Run by tests/test_adapter_acceptance.py:
//
//   dotnet run --project adapter-check -- <base-url> <credential>
//
// Exit code 0 plus a one-line JSON summary on stdout means the adapter
// accepted the exchange; anything else is a failure with a message.

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("usage: StructaDoc.LayoutWorkerAdapterCheck <base-url> [credential]");
    return 2;
}

var baseUri = new Uri(args[0], UriKind.Absolute);
var credential = args.Length == 2 ? args[1] : null;

// A small real PNG (96x64, solid color), so the worker's decode path and the
// adapter's byte-level request encoding both run for real.
const string sampleImageBase64 =
    "iVBORw0KGgoAAAANSUhEUgAAAGAAAABACAIAAABqVuVZAAAAm0lEQVR4nO3SMRHAIAAEQYhqRCACqVEAZ2C3"
    + "/e7m59pncPc9NgRqHhQECgIFgYJAQaAgUBAoCBQECgIFgYJAQaAgUBAoCBQECgIFgYJAQaAgUBAoCBQECg"
    + "IFgYJAQaAgUBAoCBQECgIFgYJAQaAgUBAoCBQECgIFgYJAQaAgUBAoCBQECgIFgYJAQaAgUBAoCBQECgIF"
    + "gYJA4+0H5cgCJBMezLIAAAAASUVORK5CYII=";
var imageBytes = Convert.FromBase64String(sampleImageBase64);

try
{
    var result = await DetectAsync(baseUri, credential, imageBytes);

    // A second exchange with a deliberately wrong credential: the worker must
    // answer 401 and the adapter must surface it as a configuration failure,
    // proving the credential path works end to end in both directions.
    var wrongCredentialRejected = false;
    if (credential is not null)
    {
        try
        {
            await DetectAsync(baseUri, "definitely-the-wrong-credential", imageBytes);
        }
        catch (ProviderException exception) when (
            exception.ErrorCode == "mineru-layout-http-401"
            && exception.Category == ProviderFailureCategory.Configuration)
        {
            wrongCredentialRejected = true;
        }
    }

    var summary = new
    {
        accepted = true,
        regions = result.Regions.Count,
        width = result.SourceImageWidth,
        height = result.SourceImageHeight,
        engine = result.Engine,
        model = result.Model,
        version = result.Version,
        wrongCredentialRejected,
    };
    Console.WriteLine(JsonSerializer.Serialize(summary));
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"the adapter rejected the worker exchange: {exception.Message}");
    return 1;
}

static async Task<LayoutDetectionResult> DetectAsync(
    Uri baseUri,
    string? credential,
    byte[] imageBytes)
{
    using var httpClient = new HttpClient();
    var runtime = new LayoutRuntimeConfiguration(
        baseUri,
        credential is null ? null : new ProviderCredential(credential),
        TimeSpan.FromSeconds(30),
        maxConcurrency: 2);
    var provider = new MinerULayoutDetectionProvider(httpClient, runtime);
    return await provider.DetectLayoutAsync(new LayoutDetectionRequest(imageBytes, "image/png"));
}
