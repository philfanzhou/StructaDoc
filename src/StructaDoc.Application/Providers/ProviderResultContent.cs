namespace StructaDoc.Application.Providers;

public sealed class ProviderResultContent(Stream content, string mediaType) : IAsyncDisposable
{
    public Stream Content { get; } = ValidateContent(content);

    public string MediaType { get; } = !string.IsNullOrWhiteSpace(mediaType)
        ? mediaType
        : throw new ArgumentException("A result media type is required.", nameof(mediaType));

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
}
