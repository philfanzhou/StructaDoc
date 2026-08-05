namespace StructaDoc.Application.Providers;

public sealed class ProviderResultContent(
    Stream content,
    string mediaType,
    string? fileName = null) : IAsyncDisposable
{
    public Stream Content { get; } = ValidateContent(content);

    public string MediaType { get; } = !string.IsNullOrWhiteSpace(mediaType)
        ? mediaType
        : throw new ArgumentException("A result media type is required.", nameof(mediaType));

    public string? FileName { get; } = ValidateFileName(fileName);

    public ValueTask DisposeAsync() => Content.DisposeAsync();

    private static Stream ValidateContent(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!content.CanRead)
        {
            throw new ArgumentException("The Provider result stream must be readable.", nameof(content));
        }

        return content;
    }

    private static string? ValidateFileName(string? fileName)
    {
        if (fileName is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Length > 512
            || fileName is "." or ".."
            || !string.Equals(fileName, fileName.Trim(), StringComparison.Ordinal)
            || fileName.Any(char.IsControl)
            || fileName.Contains('/', StringComparison.Ordinal)
            || fileName.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The Provider result file name must be a single safe path segment.",
                nameof(fileName));
        }

        return fileName;
    }
}
