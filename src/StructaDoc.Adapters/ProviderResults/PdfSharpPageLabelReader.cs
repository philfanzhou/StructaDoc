using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using StructaDoc.Application.Canonical;

namespace StructaDoc.Adapters.ProviderResults;

/// <summary>
/// Reads the ISO 32000 <c>/PageLabels</c> number tree with PDFsharp's low-level dictionary API,
/// because PDFsharp has no page-label API of its own. Nothing else in the PDF is interpreted, and
/// the traversal is bounded by depth, by the number of visited nodes and ranges, and by cycle
/// detection.
/// </summary>
public sealed class PdfSharpPageLabelReader : IPdfPageLabelReader
{
    /// <summary>The deepest tree level read, counting the root as level 1.</summary>
    public const int MaxDepth = 16;

    /// <summary>The most tree nodes and ranges read together.</summary>
    public const int MaxVisitedItems = 10_000;

    public async Task<PdfPageLabelReadResult> ReadAsync(
        Func<CancellationToken, Task<Stream>> openReadAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openReadAsync);
        cancellationToken.ThrowIfCancellationRequested();

        // Storage failures while opening or copying the document propagate, so the Parse Run keeps
        // its transient failure and a retry can never commit a result read from a partial copy.
        await using var input = await SeekableCopyAsync(
            await openReadAsync(cancellationToken),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        PdfDocument document;
        try
        {
            document = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PdfPageLabelReadResult.Unreadable;
        }

        using (document)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return ReadTree(document, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A reference PDFsharp cannot resolve is a broken tree, not a broken run.
                return PdfPageLabelReadResult.Malformed;
            }
        }
    }

    private static PdfPageLabelReadResult ReadTree(PdfDocument document, CancellationToken cancellationToken)
    {
        var rootItem = document.Internals.Catalog.Elements["/PageLabels"];
        if (rootItem is null)
        {
            return PdfPageLabelReadResult.Absent;
        }

        if (Resolve(rootItem) is not PdfDictionary root)
        {
            return PdfPageLabelReadResult.Malformed;
        }

        var traversal = new Traversal(cancellationToken);
        return traversal.Walk(root, depth: 1)
            ? new PdfPageLabelReadResult(PdfPageLabelReadStatus.Valid, traversal.Ranges)
            : PdfPageLabelReadResult.Malformed;
    }

    private static PdfItem? Resolve(PdfItem? item) =>
        item is PdfReference reference ? reference.Value : item;

    private static long? AsInteger(PdfItem? item) => Resolve(item) switch
    {
        PdfInteger value => value.Value,
        PdfLongInteger value => value.Value,
        PdfIntegerObject value => value.Value,
        PdfLongIntegerObject value => value.Value,
        _ => null,
    };

    private static async Task<FileStream> SeekableCopyAsync(Stream source, CancellationToken cancellationToken)
    {
        await using (source)
        {
            var path = Path.Combine(Path.GetTempPath(), $"structadoc-labels-{Guid.NewGuid():N}.pdf");
            var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.RandomAccess | FileOptions.DeleteOnClose);
            try
            {
                await source.CopyToAsync(stream, cancellationToken);
                stream.Position = 0;
                return stream;
            }
            catch
            {
                await stream.DisposeAsync();
                throw;
            }
        }
    }

    private sealed class Traversal(CancellationToken cancellationToken)
    {
        private readonly HashSet<PdfDictionary> visitedNodes = new(ReferenceEqualityComparer.Instance);
        private int visitedItems;
        private long lastStart = -1;

        public List<PdfPageLabelRange> Ranges { get; } = [];

        public bool Walk(PdfDictionary node, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > MaxDepth || !visitedNodes.Add(node) || !CountItem())
            {
                return false;
            }

            var hasNums = node.Elements.ContainsKey("/Nums");
            var hasKids = node.Elements.ContainsKey("/Kids");
            if (hasNums == hasKids)
            {
                return false;
            }

            if (hasKids)
            {
                if (Resolve(node.Elements["/Kids"]) is not PdfArray kids)
                {
                    return false;
                }

                foreach (var kid in kids.Elements)
                {
                    if (Resolve(kid) is not PdfDictionary child || !Walk(child, depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            }

            if (Resolve(node.Elements["/Nums"]) is not PdfArray nums || nums.Elements.Count % 2 != 0)
            {
                return false;
            }

            for (var index = 0; index < nums.Elements.Count; index += 2)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!CountItem())
                {
                    return false;
                }

                var start = AsInteger(nums.Elements[index]);
                if (start is null || start.Value < 0 || start.Value <= lastStart)
                {
                    return false;
                }

                if (Resolve(nums.Elements[index + 1]) is not PdfDictionary labelDictionary)
                {
                    return false;
                }

                var range = ReadRange(start.Value, labelDictionary);
                if (range is null)
                {
                    return false;
                }

                lastStart = start.Value;
                Ranges.Add(range);
            }

            return true;
        }

        private bool CountItem() => ++visitedItems <= MaxVisitedItems;

        private static PdfPageLabelRange? ReadRange(long start, PdfDictionary labelDictionary)
        {
            var style = PdfPageLabelStyle.None;
            if (labelDictionary.Elements.ContainsKey("/S"))
            {
                var name = Resolve(labelDictionary.Elements["/S"]) switch
                {
                    PdfName value => value.Value,
                    PdfNameObject value => value.Value,
                    _ => null,
                };
                switch (name)
                {
                    case "/D": style = PdfPageLabelStyle.Decimal; break;
                    case "/R": style = PdfPageLabelStyle.UpperRoman; break;
                    case "/r": style = PdfPageLabelStyle.LowerRoman; break;
                    case "/A": style = PdfPageLabelStyle.UpperLetters; break;
                    case "/a": style = PdfPageLabelStyle.LowerLetters; break;
                    default: return null;
                }
            }

            long startValue = 1;
            if (labelDictionary.Elements.ContainsKey("/St"))
            {
                var value = AsInteger(labelDictionary.Elements["/St"]);
                if (value is null or < 1)
                {
                    return null;
                }

                startValue = value.Value;
            }

            string? prefix = null;
            if (labelDictionary.Elements.ContainsKey("/P"))
            {
                prefix = Resolve(labelDictionary.Elements["/P"]) switch
                {
                    PdfString value => value.Value,
                    PdfStringObject value => value.Value,
                    _ => null,
                };
                if (prefix is null)
                {
                    return null;
                }
            }

            return new PdfPageLabelRange(start, style, prefix, startValue);
        }
    }
}
