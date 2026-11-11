using System.Text;
using System.Text.Json;
using StructaDoc.Application.Providers;

namespace StructaDoc.Adapters.Providers;

/// <summary>
/// The MinerU Cloud image text-recognition adapter (ADR-0015): the same non-archiving cloud
/// exchange the layout mode uses (ADR-0014 point 3), with the cloud's recognized content kept
/// instead of discarded.
///
/// It reduces the cloud's <c>layout.json</c> (its middle JSON) to blocks in reading order. Each
/// main block becomes one block with a Canonical Model type, a normalized box mapped back to the
/// stored frame, and its contents: text-bearing blocks carry their lines (the span text of each
/// line joined) and the lines joined as plain content, tables carry their recognized HTML, and
/// formulas their LaTeX. Figures carry geometry only. Nested <c>lines</c>/<c>spans</c> entries and
/// nested child blocks (a table's body and caption, a list's items) are reduced into the block
/// they belong to and are never reported as peer blocks.
///
/// The submission switches ask for the full recognition the answer needs (<c>is_ocr</c>,
/// <c>enable_table</c>, <c>enable_formula</c> all true). Recognition quality and reading order are
/// what the cloud produces; nothing here corrects, detects language, or interprets the text.
/// </summary>
public sealed class MinerUCloudImageRecognitionProvider : IImageRecognitionProvider
{
    /// <summary>How deep nested child blocks are followed. The observed shapes nest one level.</summary>
    private const int MaximumNestingDepth = 4;

    private static readonly MinerUCloudExchangeMode Mode = new(
        "recognition",
        IImageRecognitionProviderResolver.UnsupportedCapabilityErrorCode,
        "Configure an enabled default MinerU Cloud Provider with its credential.",
        IsOcr: true,
        EnableTable: true,
        EnableFormula: true);

    private readonly MinerUCloudImageExchange exchange;

    /// <param name="limiter">
    /// The Host's shared cloud exchange bound, the same one the layout mode holds. A provider built
    /// without one (a standalone use, such as a test) gets a private bound of the same size.
    /// </param>
    public MinerUCloudImageRecognitionProvider(
        HttpClient providerApiClient,
        HttpClient signedTransferClient,
        ILayoutCloudConfigurationSource configurationSource,
        TimeSpan? deadline = null,
        MinerUCloudExchangeLimiter? limiter = null)
    {
        exchange = new MinerUCloudImageExchange(
            providerApiClient,
            signedTransferClient,
            configurationSource,
            limiter ?? new MinerUCloudExchangeLimiter(),
            deadline,
            Mode);
    }

    public Task<ImageRecognitionResult> RecognizeAsync(
        LayoutDetectionRequest request,
        CancellationToken cancellationToken = default) =>
        exchange.RunAsync(request, (page, cloud) => BuildResult(page, request, cloud), cancellationToken);

    private static ImageRecognitionResult BuildResult(
        MinerUCloudLayoutPage page,
        LayoutDetectionRequest request,
        ProviderExecutionConfiguration cloud)
    {
        var blocks = new List<ImageRecognitionBlock>();
        var lineBudget = new LineBudget(page);

        // `para_blocks` is MinerU's final, reading-ordered block list; `preproc_blocks` is the
        // spelling the observed single-image cloud answer carries, already in its reading order.
        // Discarded blocks (headers and footers the backend set aside) are not read, exactly as in
        // the layout mode.
        if (page.TryGetBlocks(out var mainBlocks, "para_blocks", "preproc_blocks"))
        {
            var index = 0;
            foreach (var block in mainBlocks.EnumerateArray())
            {
                blocks.Add(ReduceBlock(page, block, $"the block at index {index}", lineBudget));
                index++;
            }
        }

        try
        {
            return new ImageRecognitionResult(
                blocks,
                request.StoredWidth!.Value,
                request.StoredHeight!.Value,
                engine: ProviderTypes.MinerUCloud,
                model: cloud.Model ?? ProviderTypeDescriptors.MinerUCloudDefaultModel,
                version: page.Version);
        }
        catch (ArgumentException exception)
        {
            throw page.Invalid(exception.Message);
        }
    }

