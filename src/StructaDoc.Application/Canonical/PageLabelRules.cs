using System.Globalization;
using System.Text;

namespace StructaDoc.Application.Canonical;

/// <summary>
/// The deterministic rules that turn a PDF page-label tree, or the run's <c>page-number</c>
/// Blocks, into Page labels. The same inputs always give the same labels, so a recovered run
/// produces the same bundle fingerprint.
/// </summary>
public static class PageLabelRules
{
    public const int MaxRomanValue = 3999;
    public const int MaxArabicDigits = 5;

    private const string PageNumberBlockType = "page-number";

    private static readonly (int Value, string Numeral)[] RomanNumerals =
    [
        (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
        (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
        (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
    ];

    /// <summary>
    /// Gives every Page the label of the range containing its zero-based physical index. Pages
    /// before the first range, and pages whose formatted label is not a valid label, get none.
    /// </summary>
    public static IReadOnlyList<ParsePage> ApplyPdfPageLabels(
        IReadOnlyList<ParsePage> pages,
        IReadOnlyList<PdfPageLabelRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(ranges);

        return pages
            .Select(page =>
            {
                var label = FormatPdfPageLabel(ranges, page.Number - 1L);
                return label is null
                    ? WithoutLabel(page)
                    : page with { Label = label, LabelSource = PageLabelSources.PdfPageLabels };
            })
            .ToArray();
    }

    /// <summary>
    /// The label a PDF page-label tree gives the page at <paramref name="pageIndex"/>, or
    /// <see langword="null"/> when no range covers it or the formatted label is not valid.
    /// <paramref name="ranges"/> must be in strictly increasing start order.
    /// </summary>
    public static string? FormatPdfPageLabel(IReadOnlyList<PdfPageLabelRange> ranges, long pageIndex)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        PdfPageLabelRange? range = null;
        var low = 0;
        var high = ranges.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (ranges[middle].StartPageIndex <= pageIndex)
            {
                range = ranges[middle];
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (range is null)
        {
            return null;
        }

        long value;
        try
        {
            value = checked(range.StartValue + (pageIndex - range.StartPageIndex));
        }
        catch (OverflowException)
        {
            return null;
        }

        string numeral;
        switch (range.Style)
        {
            case PdfPageLabelStyle.None:
                numeral = string.Empty;
                break;

            case PdfPageLabelStyle.Decimal:
                numeral = value.ToString(CultureInfo.InvariantCulture);
                break;

            case PdfPageLabelStyle.UpperRoman:
            case PdfPageLabelStyle.LowerRoman:
                var roman = ToRoman(value);
                if (roman is null)
                {
                    return null;
                }

                numeral = range.Style == PdfPageLabelStyle.LowerRoman
                    ? roman.ToLowerInvariant()
                    : roman;
                break;

            case PdfPageLabelStyle.UpperLetters:
            case PdfPageLabelStyle.LowerLetters:
                var letters = ToLetters(value, range.Style == PdfPageLabelStyle.LowerLetters);
                if (letters is null)
                {
                    return null;
                }

                numeral = letters;
                break;

            default:
                return null;
        }

        var label = (range.Prefix ?? string.Empty) + numeral;
        return PageLabelSources.IsValidLabel(label) ? label : null;
    }

    /// <summary>
    /// Labels Pages from their <c>page-number</c> Blocks when the accepted values form one
    /// consistent sequence; otherwise every label stays null. There is no interpolation: a Page
    /// without an accepted value keeps a null label.
    /// </summary>
    public static IReadOnlyList<ParsePage> ApplyPageNumberBlocks(
        IReadOnlyList<ParsePage> pages,
        IReadOnlyList<ParseBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(blocks);

        var blocksByPage = blocks
            .Where(block => block.PageNumber.HasValue
                && string.Equals(block.Type, PageNumberBlockType, StringComparison.Ordinal))
            .OrderBy(block => block.Sequence)
            .GroupBy(block => block.PageNumber!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());

        var accepted = new Dictionary<int, PrintedPageNumber>();
        foreach (var page in pages)
        {
            if (!blocksByPage.TryGetValue(page.Number, out var pageBlocks))
            {
                continue;
            }

            PrintedPageNumber? chosen = null;
            var disagrees = false;
            foreach (var block in pageBlocks)
            {
                var printed = ParsePrintedPageNumber(block.Content);
                if (printed is null)
                {
                    continue;
                }

                if (chosen is null)
                {
                    chosen = printed;
                }
                else if (chosen.IsRoman != printed.IsRoman || chosen.Value != printed.Value)
                {
                    disagrees = true;
                    break;
                }
            }

            if (chosen is not null && !disagrees)
            {
                accepted[page.Number] = chosen;
            }
        }

        if (!IsConsistent(accepted))
        {
            return pages.Select(WithoutLabel).ToArray();
        }

        return pages
            .Select(page => accepted.TryGetValue(page.Number, out var printed)
                ? page with { Label = printed.Text, LabelSource = PageLabelSources.PageNumberBlocks }
                : WithoutLabel(page))
            .ToArray();
    }

    /// <summary>
    /// The printed page number in a <c>page-number</c> Block, or <see langword="null"/> when the
    /// text, once whitespace and decoration are trimmed, is neither 1 to 5 ASCII digits nor a
    /// canonical roman numeral up to <see cref="MaxRomanValue"/>.
    /// </summary>
    public static PrintedPageNumber? ParsePrintedPageNumber(string? content)
    {
        if (content is null)
        {
            return null;
        }

        var start = 0;
        var end = content.Length;
        while (start < end && IsTrimmed(content[start]))
        {
            start++;
        }

        while (end > start && IsTrimmed(content[end - 1]))
        {
            end--;
        }

        var text = content[start..end];
        if (text.Length is >= 1 and <= MaxArabicDigits && text.All(char.IsAsciiDigit))
        {
            return new PrintedPageNumber(
                text,
                int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture),
                IsRoman: false);
        }

        var romanValue = ParseRoman(text);
        return romanValue is null
            ? null
            : new PrintedPageNumber(text, romanValue.Value, IsRoman: true);
    }

    public static string? ToRoman(long value)
    {
        if (value is < 1 or > MaxRomanValue)
        {
            return null;
        }

        var builder = new StringBuilder();
        var remaining = (int)value;
        foreach (var (numeralValue, numeral) in RomanNumerals)
        {
            while (remaining >= numeralValue)
            {
                builder.Append(numeral);
                remaining -= numeralValue;
            }
        }

        return builder.ToString();
    }

    private static string? ToLetters(long value, bool lowercase)
    {
        if (value < 1)
        {
            return null;
        }

        var repeat = ((value - 1) / 26) + 1;
        if (repeat > PageLabelSources.MaxLabelCharacters)
        {
            return null;
        }

        var letter = (char)((lowercase ? 'a' : 'A') + ((value - 1) % 26));
        return new string(letter, (int)repeat);
    }

    private static int? ParseRoman(string text)
    {
        if (text.Length is 0 or > 15)
        {
            return null;
        }

        var upper = text.ToUpperInvariant();
        var total = 0;
        var index = 0;
        foreach (var (numeralValue, numeral) in RomanNumerals)
        {
            while (index + numeral.Length <= upper.Length
                && string.CompareOrdinal(upper, index, numeral, 0, numeral.Length) == 0)
            {
                total += numeralValue;
                index += numeral.Length;
            }
        }

        // Only the canonical spelling round-trips, which rejects forms such as IIII, VX, or IC.
        return index == upper.Length && string.Equals(ToRoman(total), upper, StringComparison.Ordinal)
            ? total
            : null;
    }

    private static bool IsConsistent(IReadOnlyDictionary<int, PrintedPageNumber> accepted)
    {
        if (accepted.Count < 2)
        {
            return false;
        }

        var seenArabic = false;
        int? romanOffset = null;
        int? arabicOffset = null;
        foreach (var (pageNumber, printed) in accepted.OrderBy(item => item.Key))
        {
            var offset = printed.Value - pageNumber;
            if (printed.IsRoman)
            {
                if (seenArabic || (romanOffset.HasValue && romanOffset.Value != offset))
                {
                    return false;
                }

                romanOffset = offset;
            }
            else
            {
                if (arabicOffset.HasValue && arabicOffset.Value != offset)
                {
                    return false;
                }

                seenArabic = true;
                arabicOffset = offset;
            }
        }

        return true;
    }

    private static bool IsTrimmed(char character) =>
        char.IsWhiteSpace(character)
        || character is '-' or '–' or '—' or '.' or '(' or ')' or '[' or ']';

    private static ParsePage WithoutLabel(ParsePage page) =>
        page with { Label = null, LabelSource = null };
}

/// <summary>A printed page number accepted from a <c>page-number</c> Block.</summary>
public sealed record PrintedPageNumber(string Text, int Value, bool IsRoman);
