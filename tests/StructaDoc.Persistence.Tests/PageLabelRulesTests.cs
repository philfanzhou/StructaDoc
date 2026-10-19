using StructaDoc.Application.Canonical;

namespace StructaDoc.Persistence.Tests;

public sealed class PageLabelRulesTests
{
    private static readonly PdfPageLabelRange[] BookRanges =
    [
        new(0, PdfPageLabelStyle.LowerRoman, null, 1),
        new(3, PdfPageLabelStyle.Decimal, null, 1),
        new(6, PdfPageLabelStyle.UpperLetters, "App-", 1),
    ];

    [Fact]
    public void Pdf_ranges_label_every_page_by_its_physical_index()
    {
        var pages = Enumerable.Range(1, 8).Select(number => new ParsePage(number)).ToArray();

        var labeled = PageLabelRules.ApplyPdfPageLabels(pages, BookRanges);

        Assert.Equal(
            ["i", "ii", "iii", "1", "2", "3", "App-A", "App-B"],
            labeled.Select(page => page.Label));
        Assert.All(labeled, page => Assert.Equal(PageLabelSources.PdfPageLabels, page.LabelSource));
        Assert.Equal(pages.Select(page => page.Number), labeled.Select(page => page.Number));
    }

    [Theory]
    [InlineData(PdfPageLabelStyle.Decimal, null, 7L, "7")]
    [InlineData(PdfPageLabelStyle.UpperRoman, null, 1994L, "MCMXCIV")]
    [InlineData(PdfPageLabelStyle.LowerRoman, null, 3999L, "mmmcmxcix")]
    [InlineData(PdfPageLabelStyle.UpperLetters, null, 26L, "Z")]
    [InlineData(PdfPageLabelStyle.UpperLetters, null, 27L, "AA")]
    [InlineData(PdfPageLabelStyle.LowerLetters, null, 53L, "aaa")]
    [InlineData(PdfPageLabelStyle.None, "Cover", 5L, "Cover")]
    [InlineData(PdfPageLabelStyle.Decimal, "A-", 3L, "A-3")]
    public void Pdf_styles_format_like_iso_32000(
        PdfPageLabelStyle style,
        string? prefix,
        long startValue,
        string expected)
    {
        Assert.Equal(
            expected,
            PageLabelRules.FormatPdfPageLabel([new PdfPageLabelRange(0, style, prefix, startValue)], 0));
    }

    [Fact]
    public void Pdf_labels_that_cannot_be_displayed_are_null()
    {
        // Pages before the first range have no label.
        Assert.Null(PageLabelRules.FormatPdfPageLabel([new PdfPageLabelRange(2, PdfPageLabelStyle.Decimal, null, 1)], 1));
        // Roman numerals stop at 3999.
        Assert.Null(PageLabelRules.FormatPdfPageLabel([new PdfPageLabelRange(0, PdfPageLabelStyle.UpperRoman, null, 4000)], 0));
        // A range with no style and no prefix formats to nothing.
        Assert.Null(PageLabelRules.FormatPdfPageLabel([new PdfPageLabelRange(0, PdfPageLabelStyle.None, null, 1)], 0));
        Assert.Null(PageLabelRules.FormatPdfPageLabel([new PdfPageLabelRange(0, PdfPageLabelStyle.None, "   ", 1)], 0));
        Assert.Null(PageLabelRules.FormatPdfPageLabel([new PdfPageLabelRange(0, PdfPageLabelStyle.Decimal, new string('x', 256), 1)], 0));
        Assert.Null(PageLabelRules.FormatPdfPageLabel([new PdfPageLabelRange(0, PdfPageLabelStyle.Decimal, "a\u0007", 1)], 0));
        Assert.Null(PageLabelRules.FormatPdfPageLabel([new PdfPageLabelRange(0, PdfPageLabelStyle.Decimal, "\ud800", 1)], 0));
        // Letters repeat once per 26, so a huge start value would be a huge label; it is refused
        // before it is built.
        Assert.Null(PageLabelRules.FormatPdfPageLabel([new PdfPageLabelRange(0, PdfPageLabelStyle.UpperLetters, null, int.MaxValue)], 0));
        Assert.Null(PageLabelRules.FormatPdfPageLabel([new PdfPageLabelRange(0, PdfPageLabelStyle.Decimal, null, long.MaxValue)], 1));
        Assert.Equal(
            new string('x', 255) + "1",
            PageLabelRules.FormatPdfPageLabel([new PdfPageLabelRange(0, PdfPageLabelStyle.Decimal, new string('x', 255), 1)], 0));
    }

