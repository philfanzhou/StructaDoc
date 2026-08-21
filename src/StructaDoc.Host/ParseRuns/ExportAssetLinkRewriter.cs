using System.Text;
using System.Text.RegularExpressions;
using StructaDoc.Application.ParseRuns;

namespace StructaDoc.Host.ParseRuns;

/// <summary>
/// Maps Provider-relative image links in normalized Markdown onto export-local resources.
/// </summary>
/// <remarks>
/// Providers emit Markdown that points at their own archive layout, typically
/// <c>images/&lt;name&gt;</c>. Those paths resolve to nothing once the Markdown leaves the archive,
/// so exports must translate them. Matching is by file name because the canonical Asset display
/// name is the archive entry's final segment. A file name shared by more than one Asset is
/// ambiguous and is left untouched rather than guessed, and an unmatched link is preserved so an
/// export never silently drops a reference it could not resolve.
/// </remarks>
public static partial class ExportAssetLinkRewriter
{
    public static IReadOnlyDictionary<string, ParseAssetRecord> BuildAssetsByFileName(
        IReadOnlyList<ParseAssetRecord> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);

        var byName = new Dictionary<string, ParseAssetRecord>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in assets)
        {
            var name = Path.GetFileName(asset.Name);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (!byName.TryAdd(name, asset))
            {
                ambiguous.Add(name);
            }
        }

        foreach (var name in ambiguous)
        {
            byName.Remove(name);
        }

        return byName;
    }

    /// <summary>
    /// Rewrites every resolvable image link using <paramref name="resolve"/>. Returning
    /// <see langword="null"/> from the resolver keeps the original link.
    /// </summary>
    public static string Rewrite(
        string markdown,
        IReadOnlyDictionary<string, ParseAssetRecord> assetsByFileName,
        Func<ParseAssetRecord, string?> resolve)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(assetsByFileName);
        ArgumentNullException.ThrowIfNull(resolve);

        if (assetsByFileName.Count == 0)
        {
            return markdown;
        }

        var rewritten = MarkdownImagePattern().Replace(
            markdown,
            match => ReplaceGroup(match, "url", assetsByFileName, resolve));
        return HtmlImagePattern().Replace(
            rewritten,
            match => ReplaceGroup(match, "url", assetsByFileName, resolve));
    }

    /// <summary>
    /// Removes every image source that was not embedded as a data URI. HTML previews and exports
    /// must not turn Provider-authored Markdown into browser requests to external or internal hosts.
    /// </summary>
    public static string RemoveNonEmbeddedImages(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var sanitized = MarkdownImagePattern().Replace(markdown, RemoveNonDataTarget);
        return HtmlImagePattern().Replace(sanitized, RemoveNonDataTarget);
    }

    public static string RewriteSegmentImages(
        string markdown,
        IReadOnlyDictionary<string, ParseAssetRecord> assetsByFileName,
        int segmentIndex) => Rewrite(
            markdown,
            assetsByFileName,
            asset => EncodeUriPath($"segment-{segmentIndex:D4}-{asset.Name}"));

    private static string ReplaceGroup(
        Match match,
        string groupName,
        IReadOnlyDictionary<string, ParseAssetRecord> assetsByFileName,
        Func<ParseAssetRecord, string?> resolve)
    {
        var group = match.Groups[groupName];
        if (!group.Success || !TryResolveFileName(group.Value, out var fileName))
        {
            return match.Value;
        }

        if (!assetsByFileName.TryGetValue(fileName, out var asset))
        {
            return match.Value;
        }

        var replacement = resolve(asset);
        if (replacement is null)
        {
            return match.Value;
        }

        return string.Concat(
            match.Value.AsSpan(0, group.Index - match.Index),
            replacement,
            match.Value.AsSpan(group.Index - match.Index + group.Length));
    }

    private static string RemoveNonDataTarget(Match match)
    {
        var group = match.Groups["url"];
        if (!group.Success
            || group.Value.Trim().StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return match.Value;
        }

        return string.Concat(
            match.Value.AsSpan(0, group.Index - match.Index),
            match.Value.AsSpan(group.Index - match.Index + group.Length));
    }

    private static bool TryResolveFileName(string url, out string fileName)
    {
        fileName = string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var value = url.Trim();

        // An absolute or protocol-relative target belongs to someone else and is never rewritten.
        if (value.StartsWith("//", StringComparison.Ordinal)
            || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || Uri.IsWellFormedUriString(value, UriKind.Absolute))
        {
            return false;
        }

        var end = value.AsSpan().IndexOfAny('?', '#');
        if (end >= 0)
        {
            value = value[..end];
        }

        var separator = value.LastIndexOfAny(['/', '\\']);
        if (separator >= 0)
        {
            value = value[(separator + 1)..];
        }

        if (value.Length == 0)
        {
            return false;
        }

        try
        {
            value = Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return false;
        }

        fileName = value;
        return fileName.Length > 0;
    }

    /// <summary>
    /// Builds a data URI that is safe in both Markdown link syntax and an HTML attribute without
    /// further escaping: base64 output excludes the characters that terminate either context, and
    /// the media type is restricted to token characters rather than trusted verbatim.
    /// </summary>
    public static string ToDataUri(string mediaType, ReadOnlySpan<byte> content) =>
        $"data:{SanitizeMediaType(mediaType)};base64,{Convert.ToBase64String(content)}";

    public static string EncodeUriPath(string value) =>
        string.Join('/', value.Split('/').Select(Uri.EscapeDataString));

    private static string SanitizeMediaType(string mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return FallbackMediaType;
        }

        var builder = new StringBuilder(mediaType.Length);
        foreach (var character in mediaType)
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '/' or '+' or '-' or '.')
            {
                builder.Append(character);
            }
            else
            {
                return FallbackMediaType;
            }
        }

        return builder.Length == 0 ? FallbackMediaType : builder.ToString();
    }

    private const string FallbackMediaType = "application/octet-stream";

    [GeneratedRegex(
        @"!\[[^\]]*\]\(\s*(?<url>[^)\s]+)(?<tail>(?:\s+(?:""[^""]*""|'[^']*'|\([^)]*\)))?\s*)\)",
        RegexOptions.None,
        matchTimeoutMilliseconds: 5000)]
    private static partial Regex MarkdownImagePattern();

    [GeneratedRegex(
        @"<img\b[^>]*?\bsrc\s*=\s*(?:""(?<url>[^""]*)""|'(?<url>[^']*)')",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 5000)]
    private static partial Regex HtmlImagePattern();
}
