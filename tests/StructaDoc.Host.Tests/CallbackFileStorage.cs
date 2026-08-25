using StructaDoc.Application.Storage;

namespace StructaDoc.Host.Tests;

internal enum FileStorageOperationKind
{
    Write,
    OpenRead,
    Delete,
}

internal sealed record FileStorageOperation(
    FileStorageOperationKind Kind,
    string StorageRef);

internal sealed class CallbackFileStorage(
    IFileStorage inner,
    Action<FileStorageOperation> beforeOperation) : IFileStorage
{
    public Task<StoredFile> WriteAsync(
        string storageRef,
        Stream content,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        beforeOperation(new FileStorageOperation(FileStorageOperationKind.Write, storageRef));
        return inner.WriteAsync(storageRef, content, maxBytes, cancellationToken);
    }

    public Task<Stream> OpenReadAsync(
        string storageRef,
        CancellationToken cancellationToken = default)
    {
        beforeOperation(new FileStorageOperation(FileStorageOperationKind.OpenRead, storageRef));
        return inner.OpenReadAsync(storageRef, cancellationToken);
    }

    public Task DeleteIfExistsAsync(
        string storageRef,
        CancellationToken cancellationToken = default)
    {
        beforeOperation(new FileStorageOperation(FileStorageOperationKind.Delete, storageRef));
        return inner.DeleteIfExistsAsync(storageRef, cancellationToken);
    }
}