    [Fact]
    public void Consistent_page_number_blocks_label_their_pages_without_interpolation()
    {
        var pages = Enumerable.Range(1, 7).Select(number => new ParsePage(number)).ToArray();
        var blocks = Blocks(
            (1, "- iii -"),
            (2, "iv"),
            (2, "IV"),
            (4, "[ 1 ]"),
            (5, "text that is not a number"),
            (6, "3."),
            (7, "—4—"));

        var labeled = PageLabelRules.ApplyPageNumberBlocks(pages, blocks);

        Assert.Equal(
            [
                ("iii", PageLabelSources.PageNumberBlocks),
                ("iv", PageLabelSources.PageNumberBlocks),
                (null, null),
                ("1", PageLabelSources.PageNumberBlocks),
                (null, null),
                ("3", PageLabelSources.PageNumberBlocks),
                ("4", PageLabelSources.PageNumberBlocks),
            ],
            labeled.Select(page => (page.Label, page.LabelSource)));
    }

    [Fact]
    public void A_page_whose_page_number_blocks_disagree_is_skipped()
    {
        var pages = Enumerable.Range(1, 3).Select(number => new ParsePage(number)).ToArray();
        var labeled = PageLabelRules.ApplyPageNumberBlocks(
            pages,
            Blocks((1, "1"), (2, "2"), (2, "7"), (3, "3")));

        Assert.Equal(["1", null, "3"], labeled.Select(page => page.Label));
    }

    [Theory]
    [InlineData("single value", new[] { "5" })]
    [InlineData("offset changes", new[] { "1", "2", "9" })]
    [InlineData("roman after arabic", new[] { "1", "2", "iii" })]
    [InlineData("roman offset changes", new[] { "i", "v" })]
    [InlineData("non canonical roman", new[] { "iiii", "v" })]
    [InlineData("six digits", new[] { "100000", "100001" })]
    public void Inconsistent_or_insufficient_page_number_blocks_give_no_labels(
        string scenario,
        string[] printed)
    {
        Assert.NotEmpty(scenario);
        var pages = Enumerable.Range(1, printed.Length).Select(number => new ParsePage(number)).ToArray();
        var labeled = PageLabelRules.ApplyPageNumberBlocks(
            pages,
            Blocks(printed.Select((text, index) => (index + 1, text)).ToArray()));

        Assert.All(labeled, page =>
        {
            Assert.Null(page.Label);
            Assert.Null(page.LabelSource);
        });
    }

    [Theory]
    [InlineData("12", "12", 12, false)]
    [InlineData("  (007)  ", "007", 7, false)]
    [InlineData("xiv", "xiv", 14, true)]
    [InlineData("MMMCMXCIX", "MMMCMXCIX", 3999, true)]
    public void Printed_page_numbers_accept_arabic_and_canonical_roman(
        string content,
        string text,
        int value,
        bool isRoman)
    {
        Assert.Equal(new PrintedPageNumber(text, value, isRoman), PageLabelRules.ParsePrintedPageNumber(content));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("--")]
    [InlineData("123456")]
    [InlineData("\uFF11\uFF12")]
    [InlineData("IC")]
    [InlineData("MMMM")]
    [InlineData("Page 4")]
    [InlineData("4a")]
    public void Printed_page_numbers_reject_everything_else(string? content)
    {
        Assert.Null(PageLabelRules.ParsePrintedPageNumber(content));
    }

    [Fact]
    public void Blocks_that_are_not_page_numbers_or_have_no_page_are_ignored()
    {
        var pages = new[] { new ParsePage(1), new ParsePage(2) };
        var blocks = new[]
        {
            new ParseBlock(Guid.NewGuid(), 0, 1, "text", Content: "1"),
            new ParseBlock(Guid.NewGuid(), 1, null, "page-number", Content: "1"),
            new ParseBlock(Guid.NewGuid(), 2, 2, "footer", Content: "2"),
        };

        Assert.All(PageLabelRules.ApplyPageNumberBlocks(pages, blocks), page => Assert.Null(page.Label));
    }

