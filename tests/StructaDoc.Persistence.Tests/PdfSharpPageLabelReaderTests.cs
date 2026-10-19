using PdfSharp.Pdf;
using StructaDoc.Adapters.ProviderResults;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.Providers;

namespace StructaDoc.Persistence.Tests;

public sealed class PdfSharpPageLabelReaderTests
{
    private readonly PdfSharpPageLabelReader reader = new();

    [Fact]
    public async Task Reads_roman_arabic_and_prefixed_letter_ranges()
    {
        var pdf = PageLabelPdf.Create(8, document => PageLabelPdf.Nums(
            document,
            (0, PageLabelPdf.Label(document, "/r")),
            (3, PageLabelPdf.Label(document, "/D", start: 1)),
            (6, PageLabelPdf.Label(document, "/A", prefix: "App-"))));

        var result = await ReadAsync(pdf);

        Assert.Equal(PdfPageLabelReadStatus.Valid, result.Status);
        Assert.Equal(
            [
                new PdfPageLabelRange(0, PdfPageLabelStyle.LowerRoman, null, 1),
                new PdfPageLabelRange(3, PdfPageLabelStyle.Decimal, null, 1),
                new PdfPageLabelRange(6, PdfPageLabelStyle.UpperLetters, "App-", 1),
            ],
            result.Ranges);
    }

    [Fact]
    public async Task Follows_kids_in_order_and_accepts_the_depth_limit()
    {
        var pdf = PageLabelPdf.Create(4, document =>
        {
            var first = PageLabelPdf.Indirect(document, PageLabelPdf.Nums(document, (0, PageLabelPdf.Label(document, "/R"))));
            var second = PageLabelPdf.Indirect(document, PageLabelPdf.Nums(document, (2, PageLabelPdf.Label(document, "/a", start: 3))));
            var leaf = PageLabelPdf.Kids(document, first, second);
            // Root plus fourteen more levels plus the two leaves is exactly sixteen levels.
            for (var level = 0; level < 14; level++)
            {
                leaf = PageLabelPdf.Kids(document, PageLabelPdf.Indirect(document, leaf));
            }

            return leaf;
        });

        var result = await ReadAsync(pdf);

        Assert.Equal(PdfPageLabelReadStatus.Valid, result.Status);
        Assert.Equal(
            [
                new PdfPageLabelRange(0, PdfPageLabelStyle.UpperRoman, null, 1),
                new PdfPageLabelRange(2, PdfPageLabelStyle.LowerLetters, null, 3),
            ],
            result.Ranges);
    }

    [Fact]
    public async Task Reports_a_pdf_without_page_labels_as_absent()
    {
        var result = await ReadAsync(PageLabelPdf.Create(2, pageLabels: null));

        Assert.Equal(PdfPageLabelReadStatus.Absent, result.Status);
        Assert.Empty(result.Ranges);
    }

    [Fact]
    public async Task Accepts_a_range_count_just_under_the_limit()
    {
        var pdf = PageLabelPdf.Create(1, document => PageLabelPdf.Nums(
            document,
            Enumerable.Range(0, PdfSharpPageLabelReader.MaxVisitedItems - 1)
                .Select(index => (index, PageLabelPdf.Label(document, "/D")))
                .ToArray()));

        var result = await ReadAsync(pdf);

        Assert.Equal(PdfPageLabelReadStatus.Valid, result.Status);
        Assert.Equal(PdfSharpPageLabelReader.MaxVisitedItems - 1, result.Ranges.Count);
    }

    public static TheoryData<string> MalformedTrees => new(MalformedTreeBuilders.Keys);

    [Theory]
    [MemberData(nameof(MalformedTrees))]
    public async Task Reports_a_malformed_tree_without_throwing(string scenario)
    {
        var result = await ReadAsync(PageLabelPdf.Create(3, MalformedTreeBuilders[scenario]));

        Assert.Equal(PdfPageLabelReadStatus.Malformed, result.Status);
        Assert.Empty(result.Ranges);
    }

    [Fact]
    public async Task Reports_a_corrupt_pdf_as_unreadable()
    {
        var result = await ReadAsync("%PDF-1.7\nthis is not a real document"u8.ToArray());

        Assert.Equal(PdfPageLabelReadStatus.Unreadable, result.Status);
    }

