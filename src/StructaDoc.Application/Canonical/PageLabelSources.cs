namespace StructaDoc.Application.Canonical;

/// <summary>
/// Where a Page's printed label came from. A Page has either both a label and one of these values,
/// or neither.
/// </summary>
public static class PageLabelSources
{
    /// <summary>The <c>/PageLabels</c> number tree of the PDF submitted to the Provider.</summary>
    public const string PdfPageLabels = "pdf-page-labels";

    /// <summary>The text of the run's own <c>page-number</c> Blocks, accepted only when consistent.</summary>
    public const string PageNumberBlocks = "page-number-blocks";

    public const int MaxLabelCharacters = 256;

    public static bool IsKnown(string? value) =>
        value is PdfPageLabels or PageNumberBlocks;

    /// <summary>
    /// A label must be printable display text: not empty or only whitespace, at most
    /// <see cref="MaxLabelCharacters"/> UTF-16 characters, no control characters, and no unpaired
    /// surrogate.
    /// </summary>
    public static bool IsValidLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxLabelCharacters)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsControl(character))
            {
                return false;
            }

            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }

                index++;
            }
            else if (char.IsLowSurrogate(character))
            {
                return false;
            }
        }

        return true;
    }
}
