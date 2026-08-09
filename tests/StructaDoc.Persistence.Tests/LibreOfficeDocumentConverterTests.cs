using StructaDoc.Application.Conversion;
using StructaDoc.Platform.Conversion;

namespace StructaDoc.Persistence.Tests;

public sealed class LibreOfficeDocumentConverterTests
{
    private const string SpreadsheetMediaType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    [Fact]
    public void Supports_only_registered_office_formats_to_pdf()
    {
        using var environment = new ConverterTestEnvironment();

        Assert.True(environment.Converter.Supports(
            SpreadsheetMediaType,
            DocumentConversionMediaTypes.Pdf));
        Assert.False(environment.Converter.Supports(
            "application/pdf",
            DocumentConversionMediaTypes.Pdf));
        Assert.False(environment.Converter.Supports(
            SpreadsheetMediaType,
            "image/png"));
    }

    [Fact]
    public async Task Conversion_uses_isolated_profile_and_cleans_working_directory()
    {
        using var environment = new ConverterTestEnvironment();
        var source = "spreadsheet-content"u8.ToArray();

        string workingDirectory;
        await using (var result = await environment.Converter.ConvertAsync(
                         new DocumentConversionRequest(
                             SpreadsheetMediaType,
                             source.Length,
                             _ => Task.FromResult<Stream>(new MemoryStream(source, writable: false)))))
        {
            Assert.Equal("libreoffice", result.ConverterType);
            Assert.Equal("LibreOffice 25.2.4.2 520(Build:2)", result.ConverterVersion);
            Assert.Equal(DocumentConversionMediaTypes.Pdf, result.OutputMediaType);
            Assert.Equal("%PDF-1.7\nconverted"u8.Length, result.SizeBytes);
            workingDirectory = Assert.Single(environment.Runner.ConversionRequests).WorkingDirectory;
            Assert.True(Directory.Exists(workingDirectory));

            var request = environment.Runner.ConversionRequests[0];
            Assert.Contains("--headless", request.Arguments);
            Assert.Contains("--convert-to", request.Arguments);
            Assert.Contains("pdf", request.Arguments);
            Assert.Contains(request.Arguments, argument =>
                argument.StartsWith(
                    "-env:UserInstallation=file:",
                    StringComparison.Ordinal));
            Assert.DoesNotContain(request.Arguments, argument =>
                argument.Contains("spreadsheet-content", StringComparison.Ordinal));
        }

        Assert.False(Directory.Exists(workingDirectory));
    }

    [Fact]
    public async Task Oversized_input_is_rejected_before_starting_libreoffice()
    {
        using var environment = new ConverterTestEnvironment(maxInputBytes: 4);

        var exception = await Assert.ThrowsAsync<DocumentConversionException>(() =>
            environment.Converter.ConvertAsync(
                new DocumentConversionRequest(
                    SpreadsheetMediaType,
                    5,
                    _ => Task.FromResult<Stream>(new MemoryStream(new byte[5])))));

        Assert.Equal("document-conversion-input-too-large", exception.ErrorCode);
        Assert.Empty(environment.Runner.Requests);
    }

    [Fact]
    public async Task Invalid_pdf_output_is_rejected_and_cleaned()
    {
        using var environment = new ConverterTestEnvironment(validPdf: false);
        var source = "source"u8.ToArray();

        var exception = await Assert.ThrowsAsync<DocumentConversionException>(() =>
            environment.Converter.ConvertAsync(
                new DocumentConversionRequest(
                    SpreadsheetMediaType,
                    source.Length,
                    _ => Task.FromResult<Stream>(new MemoryStream(source, writable: false)))));

        Assert.Equal("document-conversion-output-invalid", exception.ErrorCode);
        var request = Assert.Single(environment.Runner.ConversionRequests);
        Assert.False(Directory.Exists(request.WorkingDirectory));
    }

    private sealed class ConverterTestEnvironment : IDisposable
    {
        private readonly string directoryPath;

        public ConverterTestEnvironment(
            long maxInputBytes = 1024,
            bool validPdf = true)
        {
            directoryPath = Path.Combine(
                Path.GetTempPath(),
                "structadoc-tests",
                Guid.NewGuid().ToString("N"));
            Runner = new TestProcessRunner(validPdf);
            Converter = new LibreOfficeDocumentConverter(
                new LibreOfficeConversionOptions
                {
                    ExecutablePath = "test-libreoffice",
                    TemporaryPath = directoryPath,
                    MaxInputBytes = maxInputBytes,
                    MaxOutputBytes = 1024,
                    MaxTemporaryBytes = 4096,
                },
                Runner);
        }

        public TestProcessRunner Runner { get; }

        public LibreOfficeDocumentConverter Converter { get; }

        public void Dispose()
        {
            Converter.Dispose();
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    private sealed class TestProcessRunner(bool validPdf) : ILibreOfficeProcessRunner
    {
        public List<LibreOfficeProcessRequest> Requests { get; } = [];

        public IReadOnlyList<LibreOfficeProcessRequest> ConversionRequests =>
            Requests.Where(request => request.Arguments.Contains("--convert-to")).ToArray();

        public async Task<LibreOfficeProcessResult> RunAsync(
            LibreOfficeProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (request.Arguments.Contains("--version"))
            {
                return new LibreOfficeProcessResult(
                    0,
                    "LibreOffice 25.2.4.2 520(Build:2)\n",
                    string.Empty);
            }

            var arguments = request.Arguments.ToArray();
            var outputIndex = Array.IndexOf(arguments, "--outdir");
            var outputDirectory = arguments[outputIndex + 1];
            await File.WriteAllBytesAsync(
                Path.Combine(outputDirectory, "source.pdf"),
                validPdf ? "%PDF-1.7\nconverted"u8.ToArray() : "not-pdf"u8.ToArray(),
                cancellationToken);
            return new LibreOfficeProcessResult(0, string.Empty, string.Empty);
        }
    }
}
