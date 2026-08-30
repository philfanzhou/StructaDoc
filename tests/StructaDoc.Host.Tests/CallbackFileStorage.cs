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
    string StorageRef,
    CancellationToken CancellationToken);

internal sealed class CallbackFileStorage(
    IFileStorage inner,
    Action<FileStorageOperation> beforeOperation,
    Action<FileStorageOperation>? afterOperation = null) : IFileStorage
{
    public async Task<StoredFile> WriteAsync(
        string storageRef,
        Stream content,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        var operation = new FileStorageOperation(
            FileStorageOperationKind.Write,
            storageRef,
            cancellationToken);
        beforeOperation(operation);
        var stored = await inner.WriteAsync(storageRef, content, maxBytes, cancellationToken);
        afterOperation?.Invoke(operation);
        return stored;
    }

    public async Task<Stream> OpenReadAsync(
        string storageRef,
        CancellationToken cancellationToken = default)
    {
        var operation = new FileStorageOperation(
            FileStorageOperationKind.OpenRead,
            storageRef,
            cancellationToken);
        beforeOperation(operation);
        var content = await inner.OpenReadAsync(storageRef, cancellationToken);
        afterOperation?.Invoke(operation);
        return content;
    }

    public async Task DeleteIfExistsAsync(
        string storageRef,
        CancellationToken cancellationToken = default)
    {
        var operation = new FileStorageOperation(
            FileStorageOperationKind.Delete,
            storageRef,
            cancellationToken);
        beforeOperation(operation);
        await inner.DeleteIfExistsAsync(storageRef, cancellationToken);
        afterOperation?.Invoke(operation);
    }
}
