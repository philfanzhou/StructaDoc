using System.IO.Compression;
using StructaDoc.Application.Documents;

namespace StructaDoc.Infrastructure.Documents;

public sealed class OfficeDocumentTypeDetector : IDocumentTypeDetector
{
    private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray();
    private static readonly byte[] OleSignature =
        [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public async Task<DetectedDocumentType?> DetectAsync(
        Stream content,
        string originalFileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFileName);

        if (!content.CanSeek)
        {
            throw new ArgumentException("Document type detection requires a seekable stream.", nameof(content));
        }

        var header = new byte[OleSignature.Length];
        content.Position = 0;
        var bytesRead = await content.ReadAtLeastAsync(
            header,
            PdfSignature.Length,
            throwOnEndOfStream: false,
            cancellationToken);

        if (bytesRead >= PdfSignature.Length
            && header.AsSpan(0, PdfSignature.Length).SequenceEqual(PdfSignature))
        {
            return new DetectedDocumentType("application/pdf", ".pdf");
        }

        if (bytesRead >= OleSignature.Length
            && content.Length >= 512
            && header.AsSpan().SequenceEqual(OleSignature))
        {
            return DetectLegacyOfficeType(originalFileName);
        }

        content.Position = 0;
        return DetectOpenXmlType(content);
    }

    private static DetectedDocumentType? DetectLegacyOfficeType(string originalFileName)
    {
        return Path.GetExtension(originalFileName).ToLowerInvariant() switch
        {
            ".doc" => new DetectedDocumentType("application/msword", ".doc"),
            ".xls" => new DetectedDocumentType("application/vnd.ms-excel", ".xls"),
            ".ppt" => new DetectedDocumentType("application/vnd.ms-powerpoint", ".ppt"),
            _ => null,
        };
    }

    private static DetectedDocumentType? DetectOpenXmlType(Stream content)
    {
        try
        {
            using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);

            if (archive.Entries.Count > 10_000
                || archive.Entries.Any(entry =>
                    entry.FullName.EndsWith("/vbaProject.bin", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            var entryNames = archive.Entries
                .Select(entry => entry.FullName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!entryNames.Contains("[Content_Types].xml"))
            {
                return null;
            }

            if (entryNames.Contains("word/document.xml"))
            {
                return new DetectedDocumentType(
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    ".docx");
            }

            if (entryNames.Contains("xl/workbook.xml"))
            {
                return new DetectedDocumentType(
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    ".xlsx");
            }

            if (entryNames.Contains("ppt/presentation.xml"))
            {
                return new DetectedDocumentType(
                    "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                    ".pptx");
            }

            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
