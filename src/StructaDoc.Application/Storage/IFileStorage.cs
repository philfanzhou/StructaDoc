namespace StructaDoc.Application.Storage;

public interface IFileStorage
{
    Task<StoredFile> WriteAsync(
        string storageRef,
        Stream content,
        long maxBytes,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(
        string storageRef,
        CancellationToken cancellationToken = default);

    Task DeleteIfExistsAsync(
        string storageRef,
        CancellationToken cancellationToken = default);
}