    private static ImageRecognitionBlock ReduceBlock(
        MinerUCloudLayoutPage page,
        JsonElement block,
        string where,
        LineBudget lineBudget)
    {
        var type = MapType(ReadType(page, block, where));
        var (x0, y0, x1, y1) = page.ReadStoredBox(block, where);
        var contents = new BlockContents();
        Collect(page, block, where, contents, ContentRole.Body, depth: 0, lineBudget);

        string? content;
        string? contentFormat;
        IReadOnlyList<ImageRecognitionLine> lines;
        switch (type)
        {
            case "table" when contents.Html is not null:
                content = contents.Html;
                contentFormat = ImageRecognitionBlock.HtmlFormat;
                lines = [];
                break;

            case "formula" when contents.Latex is not null:
                content = contents.Latex;
                contentFormat = ImageRecognitionBlock.LatexFormat;
                lines = [];
                break;

            case "image":
                // Figures carry geometry only: no cropped asset, no download URL, and no text the
                // backend may have read inside the picture.
                content = null;
                contentFormat = null;
                lines = [];
                break;

            default:
                lines = contents.BodyLines;
                content = lines.Count == 0 ? null : string.Join('\n', lines.Select(line => line.Text));
                contentFormat = content is null ? null : ImageRecognitionBlock.PlainFormat;
                break;
        }

        try
        {
            return new ImageRecognitionBlock(
                type,
                content,
                contentFormat,
                new ImageRecognitionBox(x0, y0, x1, y1),
                lines,
                JoinOrNull(contents.CaptionLines),
                JoinOrNull(contents.FootnoteLines));
        }
        catch (ArgumentException)
        {
            throw page.Invalid($"{where} has content outside the recognition contract.");
        }
    }

    /// <summary>
    /// Reads a block's own lines and its nested child blocks into <paramref name="contents"/>. A
    /// child whose type names a caption or a footnote feeds that slot; every other child (a
    /// table's or figure's body, a list's items) is part of the block's body.
    /// </summary>
    private static void Collect(
        MinerUCloudLayoutPage page,
        JsonElement element,
        string where,
        BlockContents contents,
        ContentRole role,
        int depth,
        LineBudget lineBudget)
    {
        if (element.TryGetProperty("lines", out var lines))
        {
            if (lines.ValueKind != JsonValueKind.Array)
            {
                throw page.Invalid($"{where} has 'lines' that is not an array.");
            }

            var lineIndex = 0;
            foreach (var line in lines.EnumerateArray())
            {
                ReadLine(page, line, $"{where}, line {lineIndex}", contents, role, lineBudget);
                lineIndex++;
            }
        }

        if (!element.TryGetProperty("blocks", out var children))
        {
            return;
        }

        if (children.ValueKind != JsonValueKind.Array)
        {
            throw page.Invalid($"{where} has 'blocks' that is not an array.");
        }

        if (depth >= MaximumNestingDepth)
        {
            throw page.Invalid($"{where} nests blocks deeper than {MaximumNestingDepth} levels.");
        }

        var childIndex = 0;
        foreach (var child in children.EnumerateArray())
        {
            var childWhere = $"{where}, nested block {childIndex}";
            var childType = NormalizeToken(ReadType(page, child, childWhere));
            var childRole = childType.EndsWith("caption", StringComparison.Ordinal)
                ? ContentRole.Caption
                : childType.EndsWith("footnote", StringComparison.Ordinal)
                    ? ContentRole.Footnote
                    : role;
            Collect(page, child, childWhere, contents, childRole, depth + 1, lineBudget);
            childIndex++;
        }
    }

    private static void ReadLine(
        MinerUCloudLayoutPage page,
        JsonElement line,
        string where,
        BlockContents contents,
        ContentRole role,
        LineBudget lineBudget)
    {
        if (line.ValueKind != JsonValueKind.Object)
        {
            throw page.Invalid($"{where} is not an object.");
        }

        var pieces = new List<string>();
        if (line.TryGetProperty("spans", out var spans))
        {
            if (spans.ValueKind != JsonValueKind.Array)
            {
                throw page.Invalid($"{where} has 'spans' that is not an array.");
            }

            var spanIndex = 0;
            foreach (var span in spans.EnumerateArray())
            {
                ReadSpan(page, span, $"{where}, span {spanIndex}", contents, pieces);
                spanIndex++;
            }
        }

        var text = JoinSpanText(pieces);
        if (text.Length == 0)
        {
            // A line of pictures, a table body, or an equation carries no line text; its content,
            // if any, was taken by the span reader above.
            return;
        }

        var (x0, y0, x1, y1) = page.ReadStoredBox(line, where);
        lineBudget.Take();
        var recognized = new ImageRecognitionLine(text, new ImageRecognitionBox(x0, y0, x1, y1));
        switch (role)
        {
            case ContentRole.Caption:
                contents.CaptionLines.Add(recognized);
                break;
            case ContentRole.Footnote:
                contents.FootnoteLines.Add(recognized);
                break;
            default:
                contents.BodyLines.Add(recognized);
                break;
        }
    }

