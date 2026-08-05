using System.Globalization;
using System.Text;
using StructaDoc.Application.Documents;

namespace StructaDoc.Host.Documents;

internal static class DocumentCursorCodec
{
    private const int MaximumEncodedLength = 128;

    public static string Encode(DocumentCursor cursor)
    {
        var value = string.Create(
            CultureInfo.InvariantCulture,
            $"{cursor.CreatedAtUtc.Ticks}.{cursor.Id:N}");
        return Convert.ToBase64String(Encoding.ASCII.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryDecode(string? value, out DocumentCursor? cursor)
    {
        cursor = null;

        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumEncodedLength
            || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            return false;
        }

        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
            var decoded = Encoding.ASCII.GetString(Convert.FromBase64String(base64));
            var separator = decoded.IndexOf('.', StringComparison.Ordinal);

            if (separator <= 0
                || decoded.IndexOf('.', separator + 1) >= 0
                || !long.TryParse(
                    decoded.AsSpan(0, separator),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var ticks)
                || !Guid.TryParseExact(decoded[(separator + 1)..], "N", out var id)
                || id == Guid.Empty)
            {
                return false;
            }

            cursor = new DocumentCursor(new DateTime(ticks, DateTimeKind.Utc), id);
            return true;
        }
        catch (Exception exception) when (
            exception is FormatException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
