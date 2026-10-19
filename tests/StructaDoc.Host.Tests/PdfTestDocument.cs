using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace StructaDoc.Host.Tests;

internal static class PdfTestDocument
{
    public static byte[] Create(int pageCount) => Create(pageCount, pageLabels: []);

    /// <summary>
    /// Creates a PDF whose catalog has a <c>/PageLabels</c> number tree with one range per entry.
    /// A null style writes no <c>/S</c>; any other value is written as given, so an invalid style
    /// produces a malformed tree.
    /// </summary>
    public static byte[] Create(
        int pageCount,
        params (int StartIndex, string? Style, string? Prefix, int? StartValue)[] pageLabels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);

        using var document = new PdfDocument();
        for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(612);
            page.Height = XUnit.FromPoint(792);
        }

        if (pageLabels.Length > 0)
        {
            var nums = new PdfArray(document);
            foreach (var (startIndex, style, prefix, startValue) in pageLabels)
            {
                var label = new PdfDictionary(document);
                if (style is not null)
                {
                    label.Elements["/S"] = new PdfName(style);
                }

                if (prefix is not null)
                {
                    label.Elements["/P"] = new PdfString(prefix);
                }

                if (startValue is not null)
                {
                    label.Elements["/St"] = new PdfInteger(startValue.Value);
                }

                nums.Elements.Add(new PdfInteger(startIndex));
                nums.Elements.Add(label);
            }

            var tree = new PdfDictionary(document);
            tree.Elements["/Nums"] = nums;
            document.Internals.AddObject(tree);
            document.Internals.Catalog.Elements["/PageLabels"] = tree.Reference;
        }

        using var output = new MemoryStream();
        document.Save(output, closeStream: false);
        return output.ToArray();
    }
}