    private static void ReadSpan(
        MinerUCloudLayoutPage page,
        JsonElement span,
        string where,
        BlockContents contents,
        List<string> pieces)
    {
        var spanType = NormalizeToken(ReadType(page, span, where));
        switch (spanType)
        {
            case "image":
                return;

            case "table":
                contents.Html ??= ReadOptionalText(page, span, "html", where);
                return;

            case "interline-equation":
                contents.Latex ??= ReadOptionalText(page, span, "content", where)
                    ?? ReadOptionalText(page, span, "latex", where);
                return;

            case "inline-equation":
                if (ReadOptionalText(page, span, "content", where) is { } inline)
                {
                    pieces.Add($"${inline.Trim()}$");
                }

                return;

            default:
                if (ReadOptionalText(page, span, "content", where) is { } text)
                {
                    pieces.Add(text);
                }

                return;
        }
    }

    private static string ReadType(MinerUCloudLayoutPage page, JsonElement element, string where)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(type.GetString()))
        {
            throw page.Invalid($"{where} has no type.");
        }

        return type.GetString()!;
    }

    /// <summary>
    /// A text field that may be absent or null; present with any other kind is malformed output.
    /// Blank text counts as absent.
    /// </summary>
    private static string? ReadOptionalText(
        MinerUCloudLayoutPage page,
        JsonElement element,
        string name,
        string where)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw page.Invalid($"{where} has a '{name}' that is not text.");
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// Joins a line's span texts with one space between them — the separate words of a printed
    /// row, such as a word, its part of speech, and its definition — except where no space belongs:
    /// between two CJK characters, before closing punctuation, around full-width punctuation, or
    /// where a span already brings its own whitespace.
    /// </summary>
    private static string JoinSpanText(List<string> pieces)
    {
        var builder = new StringBuilder();
        foreach (var piece in pieces)
        {
            if (builder.Length > 0
                && piece.Length > 0
                && NeedsSeparator(builder[^1], piece[0]))
            {
                builder.Append(' ');
            }

            builder.Append(piece);
        }

        return builder.ToString().Trim();
    }

    private static bool NeedsSeparator(char previous, char next) =>
        !char.IsWhiteSpace(previous)
        && !char.IsWhiteSpace(next)
        && !(IsCjk(previous) && IsCjk(next))
        && !IsFullWidthPunctuation(previous)
        && !IsFullWidthPunctuation(next)
        && next is not (',' or '.' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '%');

    private static bool IsCjk(char character) =>
        character is >= '⺀' and <= '鿿'
            or >= '가' and <= '힯'
            or >= '豈' and <= '﫿'
            or >= '＀' and <= '￯';

    private static bool IsFullWidthPunctuation(char character) =>
        character is >= '　' and <= '〿'
            or >= '＀' and <= '／'
            or >= '：' and <= '＠'
            or >= '［' and <= '｀'
            or >= '｛' and <= '･';

    private static string? JoinOrNull(List<ImageRecognitionLine> lines) =>
        lines.Count == 0 ? null : string.Join('\n', lines.Select(line => line.Text));

    private static string NormalizeToken(string value) =>
        value.Trim().ToLowerInvariant().Replace('_', '-');

    /// <summary>
    /// The Canonical Model's registered Block types, mapped the same way the result normalizer maps
    /// MinerU tokens. A type outside the mapped set is <c>unknown</c>, and keeps its text.
    /// </summary>
    private static string MapType(string providerType) => NormalizeToken(providerType) switch
    {
        "title" or "heading" => "title",
        "text" or "paragraph" => "text",
        "list" or "list-item" => "list",
        "table" => "table",
        "equation" or "formula" or "inline-equation" or "interline-equation" => "formula",
        "image" or "figure" or "chart" => "image",
        "code" or "algorithm" => "code",
        "header" or "page-header" => "header",
        "footer" or "page-footer" => "footer",
        "footnote" or "page-footnote" => "footnote",
        "page-number" => "page-number",
        _ => "unknown",
    };

    private enum ContentRole
    {
        Body,
        Caption,
        Footnote,
    }

    private sealed class BlockContents
    {
        public List<ImageRecognitionLine> BodyLines { get; } = [];

        public List<ImageRecognitionLine> CaptionLines { get; } = [];

        public List<ImageRecognitionLine> FootnoteLines { get; } = [];

        public string? Html { get; set; }

        public string? Latex { get; set; }
    }

    /// <summary>The result-wide line bound, enforced while reading rather than after.</summary>
    private sealed class LineBudget(MinerUCloudLayoutPage page)
    {
        private int taken;

        public void Take()
        {
            if (++taken > ImageRecognitionResult.MaximumLineCount)
            {
                throw page.Invalid(
                    $"the page holds more than {ImageRecognitionResult.MaximumLineCount} recognized lines.");
            }
        }
    }
}
