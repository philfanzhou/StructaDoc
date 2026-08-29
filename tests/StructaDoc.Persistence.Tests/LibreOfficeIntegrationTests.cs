using System.Security.Cryptography;
using StructaDoc.Adapters.Conversion;
using StructaDoc.Adapters.Documents;
using StructaDoc.Application.Conversion;

namespace StructaDoc.Persistence.Tests;

public sealed class LibreOfficeIntegrationTests
{
    public static TheoryData<string, string, string, string> LegacyOfficeFixtures =>
        new()
        {
            {
                "legacy-word.doc",
                ".doc",
                "application/msword",
                "b8d87ea2be74298009aaee9e11d26f05af6685857ef3ae40c42ffe3e3ce18c07"
            },
            {
                "legacy-spreadsheet.xls",
                ".xls",
                "application/vnd.ms-excel",
                "d854f881deb13ecd9fb274a24b75dcdf8999ee39c98cb7ef5f8168d9d85045bb"
            },
            {
                "legacy-presentation.ppt",
                ".ppt",
                "application/vnd.ms-powerpoint",
                "872012a18c6523cf852ef70759899fe2ab61a56fbd9e6bbb259e0eeba883fdcf"
            },
        };

    [LibreOfficeIntegrationTheory]
    [MemberData(nameof(LegacyOfficeFixtures))]
    public async Task Legacy_office_fixture_is_detected_and_converted_by_real_libreoffice(
        string fileName,
        string expectedExtension,
        string expectedMediaType,
        string expectedSha256)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "LibreOffice",
            fileName);
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "structadoc-libreoffice-integration-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);

        try
        {
            var originalSize = new FileInfo(fixturePath).Length;
            var originalSha256 = await ComputeSha256Async(fixturePath, cancellationToken);
            Assert.Equal(expectedSha256, originalSha256);

            await using (var fixture = File.OpenRead(fixturePath))
            {
                var detected = await new OfficeDocumentTypeDetector().DetectAsync(
                    fixture,
                    fileName,
                    cancellationToken);

                Assert.NotNull(detected);
                Assert.Equal(expectedExtension, detected.Extension);
                Assert.Equal(expectedMediaType, detected.MediaType);
            }

            var executablePath = Environment.GetEnvironmentVariable(
                "STRUCTADOC_LIBREOFFICE_EXECUTABLE") ?? "libreoffice";
            var processRunner = new LibreOfficeProcessRunner();
            var versionResult = await processRunner.RunAsync(
                new LibreOfficeProcessRequest(
                    executablePath,
                    ["--headless", "--version"],
                    temporaryRoot,
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromMilliseconds(100)),
                cancellationToken);
            Assert.Equal(0, versionResult.ExitCode);
            var expectedVersion = Assert.Single(
                versionResult.StandardOutput.Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            using var converter = new LibreOfficeDocumentConverter(
                new LibreOfficeConversionOptions
                {
                    ExecutablePath = executablePath,
                    TemporaryPath = temporaryRoot,
                    MaxInputBytes = 10L * 1024 * 1024,
                    MaxOutputBytes = 20L * 1024 * 1024,
                    MaxTemporaryBytes = 256L * 1024 * 1024,
                    Timeout = TimeSpan.FromMinutes(2),
                    ResourceInspectionInterval = TimeSpan.FromMilliseconds(100),
                },
                processRunner);

            string workingDirectory;
            await using (var result = await converter.ConvertAsync(
                             new DocumentConversionRequest(
                                 expectedMediaType,
                                 originalSize,
                                 _ => Task.FromResult<Stream>(File.OpenRead(fixturePath))),
                             cancellationToken))
            {
                Assert.Equal(LibreOfficeDocumentConverter.ConverterType, result.ConverterType);
                Assert.Equal(expectedVersion, result.ConverterVersion);
                Assert.Equal(DocumentConversionMediaTypes.Pdf, result.OutputMediaType);
                Assert.True(result.SizeBytes > 5);

                var signature = new byte[5];
                await result.Content.ReadExactlyAsync(signature, cancellationToken);
                Assert.Equal("%PDF-"u8.ToArray(), signature);

                workingDirectory = Assert.Single(Directory.GetDirectories(temporaryRoot));
                Assert.True(Directory.Exists(workingDirectory));
            }

            Assert.False(Directory.Exists(workingDirectory));
            Assert.Empty(Directory.GetDirectories(temporaryRoot));
            Assert.Equal(originalSize, new FileInfo(fixturePath).Length);
            Assert.Equal(
                originalSha256,
                await ComputeSha256Async(fixturePath, cancellationToken));
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var content = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(content, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }
}
