using System.IO.Compression;
using StructaDoc.Adapters.Documents;

namespace StructaDoc.Persistence.Tests;

public sealed class OfficeDocumentTypeDetectorTests
{
    [Theory]
    [InlineData("word/document.xml", ".docx")]
    [InlineData("xl/workbook.xml", ".xlsx")]
    [InlineData("ppt/presentation.xml", ".pptx")]
    public async Task OpenXml_package_is_detected_from_internal_structure(
        string applicationEntry,
        string expectedExtension)
    {
        await using var content = new MemoryStream();

        using (var archive = new ZipArchive(content, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("[Content_Types].xml");
            archive.CreateEntry(applicationEntry);
        }

        content.Position = 0;
        var detector = new OfficeDocumentTypeDetector();
        var detected = await detector.DetectAsync(content, "misleading.bin");

        Assert.NotNull(detected);
        Assert.Equal(expectedExtension, detected.Extension);
    }

    [Fact]
    public async Task Arbitrary_zip_is_not_accepted_as_an_office_document()
    {
        await using var content = new MemoryStream();

        using (var archive = new ZipArchive(content, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("data.txt");
        }

        content.Position = 0;
        var detector = new OfficeDocumentTypeDetector();

        Assert.Null(await detector.DetectAsync(content, "archive.docx"));
    }

    [Fact]
    public async Task Macro_enabled_package_is_not_misclassified_as_macro_free_openxml()
    {
        await using var content = new MemoryStream();

        using (var archive = new ZipArchive(content, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("[Content_Types].xml");
            archive.CreateEntry("word/document.xml");
            archive.CreateEntry("word/vbaProject.bin");
        }

        content.Position = 0;
        var detector = new OfficeDocumentTypeDetector();

        Assert.Null(await detector.DetectAsync(content, "macros.docm"));
    }

    [Fact]
    public async Task Truncated_ole_signature_is_not_accepted_as_legacy_office()
    {
        byte[] signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        await using var content = new MemoryStream(signature);
        var detector = new OfficeDocumentTypeDetector();

        Assert.Null(await detector.DetectAsync(content, "truncated.doc"));
    }
}