    private static ParseBlock[] Blocks(params (int Page, string Content)[] items) =>
        items
            .Select((item, index) => new ParseBlock(
                Guid.NewGuid(),
                index,
                item.Page,
                "page-number",
                Content: item.Content,
                ContentFormat: "plain"))
            .ToArray();
}

public sealed class ParsePageLabelValidationTests
{
    [Fact]
    public void The_current_schema_version_is_1_1()
    {
        Assert.Equal("1.1", ParseBundleValidator.CurrentSchemaVersion);
    }

    [Fact]
    public void Labels_with_a_known_source_are_valid_and_part_of_the_fingerprint()
    {
        var unlabeled = Bundle(new ParsePage(1));
        var labeled = Bundle(new ParsePage(1, Label: "iv", LabelSource: PageLabelSources.PdfPageLabels));
        var otherSource = Bundle(new ParsePage(1, Label: "iv", LabelSource: PageLabelSources.PageNumberBlocks));

        Assert.True(ParseBundleValidator.Validate(unlabeled).IsValid);
        Assert.True(ParseBundleValidator.Validate(labeled).IsValid);
        Assert.True(ParseBundleValidator.Validate(Bundle(new ParsePage(1, Label: new string('x', 256), LabelSource: PageLabelSources.PageNumberBlocks))).IsValid);
        Assert.NotEqual(ParseBundleValidator.ComputeFingerprint(unlabeled), ParseBundleValidator.ComputeFingerprint(labeled));
        Assert.NotEqual(ParseBundleValidator.ComputeFingerprint(labeled), ParseBundleValidator.ComputeFingerprint(otherSource));
    }

    [Theory]
    [InlineData("iv", null)]
    [InlineData(null, PageLabelSources.PdfPageLabels)]
    [InlineData("iv", "provider-labels")]
    [InlineData("", PageLabelSources.PdfPageLabels)]
    [InlineData("   ", PageLabelSources.PdfPageLabels)]
    [InlineData("i\nv", PageLabelSources.PdfPageLabels)]
    [InlineData("\u0000", PageLabelSources.PdfPageLabels)]
    public void Invalid_label_pairs_are_rejected(string? label, string? labelSource)
    {
        var result = ParseBundleValidator.Validate(Bundle(new ParsePage(1, Label: label, LabelSource: labelSource)));

        Assert.False(result.IsValid);
        Assert.Equal("invalid-page-label", result.ErrorCode);
    }

    [Fact]
    public void A_label_over_256_characters_or_with_an_unpaired_surrogate_is_rejected()
    {
        // Built at run time: an attribute argument cannot carry an unpaired surrogate.
        foreach (var label in new[] { new string('x', 257), "iv" + (char)0xDC00, (char)0xD800 + "iv" })
        {
            var result = ParseBundleValidator.Validate(
                Bundle(new ParsePage(1, Label: label, LabelSource: PageLabelSources.PdfPageLabels)));

            Assert.Equal("invalid-page-label", result.ErrorCode);
        }

        Assert.True(ParseBundleValidator.Validate(
            Bundle(new ParsePage(1, Label: "\ud83d\udcd6 1", LabelSource: PageLabelSources.PdfPageLabels))).IsValid);
    }

    [Fact]
    public void New_bundles_must_use_the_current_schema_version()
    {
        Assert.Equal(
            "unsupported-schema-version",
            ParseBundleValidator.Validate(Bundle(new ParsePage(1)) with { SchemaVersion = "1.0" }).ErrorCode);
    }

    private static ParseBundle Bundle(ParsePage page) =>
        new(
            ParseBundleValidator.CurrentSchemaVersion,
            Guid.Parse("8b0d3c1e-55a4-4d8e-9a43-6f1f2b9c0d11"),
            [page],
            [],
            [],
            [],
            "{\"providerType\":\"test\"}");
}
