namespace StructaDoc.Application.Storage;

public sealed class ObjectConflictException(string storageRef)
    : Exception("A different object already exists at the requested storage reference.")
{
    public string StorageRef { get; } = storageRef;
}
