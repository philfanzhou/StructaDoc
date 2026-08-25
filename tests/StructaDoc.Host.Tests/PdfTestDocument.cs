using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace StructaDoc.Host.Tests;

internal static class PdfTestDocument
{
    public static byte[] Create(int pageCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);

        using var document = new PdfDocument();
        for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(612);
            page.Height = XUnit.FromPoint(792);
        }

        using var output = new MemoryStream();
        document.Save(output, closeStream: false);
        return output.ToArray();
    }
}