    [Fact]
    public async Task Reports_an_encrypted_pdf_as_unreadable()
    {
        byte[] pdf;
        using (var document = new PdfDocument())
        {
            document.AddPage();
            document.SecuritySettings.UserPassword = "user-password";
            document.SecuritySettings.OwnerPassword = "owner-password";
            using var output = new MemoryStream();
            document.Save(output, closeStream: false);
            pdf = output.ToArray();
        }

        var result = await ReadAsync(pdf);

        Assert.Equal(PdfPageLabelReadStatus.Unreadable, result.Status);
    }

    [Fact]
    public async Task Storage_failures_propagate_instead_of_becoming_a_result()
    {
        await Assert.ThrowsAsync<IOException>(() => reader.ReadAsync(
            _ => throw new IOException("storage unavailable"),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IOException>(() => reader.ReadAsync(
            _ => Task.FromResult<Stream>(new FailingStream()),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var opened = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(
            _ =>
            {
                opened = true;
                return Task.FromResult<Stream>(new MemoryStream(PageLabelPdf.Create(1, pageLabels: null)));
            },
            cancelled.Token));
        Assert.False(opened);
    }

    [Fact]
    public async Task Enricher_prefers_pdf_labels_and_is_deterministic()
    {
        var pdf = PageLabelPdf.Create(4, document => PageLabelPdf.Nums(
            document,
            (0, PageLabelPdf.Label(document, "/r")),
            (2, PageLabelPdf.Label(document, "/D"))));
        // The page-number Blocks say something else; a valid tree wins for every page.
        var bundle = Bundle(
            pages: [1, 2, 3, 4],
            pageNumbers: [(1, "7"), (2, "8"), (3, "9"), (4, "10")]);
        var enricher = new PageLabelEnricher(reader);

        var first = await enricher.EnrichAsync(bundle, Source(pdf), TestContext.Current.CancellationToken);
        var second = await enricher.EnrichAsync(bundle, Source(pdf), TestContext.Current.CancellationToken);

        Assert.Equal(PdfPageLabelReadStatus.Valid, first.PdfStatus);
        Assert.Equal(["i", "ii", "1", "2"], first.Bundle.Pages.Select(page => page.Label));
        Assert.All(first.Bundle.Pages, page => Assert.Equal(PageLabelSources.PdfPageLabels, page.LabelSource));
        Assert.True(ParseBundleValidator.Validate(first.Bundle).IsValid);
        Assert.Equal(
            ParseBundleValidator.ComputeFingerprint(first.Bundle),
            ParseBundleValidator.ComputeFingerprint(second.Bundle));
        Assert.Equal(bundle.Blocks, first.Bundle.Blocks);
    }

    [Theory]
    [InlineData("no page labels")]
    [InlineData("malformed tree")]
    [InlineData("corrupt pdf")]
    [InlineData("tree without usable labels")]
    public async Task Enricher_falls_back_to_consistent_page_number_blocks(string scenario)
    {
        var pdf = scenario switch
        {
            "no page labels" => PageLabelPdf.Create(3, pageLabels: null),
            "malformed tree" => PageLabelPdf.Create(3, MalformedTreeBuilders["unknown style"]),
            "corrupt pdf" => "%PDF-1.7\nbroken"u8.ToArray(),
            "tree without usable labels" => PageLabelPdf.Create(3, document => PageLabelPdf.Nums(document)),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var bundle = Bundle(pages: [1, 2, 3], pageNumbers: [(1, "1"), (2, "2"), (3, "3")]);

        var result = await new PageLabelEnricher(reader).EnrichAsync(
            bundle,
            Source(pdf),
            TestContext.Current.CancellationToken);

        Assert.Equal(["1", "2", "3"], result.Bundle.Pages.Select(page => page.Label));
        Assert.All(result.Bundle.Pages, page => Assert.Equal(PageLabelSources.PageNumberBlocks, page.LabelSource));
        Assert.True(ParseBundleValidator.Validate(result.Bundle).IsValid);
    }

    [Fact]
    public async Task Enricher_does_not_read_a_document_that_is_not_a_pdf()
    {
        var opened = false;
        var source = new ProviderDocumentSource(
            "slides.pptx",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            10,
            _ =>
            {
                opened = true;
                throw new InvalidOperationException("A non-PDF source must not be opened.");
            });

        var withoutBlocks = await new PageLabelEnricher(reader).EnrichAsync(
            Bundle(pages: [1, 2], pageNumbers: []),
            source,
            TestContext.Current.CancellationToken);
        var withBlocks = await new PageLabelEnricher(reader).EnrichAsync(
            Bundle(pages: [1, 2], pageNumbers: [(1, "i"), (2, "ii")]),
            source,
            TestContext.Current.CancellationToken);

        Assert.False(opened);
        Assert.Null(withoutBlocks.PdfStatus);
        Assert.All(withoutBlocks.Bundle.Pages, page => Assert.Null(page.Label));
        Assert.Equal(["i", "ii"], withBlocks.Bundle.Pages.Select(page => page.Label));
    }

    private static readonly Dictionary<string, Func<PdfDocument, PdfItem?>> MalformedTreeBuilders = new()
    {
        ["page labels not a dictionary"] = _ => new PdfInteger(4),
        ["nums is a string"] = document =>
        {
            var root = new PdfDictionary(document);
            root.Elements["/Nums"] = new PdfString("garbage");
            return root;
        },
        ["odd nums count"] = document =>
        {
            var root = PageLabelPdf.Nums(document, (0, PageLabelPdf.Label(document, "/D")));
            ((PdfArray)root.Elements["/Nums"]!).Elements.Add(new PdfInteger(4));
            return root;
        },
        ["key is not an integer"] = document =>
        {
            var root = new PdfDictionary(document);
            var nums = new PdfArray(document);
            nums.Elements.Add(new PdfReal(1.5));
            nums.Elements.Add(PageLabelPdf.Label(document, "/D"));
            root.Elements["/Nums"] = nums;
            return root;
        },
        ["negative key"] = document => PageLabelPdf.Nums(document, (-1, PageLabelPdf.Label(document, "/D"))),
        ["decreasing keys"] = document => PageLabelPdf.Nums(
            document,
            (2, PageLabelPdf.Label(document, "/D")),
            (1, PageLabelPdf.Label(document, "/r"))),
        ["duplicate keys"] = document => PageLabelPdf.Nums(
            document,
            (1, PageLabelPdf.Label(document, "/D")),
            (1, PageLabelPdf.Label(document, "/r"))),
        ["decreasing keys across kids"] = document => PageLabelPdf.Kids(
            document,
            PageLabelPdf.Indirect(document, PageLabelPdf.Nums(document, (5, PageLabelPdf.Label(document, "/D")))),
            PageLabelPdf.Indirect(document, PageLabelPdf.Nums(document, (3, PageLabelPdf.Label(document, "/D"))))),
        ["value is not a dictionary"] = document =>
        {
            var root = new PdfDictionary(document);
            var nums = new PdfArray(document);
            nums.Elements.Add(new PdfInteger(0));
            nums.Elements.Add(new PdfInteger(7));
            root.Elements["/Nums"] = nums;
            return root;
        },
        ["unknown style"] = document => PageLabelPdf.Nums(document, (0, PageLabelPdf.Label(document, "/Q"))),
        ["style is not a name"] = document =>
        {
            var label = new PdfDictionary(document);
            label.Elements["/S"] = new PdfString("D");
            return PageLabelPdf.Nums(document, (0, label));
        },
        ["start below one"] = document => PageLabelPdf.Nums(document, (0, PageLabelPdf.Label(document, "/D", start: 0))),
        ["start is not an integer"] = document =>
        {
            var label = PageLabelPdf.Label(document, "/D");
            label.Elements["/St"] = new PdfString("1");
            return PageLabelPdf.Nums(document, (0, label));
        },
        ["prefix is not a string"] = document =>
        {
            var label = PageLabelPdf.Label(document, "/D");
            label.Elements["/P"] = new PdfName("/Prefix");
            return PageLabelPdf.Nums(document, (0, label));
        },
        ["node with neither nums nor kids"] = document => new PdfDictionary(document),
        ["node with both nums and kids"] = document =>
        {
            var root = PageLabelPdf.Nums(document, (0, PageLabelPdf.Label(document, "/D")));
            root.Elements["/Kids"] = new PdfArray(document);
            return root;
        },
        ["kid is not a dictionary"] = document =>
        {
            var root = new PdfDictionary(document);
            var kids = new PdfArray(document);
            kids.Elements.Add(new PdfInteger(1));
            root.Elements["/Kids"] = kids;
            return root;
        },
        ["kids cycle"] = document =>
        {
            var first = PageLabelPdf.Indirect(document, new PdfDictionary(document));
            var second = PageLabelPdf.Indirect(document, new PdfDictionary(document));
            var firstKids = new PdfArray(document);
            firstKids.Elements.Add(second.Reference!);
            first.Elements["/Kids"] = firstKids;
            var secondKids = new PdfArray(document);
            secondKids.Elements.Add(first.Reference!);
            second.Elements["/Kids"] = secondKids;
            return first.Reference;
        },
        ["depth limit exceeded"] = document =>
        {
            var node = PageLabelPdf.Nums(document, (0, PageLabelPdf.Label(document, "/D")));
            for (var level = 1; level < PdfSharpPageLabelReader.MaxDepth + 1; level++)
            {
                node = PageLabelPdf.Kids(document, PageLabelPdf.Indirect(document, node));
            }

            return node;
        },
        ["size limit exceeded"] = document => PageLabelPdf.Nums(
            document,
            Enumerable.Range(0, PdfSharpPageLabelReader.MaxVisitedItems)
                .Select(index => (index, PageLabelPdf.Label(document, "/D")))
                .ToArray()),
    };

    private Task<PdfPageLabelReadResult> ReadAsync(byte[] pdf) =>
        reader.ReadAsync(
            _ => Task.FromResult<Stream>(new MemoryStream(pdf, writable: false)),
            TestContext.Current.CancellationToken);

    private static ProviderDocumentSource Source(byte[] pdf) =>
        new("submitted.pdf", "application/pdf", pdf.Length, _ => Task.FromResult<Stream>(new MemoryStream(pdf, writable: false)));

    private static ParseBundle Bundle(int[] pages, (int Page, string Text)[] pageNumbers) =>
        new(
            ParseBundleValidator.CurrentSchemaVersion,
            Guid.NewGuid(),
            pages.Select(number => new ParsePage(number)).ToArray(),
            pageNumbers
                .Select((item, index) => new ParseBlock(
                    Guid.NewGuid(),
                    index,
                    item.Page,
                    "page-number",
                    Content: item.Text,
                    ContentFormat: "plain"))
                .ToArray(),
            [],
            [],
            "{\"providerType\":\"test\"}");

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("storage read failed");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("storage read failed");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Builds small PDFs whose catalog carries a hand-written <c>/PageLabels</c> tree.</summary>
internal static class PageLabelPdf
{
    public static byte[] Create(int pageCount, Func<PdfDocument, PdfItem?>? pageLabels)
    {
        using var document = new PdfDocument();
        for (var index = 0; index < pageCount; index++)
        {
            document.AddPage();
        }

        var tree = pageLabels?.Invoke(document);
        if (tree is not null)
        {
            document.Internals.Catalog.Elements["/PageLabels"] = tree;
        }

        using var output = new MemoryStream();
        document.Save(output, closeStream: false);
        return output.ToArray();
    }

    public static PdfDictionary Label(PdfDocument document, string? style, string? prefix = null, int? start = null)
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

        if (start is not null)
        {
            label.Elements["/St"] = new PdfInteger(start.Value);
        }

        return label;
    }

    public static PdfDictionary Nums(PdfDocument document, params (int Start, PdfDictionary Label)[] ranges)
    {
        var node = new PdfDictionary(document);
        var nums = new PdfArray(document);
        foreach (var (start, label) in ranges)
        {
            nums.Elements.Add(new PdfInteger(start));
            nums.Elements.Add(label);
        }

        node.Elements["/Nums"] = nums;
        return node;
    }

    public static PdfDictionary Kids(PdfDocument document, params PdfDictionary[] children)
    {
        var node = new PdfDictionary(document);
        var kids = new PdfArray(document);
        foreach (var child in children)
        {
            kids.Elements.Add(child.Reference!);
        }

        node.Elements["/Kids"] = kids;
        return node;
    }

    public static PdfDictionary Indirect(PdfDocument document, PdfDictionary dictionary)
    {
        document.Internals.AddObject(dictionary);
        return dictionary;
    }
}
